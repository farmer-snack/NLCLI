using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Core.Services.Registry;
using ClassIsland.Models.Plugins;
using ClassIsland.Services.Management;
using ClassIsland.Shared;
using ClassIsland.Shared.Protobuf.AuditEvent;
using ClassIsland.Shared.Protobuf.Enum;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClassIsland.Services;

/// <summary>
/// 插件服务
/// </summary>
public class PluginService : IPluginService
{
    public static readonly string PluginsRootPath = Path.Combine(CommonDirectories.AppRootFolderPath, "Plugins");

    public static readonly string PluginsIndexPath = Path.Combine(CommonDirectories.AppConfigPath, "PluginsIndex");

    public static readonly string PluginsPkgRootPath = Path.Combine(CommonDirectories.AppCacheFolderPath, "PluginPackages");


    public static readonly string PluginManifestFileName = "manifest.yml";

    public static readonly string PluginConfigsFolderPath = Path.Combine(CommonDirectories.AppConfigPath, "Plugins");

    public static List<PluginInfo> PluginLoadedStatus { get; internal set; } =new();

    internal static readonly Dictionary<string, PluginLoadContext> PluginLoadContexts = new();

    internal static List<PluginManifest> InstalledPlugins { get; } = [];
    
    internal static List<PluginManifest> UninstalledPlugins { get; } = [];

    /// <summary>
    /// 处理插件安装
    /// </summary>
    public static void ProcessPluginsInstall()
    {
        if (!Directory.Exists(PluginsPkgRootPath))
        {
            Directory.CreateDirectory(PluginsPkgRootPath);
        }
        if (!Directory.Exists(PluginsRootPath))
        {
            Directory.CreateDirectory(PluginsRootPath);
        }

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithTypeConverter(new OSPlatformTypeConverter_Yaml())
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        foreach (var pkgPath in Directory.EnumerateFiles(PluginsPkgRootPath).Where(x => Path.GetExtension(x) == IPluginService.PluginPackageExtension))
        {
            try
            {
                bool isEnabledBefore = true;
                using var pkg = ZipFile.OpenRead(pkgPath);
                var mf = pkg.GetEntry(PluginManifestFileName);
                if (mf == null)
                    continue;
                using var reader = new StreamReader(mf.Open());
                var mfText = reader.ReadToEnd();
                var manifest = deserializer.Deserialize<PluginManifest>(mfText);
                var targetPath = Path.Combine(PluginsRootPath, manifest.Id);
                Console.Write($"正在处理插件安装: {manifest.Name}({manifest.Id},{manifest.Version})...");
                if (Directory.Exists(targetPath))
                {
                    if(File.Exists(Path.Combine(targetPath,".disabled")))
                    {
                        isEnabledBefore = false;
                    }
                    Directory.Delete(targetPath, true);
                }
                Directory.CreateDirectory(targetPath);
                ZipFile.ExtractToDirectory(pkgPath, targetPath);
                if(!isEnabledBefore) File.WriteAllText(Path.Combine(targetPath, ".disabled"), "");
                InstalledPlugins.Add(manifest);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            File.Delete(pkgPath);
            Console.WriteLine("完成!");
        }
    }

    /// <summary>
    /// 兼容层：检查插件声明的 API 版本。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>低于 2.0.0.0 的插件（ClassIsland 1.x 时代的旧插件）运行时 API 完全不兼容，直接标记为错误；</item>
    /// <item>高于当前应用版本的插件<strong>不</strong>阻止加载，只提示——市场的插件在 API 上基本是向后兼容的，
    /// 直接拒载会让用户升级插件后完全用不了；</item>
    /// <item>没有声明或无法解析 API 版本的插件同样放行，交给实际加载过程检验。</item>
    /// </list>
    /// </remarks>
    private static void CheckApiVersionCompatibility(PluginInfo info)
    {
        var manifest = info.Manifest;
        if (!Version.TryParse(manifest.ApiVersion, out var apiVersion))
        {
            Console.WriteLine($"[插件兼容] {manifest.Name}({manifest.Id}) 未声明有效的 API 版本（\"{manifest.ApiVersion}\"），将尝试加载。");
            return;
        }

        // 兼容层：把 "2.0" 这类省略段位的写法补齐为 "2.0.0.0"，
        // 否则 Version 的默认 Build/Revision 是 -1，会被误判成低于 2.0.0.0。
        apiVersion = NormalizeVersion(apiVersion);

        if (apiVersion < new Version(2, 0, 0, 0))
        {
            info.LoadStatus = PluginLoadStatus.Error;
            info.Exception = new InvalidOperationException(
                $"不兼容的 API 版本 {apiVersion}。插件的 API 版本需要至少为 2.0.0.0 才能被当前版本的 ClassIsland 加载。");
            PluginLoadedStatus.Add(info);
            return;
        }

        if (Version.TryParse(AppBase.AppVersion, out var hostVersion) &&
            apiVersion > NormalizeVersion(hostVersion))
        {
            Console.WriteLine($"[插件兼容] {manifest.Name}({manifest.Id}) 声明的 API 版本 {apiVersion} 高于当前应用的 {hostVersion}，仍会尝试加载。");
        }
    }

    /// <summary>
    /// 把省略段位的版本号补齐为四段，缺失部分按 0 处理（<see cref="Version"/> 默认是 -1）。
    /// </summary>
    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    /// <summary>
    /// 兼容层：把插件加载过程中的底层异常翻译成用户能看懂的说明，同时保留原始异常链。
    /// </summary>
    private static Exception BuildLoadException(PluginManifest manifest, Exception ex)
    {
        string? hint = null;
        if (ex is ReflectionTypeLoadException reflectionTypeLoadException)
        {
            var loaders = reflectionTypeLoadException.LoaderExceptions
                .Where(x => x != null)
                .Select(x => x!)
                .ToArray();
            var loaderMessages = loaders
                .Select(x => x.Message)
                .Distinct()
                .ToArray();
            if (loaderMessages.Length > 0)
            {
                hint = $"插件类型加载失败：{string.Join("；", loaderMessages)}";
            }

            // 缺依赖的情况优先给出可操作的提示，覆盖上面那段英文异常。
            var missingHint = loaders.Select(MissingAssemblyHint).FirstOrDefault(x => x != null);
            if (missingHint != null)
            {
                hint = missingHint;
            }
        }

        hint ??= ex switch
        {
            FileNotFoundException or FileLoadException =>
                MissingAssemblyHint(ex) ??
                "缺少依赖程序集，或依赖的版本与当前应用不匹配。请确认插件包完整；如果插件自带 ClassIsland.Core 等核心程序集，可尝试删除后重试。",
            TypeLoadException or MissingMethodException or MissingFieldException =>
                "插件使用了当前版本中不存在的接口。请更新插件，或确认该插件与当前 ClassIsland 版本兼容。",
            BadImageFormatException =>
                "插件程序集架构不匹配（例如 32 位插件运行在 64 位进程中）。",
            _ => null
        };

        var message = $"插件 {manifest.Name}({manifest.Id}) 加载失败：{ex.Message}";
        if (!string.IsNullOrEmpty(hint))
        {
            message += $"{Environment.NewLine}可能原因：{hint}";
        }

        return new InvalidOperationException(message, ex);
    }

    /// <summary>
    /// 兼容层：把「缺少某个依赖程序集」翻译成可直接照做的建议。
    /// </summary>
    /// <remarks>
    /// 不少插件把自己的第三方依赖（如 OpenCV、Dlib 这类带原生库的包）误当作宿主会提供的框架库，
    /// 打包时没有带上。这类缺失宿主无法代为补齐，只能给出明确的处理方式，避免用户只看到一串英文异常。
    /// </remarks>
    private static string? MissingAssemblyHint(Exception ex)
    {
        var name = ex switch
        {
            FileNotFoundException fileNotFound => fileNotFound.FileName,
            FileLoadException fileLoad => fileLoad.FileName,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Split(',')[0].Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return $"缺少依赖程序集「{name}」。这个库不属于运行环境内置范围，需要由插件自己提供：" +
               $"可以把 {name}.dll（以及配套的原生库）放进插件目录后重试，或联系插件作者更新打包方式。";
    }

    /// <summary>
    /// 初始化插件
    /// </summary>
    public static void InitializePlugins(HostBuilderContext context, IServiceCollection services)
    {
        if (!Directory.Exists(PluginsRootPath))
        {
            Directory.CreateDirectory(PluginsRootPath);
        }

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithTypeConverter(new OSPlatformTypeConverter_Yaml())
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var pluginDirs = Directory.EnumerateDirectories(PluginsRootPath)
            .Append(App.ApplicationCommand.ExternalPluginPath);
        // 预处理插件信息
        foreach (var pluginDir in pluginDirs)
        {
            if (string.IsNullOrWhiteSpace(pluginDir))
                continue;
            var manifestPath = Path.Combine(pluginDir, PluginManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var manifestYaml = File.ReadAllText(manifestPath);
            var manifest = deserializer.Deserialize<PluginManifest?>(manifestYaml);
            if (manifest == null)
            {
                continue;
            }
            var info = new PluginInfo
            {
                Manifest = manifest,
                IsLocal = true,
                PluginFolderPath = Path.GetFullPath(pluginDir),
                RealIconPath = Path.Combine(Path.GetFullPath(pluginDir), manifest.Icon)
            };
            if (info.IsUninstalling)
            {
                Console.Write($"正在处理插件卸载: {manifest.Name}({manifest.Id},{manifest.Version})...");
                Directory.Delete(pluginDir, true);
                UninstalledPlugins.Add(manifest);
                Console.WriteLine("完成!");
                continue;
            }
            if (IPluginService.LoadedPluginsIds.Contains(manifest.Id))
                continue;
            IPluginService.LoadedPluginsIds.Add(manifest.Id);
            IPluginService.LoadedPluginsInternal.Add(info);
            if (!info.IsEnabled)
            {
                info.LoadStatus = PluginLoadStatus.Disabled;
                PluginLoadedStatus.Add(info);
            }
            if (info.IsEnabled)
            {
                CheckApiVersionCompatibility(info);
            }
        }
        var loadOrder = ResolveLoadOrder(IPluginService.LoadedPluginsInternal.Where(x => x.LoadStatus == PluginLoadStatus.NotLoaded).ToList());
        Console.WriteLine($"Resolved load order: {string.Join(", ", loadOrder)}");

        var forceMonoPluginLoadBehavior =
            Environment.GetEnvironmentVariable("ClassIsland_DebugForceMonoPluginLoadBehavior") == "1" ||
            Environment.GetEnvironmentVariable("ClassIsland_DebugSuppressMacOSPluginLoadBehavior") == "1";
        // 加载插件
        foreach (var id in loadOrder)
        {
            var info = IPluginService.LoadedPluginsInternal.First(x => x.Manifest.Id == id);
            var manifest = info.Manifest;
            var pluginDir = info.PluginFolderPath;
            try
            {
                var fullPath = ResolveEntranceAssemblyPath(pluginDir, manifest.EntranceAssembly);
                var loadContext = new PluginLoadContext(info, fullPath, forceMonoPluginLoadBehavior);
                PluginLoadContexts[info.Manifest.Id] = loadContext;
                var asm = loadContext.LoadFromAssemblyName(
                    new AssemblyName(Path.GetFileNameWithoutExtension(fullPath)));
                var entrance = asm.ExportedTypes.FirstOrDefault(x =>
                    x.BaseType == typeof(PluginBase) ||
                    x.GetCustomAttributes().FirstOrDefault(a => a.GetType() == typeof(PluginEntrance)) != null);

                if (entrance == null)
                {
                    continue;
                }

                if (Activator.CreateInstance(entrance) is not PluginBase entranceObj)
                {
                    continue;
                }

                entranceObj.PluginConfigFolder = Path.Combine(PluginConfigsFolderPath, manifest.Id);
                if (!Directory.Exists(entranceObj.PluginConfigFolder))
                    Directory.CreateDirectory(entranceObj.PluginConfigFolder);
                entranceObj.Info = info;
                // 在插件 Initialize 期间设置当前注册插件上下文，以便组件注册时自动捕获来源
                var previousPlugin = ComponentRegistryService.CurrentRegisteringPlugin.Value;
                ComponentRegistryService.CurrentRegisteringPlugin.Value = info;
                try
                {
                    entranceObj.Initialize(context, services);
                }
                finally
                {
                    ComponentRegistryService.CurrentRegisteringPlugin.Value = previousPlugin;
                }
                services.AddSingleton(typeof(PluginBase), entranceObj);
                services.AddSingleton(entrance, entranceObj);
                info.LoadStatus = PluginLoadStatus.Loaded;
                // Console.WriteLine($"Initialized plugin: {pluginDir} ({manifest.Version})");
                PluginLoadedStatus.Add(info);
            }
            catch (Exception ex)
            {
                info.Exception = BuildLoadException(manifest, ex);
                info.LoadStatus = PluginLoadStatus.Error;
                PluginLoadedStatus.Add(info);
                Console.WriteLine($"[插件加载失败] {manifest.Name}({manifest.Id}) {manifest.Version}：{ex}");
            }
        }
        
        AppBase.Current.AppStarted += CurrentOnAppStarted;
    }

    private static void CurrentOnAppStarted(object? sender, EventArgs e)
    {
        if (IAppHost.TryGetService<IManagementService>() is not
            { IsManagementEnabled: true, Connection: ManagementServerConnection connection })
        {
            return;
        }

        foreach (var i in InstalledPlugins)
        {
            connection.LogAuditEvent(AuditEvents.PluginInstalled, new PluginInstalled()
            {
                PluginId = i.Id,
                Version = i.Version
            });
        }
        foreach (var i in UninstalledPlugins)
        {
            connection.LogAuditEvent(AuditEvents.PluginUninstalled, new PluginUninstalled()
            {
                PluginId = i.Id,
                Version = i.Version
            });
        }
    }

    /// <summary>
    /// 异步导出指定插件为插件包
    /// </summary>
    /// <param name="id">插件ID</param>
    /// <param name="outputPath">输出文件位置</param>
    /// <exception cref="ArgumentException">当找不到指定插件时抛出此异常</exception>
    public static async Task PackagePluginAsync(string id, string outputPath)
    {
        if (IPluginService.LoadedPlugins.All(x => x.Manifest.Id != id))
        {
            throw new ArgumentException($"找不到插件 {id}。", nameof(id));
        }
        await using var outputStream = File.Create(outputPath);
        await PackagePluginAsync(id, outputStream);
    }

    /// <summary>
    /// 异步导出指定插件到流
    /// </summary>
    public static async Task PackagePluginAsync(string id, Stream outputStream)
    {
        var plugin = IPluginService.LoadedPlugins.FirstOrDefault(x => x.Manifest.Id == id);
        if (plugin == null)
        {
            throw new ArgumentException($"找不到插件 {id}。", nameof(id));
        }

        await Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(plugin.PluginFolderPath, outputStream);
        });
    }

    /// <summary>
    /// 解析插件入口程序集的实际路径。
    /// </summary>
    /// <remarks>
    /// 兼容层：清单里的程序集名大小写与实际文件不一致时，Windows 上碰巧能加载，Linux / macOS 上会直接找不到；
    /// 另外有些插件包把入口程序集放在子目录里，清单却只写了文件名。
    /// 这里在直接拼路径失败后做一次不区分大小写的兜底搜索，避免这类打包问题让整个插件加载失败。
    /// </remarks>
    private static string ResolveEntranceAssemblyPath(string pluginDir, string entranceAssembly)
    {
        var fullPath = Path.GetFullPath(Path.Combine(pluginDir, entranceAssembly));
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        var fileName = Path.GetFileName(entranceAssembly.Replace('\\', '/'));
        if (string.IsNullOrEmpty(fileName))
        {
            return fullPath;
        }

        try
        {
            var match = Directory
                .EnumerateFiles(pluginDir, "*.dll", SearchOption.AllDirectories)
                .FirstOrDefault(x => Path.GetFileName(x).Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return Path.GetFullPath(match);
            }
        }
        catch
        {
            // 目录不可读时保持原路径，让上层给出原始异常。
        }

        return fullPath;
    }

    private static List<string> ResolveLoadOrder(List<PluginInfo> plugins)
    {
        var nodes = plugins
            .Where(x => x.LoadStatus == PluginLoadStatus.NotLoaded)
            .ToDictionary(
            x => x.Manifest.Id, 
            x => new DependencyNode(x));
        foreach (var i in nodes)
        {
            ResolveDependencyNode(nodes, i.Value, []);
        }
        return nodes
            .Where(x => x.Value.Plugin.LoadStatus == PluginLoadStatus.NotLoaded)
            .OrderBy(x => x.Value.DependencyTreeDepth)
            .Select(x => x.Key)
            .ToList();
    }

    private static void ResolveDependencyNode(Dictionary<string, DependencyNode> allNodes, DependencyNode node, List<DependencyNode> walkingNodes)
    {
        if (node.IsDiscovered)
        {
            return;
        }

        if (walkingNodes.Contains(node))
        {
            throw new InvalidOperationException(
                $"检测到循环依赖：{string.Join(" -> ", walkingNodes.Select(x => x.Plugin.Manifest.Id))}");
        }

        node.IsDiscovered = true;
        var depth = 0;
        foreach (var i in node.Plugin.Manifest.Dependencies)
        {
            if (!allNodes.TryGetValue(i.Id, out var dependency) || dependency.Plugin.LoadStatus != PluginLoadStatus.NotLoaded)
            {
                if (i.IsRequired)
                {
                    node.Plugin.LoadStatus = PluginLoadStatus.Error;
                    node.Plugin.Exception = new InvalidOperationException($"插件 {node.Plugin.Manifest.Id} 依赖的必选插件 {i.Id} 不存在或处于无法加载状态。");
                    return;
                }
                continue;
            }

            ResolveDependencyNode(allNodes, dependency, walkingNodes);
            depth = Math.Max(depth, dependency.DependencyTreeDepth);
        }
        node.DependencyTreeDepth = depth + 1;

    }
}
