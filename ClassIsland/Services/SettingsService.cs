using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Shared.Helpers;
using ClassIsland.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Helpers;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Services.Automation.Actions;
using ClassIsland.Services.Management;
using ClassIsland.Shared.Protobuf.AuditEvent;
using ClassIsland.Shared.Protobuf.Enum;

namespace ClassIsland.Services;

public class SettingsService(ILogger<SettingsService> Logger, IManagementService ManagementService)
    : INotifyPropertyChanged
{
    private Settings _settings = new();
    private bool _publishingExtractedAccent;

    private bool SkipMigration { get; set; } = false;

    public Settings Settings
    {
        get => _settings;
        set
        {
            if (OperatingSystem.IsAndroid() && value.MainWindowFont == MainWindow.DefaultFontFamilyKey)
            {
                value.MainWindowFont = MainWindow.PlatformDefaultFontFamilyKey;
            }
            SetField(ref _settings, value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool WillMigrateProfileTrustedState { get; set; } = false;

    public bool WillMigrateInitTutorial { get; set; } = false;

    private async Task LoadManagementSettingsAsync()
    {
        if (!ManagementService.Manifest.DefaultSettingsSource.IsNewerAndNotNull(ManagementService.Versions
                .DefaultSettingsVersion) ||
            ManagementService.Connection == null)
        {
            return;
        }

        Logger.LogInformation("拉取集控默认设置");
        var url = ManagementService.Manifest.DefaultSettingsSource.Value!;
        var settings = await ManagementService.Connection.GetJsonAsync<Settings>(url);
        Settings = settings;
        Settings.PropertyChanged += (sender, args) => SettingsChanged(args.PropertyName);
        Logger.LogTrace("拉取集控默认设置成功！");
    }

    public async Task LoadSettingsAsync()
    {
        if (Design.IsDesignMode)
        {
            // 设计时不加载设置
            Logger.LogInformation("检测到应用以设计模式运行，跳过配置加载");
            return;
        }
        try
        {
            if (!File.Exists(Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json")))
            {
                SkipMigration = true; // 如果是新的配置文件，那么就需要跳过迁移。
                Logger.LogInformation("配置文件不存在，跳过加载。");
            }
            else
            {
                Logger.LogInformation("加载配置文件。");
                var r = ConfigureFileHelper.LoadConfig<Settings>(Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json"));
                Settings = r;
                Settings.PropertyChanged += (sender, args) => SettingsChanged(args.PropertyName);
            }

            // 当还没有初始化应用且启用集控时，从集控拉取设置。
            if (ManagementService.IsManagementEnabled && !Settings.IsWelcomeWindowShowed)
            {
                await LoadManagementSettingsAsync();
            }

            ISpeechService.GlobalSettings = Settings;
        }
        catch (Exception ex)
        {
            SkipMigration = true;
            Logger.LogError(ex, "配置文件加载失败。");
            // ignored
        }

        if (Settings is { IsSystemSpeechSystemExist: false, SpeechSource: 0 })
        {
            Settings.SpeechSource = 1;
        }

        var requiresRestarting = false;
        if (!SkipMigration)
        {
            MigrateSettings(out requiresRestarting);
        }

        // 一次性迁移：旧版本中「提醒语音」「提醒音效」的总开关默认关闭，
        // 用户在提醒提供方里打开了语音/音效后仍然完全静音，且界面上没有任何提示。
        // 这里在升级后把这两个总开关打开一次，之后用户的修改会被正常保留。
        if (!Settings.HasMigratedNotificationAudioSettings)
        {
            if (!Settings.AllowNotificationSpeech || !Settings.AllowNotificationSound)
            {
                Settings.AllowNotificationSpeech = true;
                Settings.AllowNotificationSound = true;
                Logger.LogInformation("已将「提醒语音」「提醒音效」总开关迁移为开启状态。");
            }

            // 标记必须最后写。这个属性的 setter 会走 SettingsChanged → ScheduleSave，
            // 立刻把整个配置文件落盘一次；先写标记就等于先把「已完成」写进磁盘。
            // 万一在随后保存开关时崩溃/被杀，磁盘上会永久停在
            // 「已迁移，但两个开关仍然关着」，之后每次启动都跳过迁移 ——
            // 正是这个迁移想修的「开了语音却没声音」被固化下来。
            Settings.HasMigratedNotificationAudioSettings = true;
        }

        Settings.LastAppVersion = Assembly.GetExecutingAssembly().GetName().Version!;

        if (requiresRestarting)
        {
            AppBase.Current.Restart();
        }
    }

    private T TryGetDictionaryValue<T>(IDictionary<string, object?> dictionary, string key, T? fallbackValue = null)
        where T : class
    {
        var fallback = fallbackValue ?? Activator.CreateInstance<T>();
        var r = Settings.MiniInfoProviderSettings.TryGetValue(key.ToLower(), out var o);
        if (o is JsonElement o1)
        {
            return o1.Deserialize<T>() ?? fallback;
        }

        return (T?)Settings.MiniInfoProviderSettings[key.ToLower()] ?? fallback;
    }

#pragma warning disable CS0612 // 类型或成员已过时
    private void MigrateSettings(out bool requiresRestarting)
    {
        requiresRestarting = false;
        if (Assembly.GetExecutingAssembly().GetName().Version < Version.Parse("1.4.1.0"))
        {
            return;
        }
        
        if (Settings.LastAppVersion < Version.Parse("2.0.0.2") 
            || (Settings.LastAppVersion >= Version.Parse("2.0.1.0") && Settings.LastAppVersion < Version.Parse("2.0.1.2")) )
        {
            if (PlatformServices.DesktopService.IsUrlSchemeRegistered)
            {
                PlatformServices.DesktopService.IsUrlSchemeRegistered = true;
            }
            Logger.LogInformation("成功迁移了 2.0.0.2 以前的设置。");
        }
        
        if (Settings.LastAppVersion < Version.Parse("2.0.3.0") )
        {
            WillMigrateInitTutorial = true;
            Logger.LogInformation("成功迁移了 2.0.3.0 以前的设置。");
        }

        if (Settings.LastAppVersion < Version.Parse("2.0.3.3"))
        {
            if (PlatformServices.DesktopService.IsAutoStartEnabled)
            {
                PlatformServices.DesktopService.IsAutoStartEnabled = false;
                PlatformServices.DesktopService.IsAutoStartEnabled = true;
            }
            Logger.LogInformation("成功迁移了 2.0.3.3 以前的自启动快捷方式。");
        }
        
    }
#pragma warning restore CS0612 // 类型或成员已过时

    public void SaveSettings(string note = "")
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        // 延迟保存的定时器回调与退出时的立即保存可能并发，
        // 两边的临时文件路径相同，不串行化会互相覆盖/抛出文件占用异常。
        lock (_saveFileLock)
        {
            Logger.LogInformation(note == "" ? "写入配置文件。" : "写入配置文件：{}", note);
            ConfigureFileHelper.SaveConfig(Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json"), Settings);
        }
    }

    internal void SaveExtractedAccent(Avalonia.Media.Color color)
    {
        _publishingExtractedAccent = true;
        try
        {
            Settings.SetExtractedAccent(color, () => SaveSettings(nameof(Settings.WallpaperColorPlatte)));
        }
        finally
        {
            _publishingExtractedAccent = false;
        }
    }


    public const BindingFlags SettingsPropertiesFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// 设置保存的防抖间隔。拖动一次音量滑块、在字体输入框里敲一个词，
    /// 都会连续触发几十次 PropertyChanged；每次都把整份 Settings.json 序列化
    /// 并原子写盘（写临时文件 + fsync + 改名），会造成明显的 UI 卡顿和大量无谓写盘。
    /// </summary>
    private static readonly TimeSpan SaveDebounceInterval = TimeSpan.FromMilliseconds(400);

    private readonly object _saveTimerLock = new();
    private readonly object _saveFileLock = new();
    private Timer? _saveTimer;
    private string _pendingSaveNote = "";

    /// <summary>
    /// 合并短时间内的多次设置变更，只落盘一次。
    /// </summary>
    private void ScheduleSave(string note)
    {
        lock (_saveTimerLock)
        {
            _pendingSaveNote = note;
            _saveTimer ??= new Timer(_ => FlushPendingSave(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 执行挂起的延迟保存。定时器回调发生在后台线程，
    /// 这里必须吞掉所有异常：Timer 回调里逃逸的异常会直接终止进程。
    /// </summary>
    private void FlushPendingSave()
    {
        string note;
        lock (_saveTimerLock)
        {
            note = _pendingSaveNote;
            _pendingSaveNote = "";
        }

        // 定时器被重排过，本次回调已经没有待写内容：直接返回，避免白序列化一次。
        if (note.Length == 0)
        {
            return;
        }

        try
        {
            SaveSettings(note);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "延迟保存设置失败");
        }
    }

    /// <summary>
    /// 添加应用设置叠层。
    /// </summary>
    /// <param name="guid">设置叠层 Guid。</param>
    /// <param name="name">设置属性名称。</param>
    /// <param name="value">要设置的值。</param>
    /// <returns>是否成功。失败会抛出。</returns>
    public bool AddSettingsOverlay(Guid guid, string name, object? value)
    {
        var key = guid.ToString();
        var isPropertyOverlayNotNull = Settings.SettingsOverlays.TryGetValue(name, out var propertyOverlay);
        if (isPropertyOverlayNotNull)
            propertyOverlay!.Remove(key);

        var info = typeof(Settings).GetProperty(name, SettingsPropertiesFlags);
        if (info == null)
        {
            throw new KeyNotFoundException($"应用设置中找不到属性“{name}”。");
            return false;
        }

        if (!isPropertyOverlayNotNull || propertyOverlay!.Contains(key))
        {
            propertyOverlay = [];
            var sourceValue = info.GetValue(Settings);
            if (value == sourceValue)
                return true;
            propertyOverlay["@"] = sourceValue;
        }

        info.SetValue(Settings, value);
        propertyOverlay[key] = value;
        Settings.SettingsOverlays[name] = propertyOverlay;
        return true;
    }

    // PropertyInfo 缓存。
    private static readonly ConcurrentDictionary<string, PropertyInfo?> _propertyCache = new();

    public static PropertyInfo? GetPropertyInfoByName(string name) =>
        string.IsNullOrEmpty(name) ? null :
            _propertyCache.GetOrAdd(name,
                key => typeof(Settings).GetProperty(key, SettingsPropertiesFlags));

    /// <summary>
    /// 删除应用设置叠层。
    /// </summary>
    /// <param name="guid">设置叠层 Guid。</param>
    /// <param name="name">设置属性名。</param>
    /// <returns>是否进行了删除。删除出错会抛出。</returns>
    public bool RemoveSettingsOverlay(Guid guid, string name)
    {
        if (!Settings.SettingsOverlays.TryGetValue(name, out var propertyOverlay))
            return false;

        var lengthBefore = propertyOverlay.Count;
        propertyOverlay.Remove(guid.ToString());
        var length = propertyOverlay.Count;
        if (length == lengthBefore)
            return false;

        var info = typeof(Settings).GetProperty(name, SettingsPropertiesFlags);
        if (info == null)
            return false;

        var last = propertyOverlay[length - 1]; // propertyOverlay 至少存在一项。
        if (last is JsonElement json)
            last = json.Deserialize(info.PropertyType);
        info.SetValue(Settings, last);

        if (length > 1)
            Settings.SettingsOverlays[name] = propertyOverlay;
        else
            Settings.SettingsOverlays.Remove(name);
        return true;
    }

    void SettingsChanged(string? propertyName)
    {
        // PropertyChanged 允许把 PropertyName 留空（表示「所有属性都变了」），
        // 不少 MVVM 框架的批量通知就是这么发的。直接拿 null 去
        // Dictionary.Remove / Type.GetProperty 会抛 ArgumentNullException，
        // 而这里是在事件回调里，异常会一路冒到触发属性变更的调用方。
        if (string.IsNullOrEmpty(propertyName))
        {
            return;
        }

        if (_publishingExtractedAccent && propertyName is nameof(Settings.WallpaperColorPlatte) or nameof(Settings.SelectedPlatteIndex))
            return;

        if (propertyName != nameof(Settings.SettingsOverlays))
        {
            Settings.SettingsOverlays.Remove(propertyName);
        }

        if (typeof(Settings).GetProperty(propertyName, SettingsPropertiesFlags)?
                            .GetCustomAttribute<JsonIgnoreAttribute>() != null)
            return;

        // 高频属性变更（滑块、文本框）交给防抖器合并，避免每敲一个字符写一次盘。
        ScheduleSave(propertyName);
        if (ManagementService is { IsManagementEnabled: true, Connection: ManagementServerConnection connection })
        {
            connection.LogAuditEvent(AuditEvents.AppSettingsUpdated, new AppSettingsUpdated()
            {
                PropertyName = propertyName
            });
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
