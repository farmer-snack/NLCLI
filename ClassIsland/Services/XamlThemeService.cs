using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Helpers;
using ClassIsland.Core.Models;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Core.Models.XamlTheme;
using ClassIsland.Shared;
using ClassIsland.Shared.ComponentModels;
using ClassIsland.Shared.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using Downloader;
using Microsoft.Extensions.Logging;
using Sentry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClassIsland.Services;

public class XamlThemeService : ObservableRecipient, IXamlThemeService
{
    private static readonly FieldInfo? s_stylesAppliedField = typeof(StyledElement).GetField("_stylesApplied", BindingFlags.Instance | BindingFlags.NonPublic);
    
    public ILogger<XamlThemeService> Logger { get; }
    public IPluginMarketService PluginMarketService { get; }
    public SettingsService SettingsService { get; }
    private IComponentsService ComponentsService { get; }
    private Styles RootStyles { get; set; } = [];

    public Window? MainWindow { get; set; }
    
    private Border? ResourceLoaderBorder { get; set; }

    public static readonly string ThemesPath = Path.Combine(CommonDirectories.AppConfigPath, "Themes");
    public static readonly string EnabledThemesPath = Path.Combine(CommonDirectories.AppConfigPath, "EnabledThemes.json");
    public static readonly string ThemesPkgRootPath = Path.Combine(CommonDirectories.AppCacheFolderPath, "ThemePackages");

    /// <summary>
    /// 默认启用的主界面主题。液态玻璃只是主题页面里的一个可选项，不作为默认。
    /// </summary>
    public const string DefaultThemeId = "classisland.fluent";

    /// <summary>
    /// 曾被设为默认、现在仅作为可选项保留的内置主题。
    /// </summary>
    private const string LegacyLiquidGlassThemeId = "classisland.liquidglass";

    /// <summary>
    /// 旧版本把液态玻璃设为默认时写下的迁移标记，只用于清理，不再参与判断。
    /// </summary>
    private static readonly string LegacyDefaultThemeMigrationPath =
        Path.Combine(CommonDirectories.AppConfigPath, "DefaultThemeMigration");

    /// <summary>
    /// 「取消液态玻璃默认」回滚标记。写入后不再自动改动用户的主题选择。
    /// </summary>
    public static readonly string LiquidGlassDefaultRollbackPath =
        Path.Combine(CommonDirectories.AppConfigPath, "LiquidGlassDefaultRollback");

    public ObservableCollection<ThemeInfo> Themes { get; } = [];

    public ObservableDictionary<string, ThemeInfo> MergedThemes
    {
        get => _mergedThemes;
        set => SetProperty(ref _mergedThemes, value);
    }

    public ObservableDictionary<string, ThemeIndex> Indexes { get; } = [];

    public ObservableDictionary<string, DownloadProgress> DownloadTasks { get; } = new();

    public ObservableCollection<string> EnabledThemes { get; }

    
    private ObservableDictionary<string, ThemeInfo> _mergedThemes = [];

    public event EventHandler? RestartRequested;

    public double ActualVerticalSafeAreaPx { get; set; } = 0.0;


    public XamlThemeService(ILogger<XamlThemeService> logger, IPluginMarketService pluginMarketService,
        SettingsService settingsService, IComponentsService componentsService)
    {
        Logger = logger;
        PluginMarketService = pluginMarketService;
        SettingsService = settingsService;
        ComponentsService = componentsService;
        EnabledThemes = ConfigureFileHelper.LoadConfig<ObservableCollection<string>>(EnabledThemesPath);
        if (EnabledThemes.Count == 0)
        {
            EnabledThemes.Add(DefaultThemeId);
            // 全新安装没有经历过「液态玻璃被设为默认」的旧版本，配置里不会有需要回滚的痕迹，
            // 但同样要落下回滚标记：否则用户以后在主题页主动选中液态玻璃（启用列表恰好只有它）时，
            // 下次启动会被 RollbackLiquidGlassDefault 误判为旧版残留，强制改回 Fluent。
            TryWriteLiquidGlassRollbackMarker();
        }
        else
        {
            RollbackLiquidGlassDefault();
        }
        EnabledThemes.CollectionChanged +=
            (_, _) => ConfigureFileHelper.SaveConfig(EnabledThemesPath, EnabledThemes);
        if (App.ApplicationCommand.Safe)
        {
            return;
        }
        
        ProcessThemeInstall();
        // LoadAllThemes();
        //LoadThemeSource();
    }

    /// <summary>
    /// 撤销旧版本「把液态玻璃设为默认」的一次性迁移。
    /// </summary>
    /// <remarks>
    /// 旧版本会把从未调整过主题的配置从 classisland.fluent 改成 classisland.liquidglass，
    /// 首次运行更是直接写入液态玻璃。只改代码里的默认值对这些已有配置无效，升级后
    /// 看到的仍然是液态玻璃，所以要把「启用列表恰好只有液态玻璃」的配置回滚成默认主题。
    /// 标记必须先写成功再改配置：否则写标记失败时，每次启动都会把用户后来主动选的
    /// 液态玻璃又改回去。只做一次，之后用户主动选回液态玻璃不会再被干预。
    /// </remarks>
    private void RollbackLiquidGlassDefault()
    {
        if (File.Exists(LiquidGlassDefaultRollbackPath) || !TryWriteLiquidGlassRollbackMarker())
        {
            return;
        }

        if (EnabledThemes.Count == 1 && EnabledThemes[0] == LegacyLiquidGlassThemeId)
        {
            EnabledThemes[0] = DefaultThemeId;
            try
            {
                // 这里 EnabledThemes 的 CollectionChanged 还没订阅，必须显式落盘。
                ConfigureFileHelper.SaveConfig(EnabledThemesPath, EnabledThemes);
                Logger.LogInformation("已将默认主界面主题从 {} 回滚到 {}", LegacyLiquidGlassThemeId, DefaultThemeId);
            }
            catch (Exception ex)
            {
                // 内存里改了、磁盘上没改，本次会话看起来已经回滚。标记保持已写状态，
                // 不删标记重试：磁盘故障恢复后用户可能已经主动选了液态玻璃，重试会把
                // 用户自己的选择又改掉。液态玻璃现在是可选项，用户可在主题页自行换回。
                Logger.LogWarning(ex, "回滚后的主题配置保存失败，本次不再重试");
            }
        }

        // 旧迁移标记已经没有作用，顺手清掉，避免配置目录里留下无意义的残留文件。
        try
        {
            if (File.Exists(LegacyDefaultThemeMigrationPath))
            {
                File.Delete(LegacyDefaultThemeMigrationPath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "无法清理旧的主题迁移标记");
        }
    }

    /// <summary>
    /// 写入「已处理过液态玻璃默认回滚」标记。只有写成功才允许改动用户配置。
    /// </summary>
    private bool TryWriteLiquidGlassRollbackMarker()
    {
        if (File.Exists(LiquidGlassDefaultRollbackPath))
        {
            return true;
        }

        try
        {
            var directory = Path.GetDirectoryName(LiquidGlassDefaultRollbackPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(LiquidGlassDefaultRollbackPath, DateTimeOffset.Now.ToString("O"));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "无法写入主题回滚标记，跳过本次回滚以免反复改动用户选择");
            return false;
        }
    }

    public void LoadAllThemes()
    {
        LoadThemeSource();

        if (App.ApplicationCommand.Safe)
        {
            return;
        }
        if (ResourceLoaderBorder == null)
        {
            SetResourceHost(MainWindow?.FindControl<Border>("ResourceLoaderBorder"));
        }
        RootStyles.Clear();
        ResourceLoaderBorder?.Styles.Remove(RootStyles);
        if (ResourceLoaderBorder != null)
        {
            s_stylesAppliedField?.SetValue(ResourceLoaderBorder, false);
        }
        RootStyles = [];
        ResourceLoaderBorder?.Styles.Add(RootStyles);
        var actualSafeAreaPx = 0.0;
        foreach (var themeInfo in EnabledThemes.Select(x => Themes.FirstOrDefault(y => y.Manifest.Id == x))
                     .OfType<ThemeInfo>())
        {
            try
            {
                if (themeInfo.IsExternal)
                {
                    LoadThemeFromFile(Path.Combine(themeInfo.Path, "Styles.axaml"));
                }
                else
                {
                    LoadThemeFromResource(themeInfo.ThemeUri ?? throw new InvalidOperationException("资源主题必须指定主题 Uri"));
                }
                actualSafeAreaPx = Math.Max(themeInfo.Manifest.VerticalSafeAreaPx, actualSafeAreaPx);
                themeInfo.IsLoaded = true;
            }
            catch (Exception e)
            {
                themeInfo.IsError = true;
                themeInfo.Error = e;
            }
        }

        ActualVerticalSafeAreaPx = actualSafeAreaPx;
    }

    void IXamlThemeService.SetResourceHost(Border? host)
    {
        SetResourceHost(host);
    }

    private void SetResourceHost(Border? host)
    {
        if (ReferenceEquals(ResourceLoaderBorder, host))
        {
            return;
        }
        ResourceLoaderBorder?.Styles.Remove(RootStyles);
        RootStyles.Clear();
        ResourceLoaderBorder = host;
    }

    private void LoadThemeFromFile(string themePath)
    {
        Logger.LogInformation("正在从文件加载主题 {}", themePath);
        var uri = new Uri(Path.GetFullPath(themePath));
        if (AvaloniaRuntimeXamlLoader.Load(File.ReadAllText(themePath), Assembly.GetExecutingAssembly(), uri: uri) is
            not Styles styles)
        {
            return;
        }
        RootStyles.Add(styles);
    }
    
    private void LoadThemeFromResource(Uri uri)
    {
        Logger.LogInformation("正在从资源加载主题 {}", uri);
        RootStyles.Add((IStyle)AvaloniaXamlLoader.Load(uri));
    }

    public void LoadThemeSource()
    {
        Logger.LogInformation("正在加载主题源");
        LoadLocalThemes();
        PluginMarketService.LoadPluginSource();
        var merged = new ObservableDictionary<string, ThemeInfo>();
        Indexes.Clear();
        
        foreach (var themeLocal in Themes)
        {
            var id = themeLocal.Manifest.Id;
            merged[id] = themeLocal;
        }


        var indexInfos = PluginMarketService.GetIndexInfos().ToList();
        foreach (var i in indexInfos)
        {
            var indexFolderPath = Path.Combine(Services.PluginService.PluginsIndexPath, i.Id);
            var name = Path.GetFileName(indexFolderPath);
            Logger.LogDebug("正在加载主题源：{}", name);
            var indexPath = Path.Combine(indexFolderPath, "themes.json");
            if (!File.Exists(indexPath))
                continue;
            var index = Indexes[name] = ConfigureFileHelper.LoadConfig<ThemeIndex>(indexPath);
            var pluginIndex = PluginMarketService.Indexes.GetValueOrDefault(name) ?? new PluginIndex()
            {
                DownloadMirrors = Services.PluginMarketService.FallbackMirrors
            };
            var mirror = i.SelectedMirror;
            if (!pluginIndex.DownloadMirrors.TryGetValue(mirror, out var root))
            {
                root = pluginIndex.DownloadMirrors.First().Value;
            }
            Logger.LogDebug("主题源 {} 选择的镜像根：{}", name, root);
            foreach (var theme in index.Themes)
            {
                var id = theme.Manifest.Id;
                theme.DownloadUrl = theme.DownloadUrl.Replace("{root}", root);
                if (merged.ContainsKey(id) && merged[id].IsLocal)
                {
                    var themeLocal = merged[id];
                    themeLocal.IsAvailableOnMarket = true;
                    themeLocal.DownloadCount = theme.DownloadCount;
                    themeLocal.StarsCount = theme.StarsCount;
                    if (Version.TryParse(themeLocal.Manifest.Version, out var versionLocal) &&
                        Version.TryParse(theme.Manifest.Version, out var versionRemote) &&
                        versionRemote > versionLocal)
                    {
                        themeLocal.IsUpdateAvailable = true;
                    }

                    merged.Remove(id);
                    merged[id] = themeLocal;
                    continue;
                }
                theme.IsAvailableOnMarket = true;
                theme.RealBannerPath = theme.RealBannerPath.Replace("{root}", root);
                merged[id] = theme;
            }
        }

        MergedThemes = merged;
        BindDownloadTasks();
    }

    private void BindDownloadTasks()
    {
        foreach (var i in DownloadTasks)
        {
            var b = MergedThemes.TryGetValue(i.Key, out var v);
            if (!b || v == null)
                continue;
            v.DownloadProgress = i.Value;
        }

        foreach (var theme in MergedThemes.Where(theme => File.Exists(Path.Combine(ThemesPkgRootPath, theme.Value.Manifest.Id + ".zip"))))
        {
            theme.Value.RestartRequired = true;
        }
    }

    private void LoadLocalThemes()
    {
        Themes.Clear();
        foreach (var integratedTheme in IXamlThemeService.IntegratedThemes)
        {
            Themes.Add(integratedTheme);
        }
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        foreach (var i in Directory.GetDirectories(ThemesPath))
        {
            var manifest = new ThemeManifest()
            {
                Name = Path.GetFileName(i),
                Id = Path.GetFileName(i),
            };
            var themeInfo = new ThemeInfo
            {
                Path = Path.GetFullPath(i)
            };
            try
            {
                if (File.Exists(Path.Combine(i, "manifest.yml")))
                {
                    var yaml = File.ReadAllText(Path.Combine(i, "manifest.yml"));
                    manifest = deserializer.Deserialize<ThemeManifest>(yaml);
                }

                themeInfo.Manifest = manifest;
                themeInfo.Path = Path.GetFullPath(i);
                themeInfo.IsLocal = true;
                themeInfo.RealBannerPath = Path.GetFullPath(Path.Combine(themeInfo.Path, themeInfo.Manifest.Banner));
            }
            catch (Exception e)
            {
                themeInfo.IsError = true;
                themeInfo.Error = e;
                Logger.LogError(e, "无法加载主题元数据 {}", i);
            }
            Themes.Add(themeInfo);
        }
    }

    public async void RequestDownloadTheme(string id)
    {
        var item = Indexes.Select(i => i.Value.Themes
                .FirstOrDefault(x => x.Manifest.Id == id))
            .OfType<ThemeIndexItem>()
            .FirstOrDefault();
        var transaction = SentrySdk.StartTransaction("Download Theme", "theme.download");
        transaction.SetTag("theme.id", id);

        if (item == null)
        {
            Logger.LogWarning("找不到符合id的主题：{}", id);
            transaction.Finish(SpanStatus.NotFound);
            return;
        }
        transaction.SetTag("theme", item.Manifest.Name);

        if (DownloadTasks.ContainsKey(id))
        {
            Logger.LogWarning("{}已正在下载。", id);
            transaction.Finish(SpanStatus.AlreadyExists);
            return;
        }

        Logger.LogInformation("开始下载主题：{}", id);
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
        var destFileName = Path.Combine(ThemesPkgRootPath, id + ".zip");
        download.DownloadFileCompleted += (sender, args) =>
        {
            stopwatch.Stop();
            transaction.SetExtra("download.size", download.TotalFileSize);
            var speed = stopwatch.Elapsed.TotalSeconds == 0
                ? 0.0
                : download.TotalFileSize / stopwatch.Elapsed.TotalSeconds;
            transaction.SetExtra("download.bytesPerSecond", speed);
            if (args.Error != null)
            {
                spanDownload.Finish(args.Error, SpanStatus.InternalError);
                // 这里是 Downloader 的事件回调，抛出去的异常没有任何 await 能接住，
                // 会直接终止进程（表现为「下载主题时应用突然消失」）。
                // 把错误挂到任务上，由下面的 await StartAsync / catch 统一处理。
                Logger.LogError(args.Error, "无法下载主题 {ThemeId}", id);
                task.Exception = args.Error;
                return;
            }

            // 校验、改名同样可能失败（磁盘满、文件被占用、校验不匹配），
            // 事件回调里一律不能抛。
            try
            {
                spanDownload.Finish(SpanStatus.Ok);

                var spanValidateChecksum = transaction.StartChild("validate");
                ChecksumHelper.VerifyChecksum(archive, md5);
                spanValidateChecksum.Finish(SpanStatus.Ok);

                var spanMoveToCache = transaction.StartChild("moveToCache");
                File.Move(archive, destFileName, true);
                spanMoveToCache.Finish(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "主题 {ThemeId} 下载完成后的校验/落盘失败", id);
                task.Exception = ex;
            }
        };
        download.DownloadProgressChanged += (sender, args) =>
        {
            task.Progress = args.ProgressPercentage;
        };
        try
        {
            BindDownloadTasks();
            stopwatch.Start();
            await download.StartAsync(task.CancellationToken);
            // 事件回调里的错误只写到了 task.Exception，StartAsync 本身可能正常返回。
            // 不在这里补一次检查，就会拿着一个不存在的文件去 InstallTheme。
            if (task.Exception != null)
            {
                throw new InvalidOperationException($"主题 {id} 下载失败：{task.Exception.Message}", task.Exception);
            }
            if (!Themes.Any(x => x.Manifest.Id == id && EnabledThemes.Contains(id)))
            {
                InstallTheme(destFileName);
                LoadThemeSource();
            }
            else
            {
                if (MergedThemes.TryGetValue(id, out var plugin))
                {
                    plugin.RestartRequired = true;
                }
                item.RestartRequired = true;
                RestartRequested?.Invoke(this, EventArgs.Empty);
            }
            Logger.LogInformation("主题 {} 下载完成。", id);
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception e)
        {
            task.Exception = e;
            transaction.GetLastActiveSpan()?.Finish(e, SpanStatus.InternalError);
            transaction.Finish(e, SpanStatus.InternalError);
            Logger.LogError(e, "无法从 {} 下载主题 {}", url, id);
        }
        task.IsDownloading = false;
        DownloadTasks.Remove(id);
    }

    private void ProcessThemeInstall()
    {
        if (!Directory.Exists(ThemesPkgRootPath))
        {
            Directory.CreateDirectory(ThemesPkgRootPath);
        }
        if (!Directory.Exists(ThemesPath))
        {
            Directory.CreateDirectory(ThemesPath);
        }

        foreach (var pkgPath in Directory.EnumerateFiles(ThemesPkgRootPath).Where(x => Path.GetExtension(x) == ".zip"))
        {
            try
            {
                InstallTheme(pkgPath);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "无法安装主题 {}", pkgPath);
            }
        }

        foreach (var pkg in Directory.EnumerateDirectories(ThemesPath).Where(x => Path.Exists(Path.Combine(x, ".uninstall"))))
        {
            try
            {
                Directory.Delete(pkg, true);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "无法卸载主题 {}", pkg);
            }
        }
    }

    private static void InstallTheme(string pkgPath)
    {
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        using (var pkg = ZipFile.OpenRead(pkgPath))
        {
            var mf = pkg.GetEntry("manifest.yml");
            if (mf == null)
                return;
            using var reader = new StreamReader(mf.Open());
            var mfText = reader.ReadToEnd();
            var manifest = deserializer.Deserialize<PluginManifest>(mfText);
            var targetPath = Path.Combine(ThemesPath, manifest.Id);
            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, true);
            }

            Directory.CreateDirectory(targetPath);
            ZipFile.ExtractToDirectory(pkgPath, targetPath);
        }
        File.Delete(pkgPath);
    }

    public async Task PackageThemeAsync(string id, string outputPath)
    {
        if (Themes.All(x => x.Manifest.Id != id))
        {
            throw new ArgumentException($"找不到主题 {id}。", nameof(id));
        }
        await using var outputStream = File.Create(outputPath);
        await PackageThemeAsync(id, outputStream);
    }

    public async Task PackageThemeAsync(string id, Stream outputStream)
    {
        var plugin = Themes.FirstOrDefault(x => x.Manifest.Id == id);
        if (plugin == null)
        {
            throw new ArgumentException($"找不到主题 {id}。", nameof(id));
        }

        await Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(plugin.Path, outputStream);
        });
    }
}
