using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using ClassIsland.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Shared.Helpers;

/// <summary>
/// 配置文件工具集
/// </summary>
public class ConfigureFileHelper
{
    private static ILogger? _logger;

    private static ILogger? Logger
    {
        get
        {
#if NET8_0_OR_GREATER
            return _logger ??= IAppHost.Host?.Services.GetService<ILogger<ConfigureFileHelper>>();

#else
            return null;
#endif
        }
    }

    internal static JsonSerializerOptions SerializerOptions { get; set; } = new();

    private static Lazy<JsonSerializerOptions> WriteIndentedSerializerOptions { get; } =
        new(() => new JsonSerializerOptions(SerializerOptions)
        {
            WriteIndented = true
        });

    /// <summary>
    /// 配置在默认情况下，是否启用配置文件备份
    /// </summary>
    public static bool IsBackupEnabled { get; set; } = true;

    /// <summary>
    /// 加载配置文件时发生的错误
    /// </summary>
    public static ObservableCollection<ConfigError> Errors { get; } = [];

    static ConfigureFileHelper()
    {
        // 覆盖默认的 JsonSerializerOptions
        var field = typeof(JsonSerializerOptions).GetField("<Default>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? typeof(JsonSerializerOptions).GetField("s_defaultOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(null, SerializerOptions);
    }

    /// <summary>
    /// 加载配置文件，并自动创建备份。当加载失败时，将尝试加载备份的配置文件。如果希望在发生加载异常时自动捕获错误，请使用方法 <see cref="LoadConfig{T}"/>。
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="backupEnabled">是否启用备份。如果没有填写此参数，将使用默认值。</param>
    /// <param name="isLoadingBackup">是否正在加载备份。如果是，在加载失败时将不会尝试加载备份。</param>
    /// <returns>加载的配置文件对象</returns>
    public static T LoadConfigUnWrapped<T>(string path, bool? backupEnabled = null, bool isLoadingBackup = false)
    {
        backupEnabled ??= IsBackupEnabled;
        Logger?.LogInformation("读取 JSON 文件：{}", path);
        if (!File.Exists(path))
        {
            var cfg = Activator.CreateInstance<T>();
            if (!isLoadingBackup)
                SaveConfig(path, cfg);
            return cfg;
        }

        var backupPath = path + ".bak";
        T? result;
        try
        {
            // 这个 try 只包「读取 + 解析」。生成 .bak 是另一回事：磁盘满、备份文件被占用
            // 都会让 File.Copy 抛异常，但那恰恰说明配置本身是完好的。
            // 把两者放在一起会把一份完好的配置误判成损坏，进而用旧备份覆盖用户当前的设置。
            var json = File.ReadAllText(path);
            result = JsonSerializer.Deserialize<T>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            // 解析失败时必须先把原始文件留一份。调用方（尤其是 SettingsService）
            // 拿到默认对象后会立刻保存，把整份配置覆盖成默认值；
            // 而这里的 .bak 只在「解析成功」之后才生成，损坏的文件此前根本没有备份，
            // 一次异常就会让用户的主题、课表、插件配置引用永久消失。
            PreserveCorruptFile(path);
            if (!backupEnabled.Value || !File.Exists(backupPath))
                throw;
            Logger?.LogWarning(ex, "读取 JSON 文件 {} 失败，尝试加载备份", path);
            Errors.Add(new ConfigError(path, 0, false, ex));
            var backup = LoadConfig<T>(backupPath, false, true);
            try
            {
                File.Copy(backupPath, path, true);
            }
            catch (Exception restoreEx)
            {
                // 恢复失败不该连累「已经成功读出的备份内容」。
                Logger?.LogWarning(restoreEx, "用备份覆盖损坏的配置文件 {} 失败", path);
            }
            return backup;
        }

        if (result == null)
            return Activator.CreateInstance<T>();

        if (backupEnabled.Value)
        {
            try
            {
                File.Copy(path, backupPath, true);
            }
            catch (Exception ex)
            {
                // 备份失败不能影响这次已经成功的读取。
                Logger?.LogWarning(ex, "备份配置文件 {} 失败", path);
            }
        }

        return result;
    }

    /// <summary>
    /// 把无法解析的配置文件复制一份到 <c>.corrupt</c>，供用户/支持人员手工恢复。
    /// 复制失败不能影响原有的加载失败处理流程。
    /// </summary>
    private static void PreserveCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.Copy(path, path + ".corrupt", true);
            Logger?.LogWarning("已将无法解析的配置文件备份到 {}", path + ".corrupt");
        }
        catch
        {
            // 备份失败时仍然要按原逻辑继续（抛异常或回退默认值）。
        }
    }

    /// <summary>
    /// 从流中加载配置文件。流的生命周期由调用方管理。
    /// </summary>
    public static T LoadConfigUnWrapped<T>(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? Activator.CreateInstance<T>();
    }

    /// <summary>
    /// 加载配置文件，并自动创建备份。当加载失败时，将尝试加载备份的配置文件。如果备份文件也加载失败，会返回新的对象。如果希望在发生加载异常时不自动捕获错误，请使用方法 <see cref="LoadConfigUnWrapped{T}"/>。
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="backupEnabled">是否启用备份。如果没有填写此参数，将使用默认值。</param>
    /// <param name="isLoadingBackup">是否正在加载备份。如果是，在加载失败时将不会尝试加载备份。</param>
    /// <returns>加载的配置文件对象</returns>
    public static T LoadConfig<T>(string path, bool? backupEnabled=null, bool isLoadingBackup=false)
    {
        try
        {
            return LoadConfigUnWrapped<T>(path, backupEnabled, isLoadingBackup);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "无法读取 JSON 文件 {}，已返回默认值", path);
            Errors.Add(new ConfigError(path, 1, true, e));
            return Activator.CreateInstance<T>();
        }
    }

    /// <summary>
    /// 从流中加载配置文件。读取失败时返回新的配置对象，流的生命周期由调用方管理。
    /// </summary>
    public static T LoadConfig<T>(Stream stream)
    {
        try
        {
            return LoadConfigUnWrapped<T>(stream);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "无法从流中读取 JSON 文件，已返回默认值");
            Errors.Add(new ConfigError("<stream>", 1, true, e));
            return Activator.CreateInstance<T>();
        }
    }

    /// <summary>
    /// 保存配置文件，并自动创建备份
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="o">要写入到配置的对象</param>
    public static void SaveConfig<T>(string path, T o)
    {
        SaveConfig(path, o, false);
    }

    /// <summary>
    /// 保存配置文件，并自动创建备份
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="o">要写入到配置的对象</param>
    /// <param name="writeIndented">是否在保存时格式化 JSON</param>
    public static void SaveConfig<T>(string path, T o, bool writeIndented)
    {
        Logger?.LogInformation("写入 JSON 文件：{}", path);
        var options = writeIndented ? WriteIndentedSerializerOptions.Value : SerializerOptions;
        // 在保存时不对备份文件进行操作，以防止在保存时发生意外断电时，备份文件也受到损坏。
        WriteAllTextSafe(path, JsonSerializer.Serialize<T>(o, options));
    }

    /// <summary>
    /// 将配置写入流。流的生命周期由调用方管理。
    /// </summary>
    public static void SaveConfig<T>(Stream stream, T o, bool writeIndented = false)
    {
        var options = writeIndented ? WriteIndentedSerializerOptions.Value : SerializerOptions;
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true);
        writer.Write(JsonSerializer.Serialize<T>(o, options));
        writer.Flush();
        stream.Flush();
    }

    /// <summary>
    /// 复制配置对象
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="o">要复制的对象</param>
    /// <returns>复制后的对象副本</returns>
    public static T CopyObject<T>(T o)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o, SerializerOptions), SerializerOptions) ?? Activator.CreateInstance<T>();
    }

    /// <summary>
    /// 合并字典。将新的字典与基准字典比较，向要操作的字典中添加新字典与基准字典相较没有的元素，并删除要操作的字典中新字典与基准字典相比没有的元素。
    /// </summary>
    /// <typeparam name="TKey">字典键类型</typeparam>
    /// <typeparam name="TValue">字典值类型</typeparam>
    /// <param name="raw">要操作的字典</param>
    /// <param name="diffBase">合并基准字典</param>
    /// <param name="diffNew">新的字典</param>
    /// <returns></returns>
    public static IDictionary<TKey, TValue> MergeDictionary<TKey, TValue>(IDictionary<TKey, TValue> raw,
        IDictionary<TKey, TValue> diffBase, IDictionary<TKey, TValue> diffNew)
    {
        var rm = (from i in diffNew.Keys where !diffBase.Keys.Contains(i) select i).ToList();
        rm.ForEach(i => raw.Remove(i));
        foreach (var i in diffNew)
        {
            if (raw.ContainsKey(i.Key))
                raw[i.Key] = i.Value;
            else
                raw.Add(i);
        }

        return raw;
    }

    private static void WriteAllTextSafe(string path, string content)
    {
        // 不能直接 FileMode.Create：它会先把目标文件截成 0 字节再逐块写入，
        // 写盘途中崩溃、断电或进程被杀，磁盘上就只剩半截 JSON。
        // 下次启动解析失败，而设置加载失败会静默回退到全默认值并立刻写回覆盖 ——
        // 用户的主题、课表、插件配置引用会在一次意外断电后全部丢失。
        // 先写同目录临时文件、落盘后再原子改名，目标文件在任何时刻都是
        // 「完整的旧版本」或「完整的新版本」，不存在中间态。
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(true);
        }

        File.Move(temp, path, true);
    }
}
