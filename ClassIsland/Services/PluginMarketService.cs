using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Helpers;
using ClassIsland.Core.Models;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Platforms.Abstraction.Models;
using ClassIsland.Shared;
using ClassIsland.Shared.ComponentModels;
using ClassIsland.Shared.Helpers;
using ClassIsland.Core.Enums;
using ClassIsland.Services.PluginCompatibility;
using CommunityToolkit.Mvvm.ComponentModel;
using Downloader;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Sentry;

namespace ClassIsland.Services;

public class PluginMarketService : ObservableRecipient, IPluginMarketService
{
    public static string DefaultPluginIndexKey { get; } = "Default";

    public SettingsService SettingsService { get; }
    public IPluginService PluginService { get; }

    public ObservableDictionary<string, DownloadProgress> DownloadTasks { get; } = new();

    public ObservableDictionary<string, PluginIndex> Indexes { get; } = new();
    public ILogger<PluginMarketService> Logger { get; }

    /// <summary>
    /// 插件市场「原生支持 / 需要兼容层」状态的探测器。
    /// </summary>
    private PluginMarketCompatibilityProbe CompatibilityProbe { get; }

    public static ObservableDictionary<string, string> FallbackMirrors { get; } = new()
    {
        { "github", "https://github.com" },
        { "ghproxy", "https://mirror.ghproxy.com/https://github.com" },
        { "moeyy", "https://github.moeyy.xyz/https://github.com" }
    };

    private bool _isLoadingPluginSource = false;
    private double _pluginSourceDownloadProgress;
    private Exception? _exception;
    private IDisposable? _pluginsUpdateProgressObserver;

    /// <summary>
    /// 下载插件市场图标、简介用的客户端。
    /// </summary>
    private static readonly HttpClient AssetHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// 正在后台缓存资源的插件 ID，避免重复排队。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _assetDownloads = new(StringComparer.Ordinal);

    private readonly OSPlatform _currentOSPlatform =
        OperatingSystem.IsWindows() ? OSPlatform.Windows :
        OperatingSystem.IsLinux() ? OSPlatform.Linux :
        OperatingSystem.IsMacOS() ? OSPlatform.macOS :
        OperatingSystem.IsAndroid() ? OSPlatform.Android :
        OperatingSystem.IsIOS() ? OSPlatform.iOS :
        OSPlatform.Unknown;

    public PluginMarketService(SettingsService settingsService, IPluginService pluginService, ILogger<PluginMarketService> logger)
    {
        SettingsService = settingsService;
        PluginService = pluginService;
        Logger = logger;
        CompatibilityProbe = new PluginMarketCompatibilityProbe(logger);

        if (DateTime.Now - SettingsService.Settings.LastRefreshPluginSourceTime >= TimeSpan.FromDays(7))
        {
            _ = RefreshPluginSourceAsync();
        }
    }

    public ObservableDictionary<string, PluginInfo> MergedPlugins { get; } = new();

    public bool IsLoadingPluginSource
    {
        get => _isLoadingPluginSource;
        set
        {
            if (value == _isLoadingPluginSource) return;
            _isLoadingPluginSource = value;
            OnPropertyChanged();
        }
    }

    public double PluginSourceDownloadProgress
    {
        get => _pluginSourceDownloadProgress;
        set
        {
            if (value.Equals(_pluginSourceDownloadProgress)) return;
            _pluginSourceDownloadProgress = value;
            OnPropertyChanged();
        }
    }

    public Exception? Exception
    {
        get => _exception;
        set
        {
            if (Equals(value, _exception)) return;
            _exception = value;
            OnPropertyChanged();
        }
    }

    public async Task RefreshPluginSourceAsync()
    {
        if (IsLoadingPluginSource)
            return;
        IsLoadingPluginSource = true;
        Exception = null;
        PluginSourceDownloadProgress = 0.0;
        Logger.LogInformation("正在刷新插件源……");
        var transaction = SentrySdk.StartTransaction("Update Plugin Index", "pluginIndex.update");
        var ignoreSsl = SettingsService.Settings.IgnoreSslForPluginMirrors;
        var prevCallback = (ignoreSsl ? ServicePointManager.ServerCertificateValidationCallback : null);
        if (ignoreSsl)
        {
            ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        try
        {
            if (SettingsService.Settings.OfficialIndexMirrors.Count <= 0)
            {
                SettingsService.Settings.OfficialIndexMirrors = ConfigureFileHelper.CopyObject(FallbackMirrors);
            }
            var indexes = GetIndexInfos().ToList();
            var i = 0.0;
            var total = Math.Max(1, indexes.Count);
            foreach (var indexInfo in indexes)
            {
                var url = indexInfo.Url.Replace("{time}",
                    ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString());
                Logger.LogDebug("正在刷新插件源：{}（{}）", indexInfo.Id, url);
                var archive = Path.GetTempFileName();
                var download = DownloadBuilder.New()
                    .WithUrl(url)
                    .WithFileLocation(archive)
                    .WithConfiguration(new DownloadConfiguration()
                    {
                        BlockTimeout = 10_000,
                        HttpClientTimeout = 10_000
                    })
                    .Build();
                var i1 = i;
                download.DownloadProgressChanged +=
                    (sender, args) =>
                        PluginSourceDownloadProgress = (args.ProgressPercentage / total) + (i1 / total * 100.0);
                var downloadError = new Exception?[1];
                download.DownloadFileCompleted += (sender, args) =>
                {
                    if (args.Error != null)
                    {
                        // 事件回调里抛异常会一路逃到线程池，直接终止进程。
                        // 记下来，交给 await 之后的统一出口处理。
                        downloadError[0] = args.Error;
                        return;
                    }

                    try
                    {
                        var indexFolderPath = Path.Combine(Services.PluginService.PluginsIndexPath, indexInfo.Id);
                        if (Directory.Exists(indexFolderPath))
                        {
                            Directory.Delete(indexFolderPath, true);
                        }

                        Directory.CreateDirectory(indexFolderPath);
                        ZipFile.ExtractToDirectory(archive, indexFolderPath);
                    }
                    catch (Exception ex)
                    {
                        downloadError[0] = ex;
                    }
                };
                await download.StartAsync();
                if (downloadError[0] != null)
                {
                    throw new InvalidOperationException($"无法加载插件源 {indexInfo.Id}：{downloadError[0].Message}",
                        downloadError[0]);
                }

                i++;
            }
            LoadPluginSource();
            var count = MergedPlugins.Count(x => x.Value is { IsUpdateAvailable: true, IsEnabled: true, RestartRequired: false });
            if (count > 0)
            {
                if (SettingsService.Settings.IsPluginsUpdateNotificationEnabled)
                {
                    await PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                    {
                        Title = "插件更新可用",
                        Body = $"有 {count} 个插件有新版本可用，点击以查看详细信息。",
                        Activated = (_, _) => IAppHost.GetService<IUriNavigationService>().NavigateWrapped(new Uri("classisland://app/settings/classisland.plugins"))
                    });
                }
                if (SettingsService.Settings.IsPluginsAutoUpdateEnabled)
                {
                    UpdateAllPlugins();
                }
            }
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            transaction.Finish(ex, SpanStatus.InternalError);
            Logger.LogError(ex, "无法加载插件源。");
            Exception = ex;

            // 网络不通时退回本地已有索引：插件列表、图标和简介仍然可以浏览，
            // 不至于因为一次刷新失败就把整个插件市场清空。
            if (MergedPlugins.Count == 0 && HasLocalPluginIndex())
            {
                try
                {
                    LoadPluginSource();
                }
                catch (Exception fallbackException)
                {
                    Logger.LogWarning(fallbackException, "回退加载本地插件源失败。");
                }
            }
        }
        finally
        {
            if (ignoreSsl)
            {
                ServicePointManager.ServerCertificateValidationCallback = prevCallback;
            }
        }
        Logger.LogInformation("插件源刷新成功。");
        SettingsService.Settings.LastRefreshPluginSourceTime = DateTime.Now;
        IsLoadingPluginSource = false;
    }

    public IEnumerable<PluginIndexInfo> GetIndexInfos()
    {
        var mirrors = SettingsService.Settings.OfficialIndexMirrors.Count == 0
            ? FallbackMirrors
            : SettingsService.Settings.OfficialIndexMirrors;
        const string repo = "https://get.classisland.tech/d/ClassIsland-Ningbo-S3/classisland/plugin/index.zip?time={time}";
        return SettingsService.Settings.PluginIndexes.Where(x => !string.IsNullOrWhiteSpace(x.Url)).Append(new PluginIndexInfo()
        {
            Id = DefaultPluginIndexKey,
            Url = repo,
            SelectedMirror = SettingsService.Settings.OfficialSelectedMirror ?? "github",
            Mirrors = SettingsService.Settings.OfficialIndexMirrors
        });
    }

    /// <summary>
    /// 本地是否已经存在一份可用的插件索引。
    /// </summary>
    private bool HasLocalPluginIndex()
    {
        try
        {
            return GetIndexInfos().Any(info =>
                File.Exists(Path.Combine(Services.PluginService.PluginsIndexPath, info.Id, "index.v2.json")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 请求探测单个市场插件的兼容状态。
    /// </summary>
    /// <remarks>
    /// 由插件列表在容器准备时逐个触发：只有用户实际滚到的插件才会下载插件包用于检测，
    /// 避免一进插件页就把整个市场的插件包拉一遍。
    /// </remarks>
    public void RequestCompatibilityProbe(PluginInfo plugin)
    {
        if (plugin.IsLocal || !plugin.IsAvailableOnMarket)
        {
            return;
        }

        CompatibilityProbe.Request(plugin);
    }

    public void UpdateAllPlugins(bool discardDisabled = false)
    {
        var toUpdate = MergedPlugins
            .Where(x => x.Value is { IsUpdateAvailable: true, RestartRequired: false }
                        && (x.Value.DownloadProgress == null || x.Value.DownloadProgress.IsDownloading == false)
                        && (discardDisabled || x.Value.IsEnabled))
            .ToImmutableDictionary();
        if (toUpdate.Count <= 0)
        {
            return;
        }
        _pluginsUpdateProgressObserver ??= DownloadTasks.ObservableForProperty(x => x.Count)
            .Subscribe(_ =>
            {
                if (DownloadTasks.Count > 0) return;
                var success = toUpdate.Values.Count(x => x.DownloadProgress?.Exception == null);

                if (SettingsService.Settings.IsPluginsUpdateNotificationEnabled)
                {
                    if (success == toUpdate.Count)
                    {
                        PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                        {
                            Title = "插件更新完成",
                            Body = $"已将 {success} 个插件升级到最新版本，将在下次启动应用时生效。",
                            Buttons =
                            {
                                { "立即重启", () => AppBase.Current.Restart() }
                            }
                        });
                    }
                    else if (success > 0 && success < toUpdate.Count)
                    {
                        PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                        {
                            Title = "插件更新完成",
                            Body = $"已将 {success} 个插件升级到最新版本，{toUpdate.Count - success} 个插件升级失败。将在下次启动应用时生效。",
                            Buttons =
                            {
                                { "立即重启", () => AppBase.Current.Restart() }
                            }
                        });
                    }
                    else
                    {
                        PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                        {
                            Title = "插件更新失败",
                            Body = $"无法更新插件。请检查您的网络设置，或更换插件镜像源，然后再试一遍。"
                        });
                    }

                }

                _pluginsUpdateProgressObserver?.Dispose();
                _pluginsUpdateProgressObserver = null;
            });

        foreach (var (id, _) in toUpdate)
        {
            RequestDownloadPlugin(id);
        }
    }

    public PluginIndexItem? ResolveMarketPlugin(string id)
    {
        return Indexes.Select(i => i.Value.Plugins
            .FirstOrDefault(x => x.Manifest.Id == id))
            .OfType<PluginIndexItem>()
            .FirstOrDefault();
    }

    public async void RequestDownloadPlugin(string id)
    {
        try
        {
            await RequestDownloadPluginCore(id);
        }
        catch (Exception ex)
        {
            // 这是 async void：任何漏出来的异常都会变成进程级的未处理异常。
            // 方法体内已经有一层 catch，这里兜住的是 try 之前的同步段
            // （解析市场项、启动 Sentry 事务、构造下载器等都可能抛）。
            Logger.LogError(ex, "下载插件 {} 时发生未处理的错误", id);
        }
    }

    private async Task RequestDownloadPluginCore(string id)
    {
        var item = ResolveMarketPlugin(id);
        var transaction = SentrySdk.StartTransaction("Download Plugin", "plugin.download");
        transaction.SetTag("plugin.id", id);

        if (item == null)
        {
            Logger.LogWarning("找不到符合id的插件：{}", id);
            transaction.Finish(SpanStatus.NotFound);
            return;
        }
        transaction.SetTag("plugin", item.Manifest.Name);

        if (DownloadTasks.ContainsKey(id))
        {
            Logger.LogWarning("{}已正在下载。", id);
            transaction.Finish(SpanStatus.AlreadyExists);
            return;
        }

        Logger.LogInformation("开始下载插件：{}", id);
        var spanDownload = transaction.StartChild("download");
        var url = item.DownloadUrl;
        var md5 = item.DownloadMd5;
        var task = new DownloadProgress()
        {
            IsDownloading = true
        };
        DownloadTasks[id] = task;
        var archive = Path.GetTempFileName() + ".tmp";
        var download = DownloadBuilder.New()
            .WithUrl(url)
            .WithFileLocation(archive)
            .WithConfiguration(new DownloadConfiguration())
            .Build();
        transaction.SetTag("url", url);
        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            transaction.SetTag("url.host", uri.Host);
        }

        var stopwatch = new Stopwatch();
        var packagePath = Path.Combine(Services.PluginService.PluginsPkgRootPath, id + ".cipx");
        var downloadError = new Exception?[1];
        download.DownloadFileCompleted += (sender, args) =>
        {
            // Downloader 会静默吞掉这个回调里的异常（实测：回调抛异常时 StartAsync 仍正常返回）。
            // 异常一旦发生而没人写 downloadError，后面就会把「下载失败 / 安装包根本没落盘」
            // 当成成功：提示用户重启，重启后插件并不存在，全程没有任何错误提示。
            // 所以整个回调体必须共用同一个 try，任何一步出错都要落到 downloadError。
            try
            {
                stopwatch.Stop();
                transaction.SetExtra("download.size", download.TotalFileSize);
                var speed = stopwatch.Elapsed.TotalSeconds == 0
                    ? 0.0
                    : download.TotalFileSize / stopwatch.Elapsed.TotalSeconds;
                transaction.SetExtra("download.bytesPerSecond", speed);
                if (args.Error != null)
                {
                    downloadError[0] = args.Error;
                    spanDownload.Finish(args.Error, SpanStatus.InternalError);
                    return;
                }

                spanDownload.Finish(SpanStatus.Ok);

                var spanValidateChecksum = transaction.StartChild("validate");
                ChecksumHelper.VerifyChecksum(archive, md5);
                spanValidateChecksum.Finish(SpanStatus.Ok);

                var spanMoveToCache = transaction.StartChild("moveToCache");
                File.Move(archive, packagePath, true);
                spanMoveToCache.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                // 校验失败/磁盘满/文件被占用同样只能记下来。
                downloadError[0] = ex;
                try
                {
                    spanDownload.Finish(ex, SpanStatus.InternalError);
                }
                catch
                {
                    // Sentry 的 span 可能已经结束，这里失败不能影响错误记录本身。
                }
            }
        };
        download.DownloadProgressChanged += (sender, args) =>
        {
            task.Progress = args.ProgressPercentage;
        };
        var ignoreSsl = SettingsService.Settings.IgnoreSslForPluginMirrors;
        var prevCallback = (ignoreSsl ? ServicePointManager.ServerCertificateValidationCallback : null);
        if (ignoreSsl)
        {
            ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        try
        {
            BindDownloadTasks();
            stopwatch.Start();
            await download.StartAsync(task.CancellationToken);
            if (downloadError[0] != null)
            {
                throw new InvalidOperationException($"无法下载插件 {id}：{downloadError[0].Message}", downloadError[0]);
            }
            if (!File.Exists(packagePath))
            {
                // 回调没报错但安装包不在，说明落盘环节被跳过了。不能当作成功，
                // 否则用户会收到「重启以应用更改」而重启后什么都没装上。
                throw new InvalidOperationException($"插件 {id} 下载完成但安装包未落盘：{packagePath}");
            }
            item.RestartRequired = true;
            if (MergedPlugins.TryGetValue(id, out var plugin))
            {
                plugin.RestartRequired = true;
            }
            RestartRequested?.Invoke(this, EventArgs.Empty);
            Logger.LogInformation("插件 {} 下载完成。", id);
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception e)
        {
            task.Exception = e;
            transaction.GetLastActiveSpan()?.Finish(e, SpanStatus.InternalError);
            transaction.Finish(e, SpanStatus.InternalError);
            Logger.LogError(e, "无法从 {} 下载插件 {}", url, id);
        }
        finally
        {
            if (ignoreSsl)
            {
                ServicePointManager.ServerCertificateValidationCallback = prevCallback;
            }
        }
        task.IsDownloading = false;
        DownloadTasks.Remove(id);
    }

    public event EventHandler? RestartRequested;

    public void LoadPluginSource()
    {
        Logger.LogInformation("正在加载插件源");
        MergedPlugins.Clear();
        Indexes.Clear();

        foreach (var pluginLocal in IPluginService.LoadedPlugins)
        {
            var id = pluginLocal.Manifest.Id;
            MergedPlugins[id] = pluginLocal;
            // 已安装插件直接读本地程序集，禁用/加载失败的插件也能标出兼容状态。
            CompatibilityProbe.ApplyLocal(pluginLocal);
        }

        var indexInfos = GetIndexInfos().ToList();
        foreach (var i in indexInfos)
        {
            var indexFolderPath = Path.Combine(Services.PluginService.PluginsIndexPath, i.Id);
            var name = Path.GetFileName(indexFolderPath);
            Logger.LogDebug("正在加载插件源：{}", name);
            var indexPath = Path.Combine(indexFolderPath, "index.v2.json");
            if (!File.Exists(indexPath))
                continue;
            var index = Indexes[name] = ConfigureFileHelper.LoadConfig<PluginIndex>(indexPath);
            var mirror = i.SelectedMirror;
            i.Mirrors = ConfigureFileHelper.CopyObject(index.DownloadMirrors);
            if (!index.DownloadMirrors.TryGetValue(mirror, out var root))
            {
                mirror = i.SelectedMirror = index.DownloadMirrors.First().Key;
                root = index.DownloadMirrors.First().Value;
            }
            Logger.LogDebug("插件源 {} 选择的镜像根：{}", name, root);
            foreach (var plugin in index.Plugins.Where(x =>
                         Version.TryParse(x.Manifest.ApiVersion, out var version) &&
                         version >= Version.Parse("2.0.0.0")))
            {
                var id = plugin.Manifest.Id;
                plugin.DownloadUrl = plugin.DownloadUrl.Replace("{root}", root);
                if (MergedPlugins.ContainsKey(id) && MergedPlugins[id].IsLocal)
                {
                    var pluginLocal = MergedPlugins[id];
                    pluginLocal.IsAvailableOnMarket = true;
                    pluginLocal.DownloadCount = plugin.DownloadCount;
                    pluginLocal.StarsCount = plugin.StarsCount;
                    if (Version.TryParse(pluginLocal.Manifest.Version, out var versionLocal) &&
                        Version.TryParse(plugin.Manifest.Version, out var versionRemote) &&
                        Version.TryParse(plugin.Manifest.ApiVersion, out var apiVersion) &&
                        Version.TryParse(AppBase.AppVersion, out var appVersion) &&
                        versionRemote > versionLocal)  // TODO: 在 2.0 发布后，添加 api 版本校验！
                    {
                        pluginLocal.IsUpdateAvailable = true;
                    }
                    continue;
                }
                plugin.IsAvailableOnMarket = true;
                plugin.RealIconPath = plugin.RealIconPath.Replace("{root}", root);
                plugin.Manifest.Readme = plugin.Manifest.Readme.Replace("{root}", root);
                plugin.IsNotSupportCurrentOS = !plugin.Manifest.SupportedOSPlatforms.Contains(_currentOSPlatform);
                ApplyCachedAssets(plugin);
                MergedPlugins[id] = plugin;
            }
        }

        SettingsService.Settings.OfficialSelectedMirror =
            indexInfos.First(x => x.Id == DefaultPluginIndexKey).SelectedMirror;
        var defaultIndex = Indexes.FirstOrDefault(x => x.Key == DefaultPluginIndexKey).Value ?? new PluginIndex();
        SettingsService.Settings.OfficialIndexMirrors = ConfigureFileHelper.CopyObject(
            defaultIndex.DownloadMirrors);
        BindDownloadTasks();
    }

    /// <summary>
    /// 插件市场资源（图标、简介）的本地缓存目录。
    /// </summary>
    private static string GetAssetCacheDirectory(string pluginId) =>
        Path.Combine(CommonDirectories.AppCacheFolderPath, "PluginMarketAssets", pluginId);

    /// <summary>
    /// 让插件优先使用本地缓存的图标和简介；缓存缺失时在后台补一份。
    /// </summary>
    /// <remarks>
    /// 插件市场的图标和 README 都是指向 GitHub 的远程地址，每次进页面都要重新联网才能显示。
    /// 这里在首次成功加载后把资源落盘，之后即使插件源刷新失败、或者网络被墙，列表和详情页也能正常显示。
    /// </remarks>
    private void ApplyCachedAssets(PluginIndexItem plugin)
    {
        var id = plugin.Manifest.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var directory = GetAssetCacheDirectory(id);
        var icon = FindCachedIcon(directory);
        var readme = Path.Combine(directory, "readme.md");
        var hasIcon = icon != null && File.Exists(icon);
        var hasReadme = File.Exists(readme);

        if (hasIcon)
        {
            plugin.RealIconPath = icon!;
        }

        if (hasReadme)
        {
            plugin.Manifest.Readme = readme;
        }

        if (hasIcon && hasReadme)
        {
            return;
        }

        ScheduleAssetCaching(plugin, directory, hasIcon, hasReadme);
    }

    /// <summary>
    /// 在后台下载缺失的图标与简介，成功后更新插件对象并落盘。
    /// </summary>
    private void ScheduleAssetCaching(PluginIndexItem plugin, string directory, bool hasIcon, bool hasReadme)
    {
        var id = plugin.Manifest.Id;
        if (!_assetDownloads.TryAdd(id, 0))
        {
            return;
        }

        var iconUrl = plugin.RealIconPath;
        var readmeUrl = plugin.Manifest.Readme;
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (!hasIcon && IsRemoteUrl(iconUrl))
                {
                    var iconPath = Path.Combine(directory, "icon" + GetAssetExtension(iconUrl));
                    if (await DownloadAssetAsync(iconUrl, iconPath).ConfigureAwait(false))
                    {
                        Dispatcher.UIThread.Post(() => plugin.RealIconPath = iconPath);
                    }
                }

                if (!hasReadme && IsRemoteUrl(readmeUrl))
                {
                    var readmePath = Path.Combine(directory, "readme.md");
                    if (await DownloadAssetAsync(readmeUrl, readmePath).ConfigureAwait(false))
                    {
                        Dispatcher.UIThread.Post(() => plugin.Manifest.Readme = readmePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "缓存插件 {} 的市场资源失败。", id);
            }
            finally
            {
                _assetDownloads.TryRemove(id, out _);
            }
        });
    }

    private static string? FindCachedIcon(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory.EnumerateFiles(directory, "icon.*").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRemoteUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string GetAssetExtension(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (extension.Length is > 1 and <= 6)
            {
                return extension;
            }
        }

        return ".png";
    }

    private static async Task<bool> DownloadAssetAsync(string url, string destination)
    {
        if (File.Exists(destination))
        {
            return true;
        }

        var temporary = destination + ".tmp";
        try
        {
            using var response = await AssetHttpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using (var target = File.Create(temporary))
            {
                await source.CopyToAsync(target).ConfigureAwait(false);
            }

            File.Move(temporary, destination, true);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // 忽略清理失败。
            }

            return false;
        }
    }

    private void BindDownloadTasks()
    {
        foreach (var i in DownloadTasks)
        {
            var b = MergedPlugins.TryGetValue(i.Key, out var v);
            if (!b || v == null)
                continue;
            v.DownloadProgress = i.Value;
        }

        foreach (var plugin in MergedPlugins.Where(plugin => File.Exists(Path.Combine(Services.PluginService.PluginsPkgRootPath, plugin.Value.Manifest.Id + ".cipx"))))
        {
            plugin.Value.RestartRequired = true;
        }
    }
}