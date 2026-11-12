using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Enums;
using ClassIsland.Core.Models.Plugin;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.PluginCompatibility;

/// <summary>
/// 插件市场兼容状态探测器。
/// </summary>
/// <remarks>
/// <para>
/// 插件索引里没有记录插件编译时使用的 Avalonia 版本，只有拿到入口程序集、读取它的程序集引用
/// 才能判断这个插件是原生支持当前宿主，还是需要兼容层转译。
/// </para>
/// <para>
/// 为了让插件市场列表在下载前就能标出状态，这里在后台按队列逐个下载插件包、读取入口程序集、
/// 记录结论后立刻删除临时包。探测结果按「插件 ID + 版本」持久化，同一个版本只探测一次；
/// 网络不通或插件包过大时不做任何标记，等下次再试。
/// </para>
/// </remarks>
internal sealed class PluginMarketCompatibilityProbe
{
    private const string CacheFileName = "PluginCompatibilityProbe.json";

    /// <summary>
    /// 探测用的体积上限。超过这个大小的插件包不下载，避免为了一个标签浪费大量流量。
    /// </summary>
    private const long MaxPackageSize = 32 * 1024 * 1024;

    private static readonly HttpClient ProbeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PluginCompatibilityMode> _results = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _attempted = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly object _saveLock = new();
    private readonly string _cachePath;

    public PluginMarketCompatibilityProbe(ILogger logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(CommonDirectories.AppCacheFolderPath, CacheFileName);
        LoadCache();
    }

    /// <summary>
    /// 本地已安装插件的探测结果会直接写入 <see cref="PluginInfo.CompatibilityMode"/>。
    /// </summary>
    public void ApplyLocal(PluginInfo plugin)
    {
        if (plugin.CompatibilityMode != PluginCompatibilityMode.Unknown)
        {
            return;
        }

        var mode = DetectLocal(plugin);
        if (mode != PluginCompatibilityMode.Unknown)
        {
            plugin.CompatibilityMode = mode;
        }
    }

    /// <summary>
    /// 从本地已安装的插件目录检测兼容状态。目录不存在时返回 <see cref="PluginCompatibilityMode.Unknown"/>。
    /// </summary>
    public PluginCompatibilityMode DetectLocal(PluginInfo plugin)
    {
        var folder = plugin.PluginFolderPath;
        var entrance = plugin.Manifest.EntranceAssembly;
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(entrance))
        {
            return PluginCompatibilityMode.Unknown;
        }

        var assemblyPath = Path.Combine(folder, entrance);
        if (!File.Exists(assemblyPath))
        {
            return PluginCompatibilityMode.Unknown;
        }

        var mode = PluginTypeCompatibility.Detect(assemblyPath);
        if (mode != PluginCompatibilityMode.Unknown)
        {
            Remember(plugin, mode);
        }

        return mode;
    }

    /// <summary>
    /// 请求探测单个市场插件的兼容状态。已探测或已排队的插件不会重复下载。
    /// </summary>
    /// <remarks>
    /// 由插件列表在容器准备时触发，这样只有用户实际滚到的插件才会被下载，避免一进页面就拉取整个市场。
    /// </remarks>
    public void Request(PluginInfo plugin)
    {
        Enqueue(plugin);
    }

    private void Enqueue(PluginInfo plugin)
    {
        if (plugin.IsLocal || plugin.CompatibilityMode != PluginCompatibilityMode.Unknown)
        {
            return;
        }

        var key = GetKey(plugin);
        if (_results.TryGetValue(key, out var cached))
        {
            plugin.CompatibilityMode = cached;
            return;
        }

        // 同一次会话里失败过的插件不再重试，避免每次打开插件页都发起一轮注定失败的请求。
        if (!_attempted.TryAdd(key, 0) || !_pending.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(() => ProbeAsync(plugin, key));
    }

    private async Task ProbeAsync(PluginInfo plugin, string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var mode = await ProbeCoreAsync(plugin).ConfigureAwait(false);
            if (mode == PluginCompatibilityMode.Unknown)
            {
                return;
            }

            Remember(plugin, mode);
            Dispatcher.UIThread.Post(() => plugin.CompatibilityMode = mode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "探测插件 {} 的兼容状态失败。", plugin.Manifest.Id);
        }
        finally
        {
            _pending.TryRemove(key, out _);
            _gate.Release();
        }
    }

    private async Task<PluginCompatibilityMode> ProbeCoreAsync(PluginInfo plugin)
    {
        var entrance = plugin.Manifest.EntranceAssembly;
        if (string.IsNullOrWhiteSpace(entrance))
        {
            return PluginCompatibilityMode.Unknown;
        }

        // 已经下载好的插件包优先复用，避免重复下载。
        var packagePath = Path.Combine(
            PluginService.PluginsPkgRootPath,
            plugin.Manifest.Id + IPluginService.PluginPackageExtension);
        string? temporary = null;
        if (!File.Exists(packagePath))
        {
            var url = (plugin as PluginIndexItem)?.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return PluginCompatibilityMode.Unknown;
            }

            temporary = Path.Combine(Path.GetTempPath(), $"ci-probe-{Guid.NewGuid():N}.cipx");
            packagePath = temporary;
            if (!await DownloadAsync(url, temporary).ConfigureAwait(false))
            {
                TryDelete(temporary);
                return PluginCompatibilityMode.Unknown;
            }
        }

        try
        {
            return DetectFromPackage(packagePath, entrance);
        }
        finally
        {
            if (temporary != null)
            {
                TryDelete(temporary);
            }
        }
    }

    private static async Task<bool> DownloadAsync(string url, string destination)
    {
        try
        {
            using var response = await ProbeHttpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (response.Content.Headers.ContentLength is > MaxPackageSize)
            {
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var target = File.Create(destination);
            await source.CopyToAsync(target).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PluginCompatibilityMode DetectFromPackage(string packagePath, string entranceAssembly)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.Entries.FirstOrDefault(x =>
                        x.FullName.Equals(entranceAssembly, StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(x =>
                        x.FullName.EndsWith("/" + entranceAssembly, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return PluginCompatibilityMode.Unknown;
        }

        var temporary = Path.Combine(Path.GetTempPath(), $"ci-probe-{Guid.NewGuid():N}.dll");
        try
        {
            entry.ExtractToFile(temporary, true);
            return PluginTypeCompatibility.Detect(temporary);
        }
        catch
        {
            return PluginCompatibilityMode.Unknown;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static string GetKey(PluginInfo plugin) => $"{plugin.Manifest.Id}@{plugin.Manifest.Version}";

    private void Remember(PluginInfo plugin, PluginCompatibilityMode mode)
    {
        var key = GetKey(plugin);
        if (_results.TryGetValue(key, out var existing) && existing == mode)
        {
            return;
        }

        _results[key] = mode;
        SaveCache();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, PluginCompatibilityMode>>(
                File.ReadAllText(_cachePath));
            if (data == null)
            {
                return;
            }

            foreach (var (key, value) in data)
            {
                _results[key] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法读取插件兼容状态缓存，将重新探测。");
        }
    }

    private void SaveCache()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllText(_cachePath,
                    JsonSerializer.Serialize(_results.ToDictionary(x => x.Key, x => x.Value)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "无法写入插件兼容状态缓存。");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略清理失败。
        }
    }
}
