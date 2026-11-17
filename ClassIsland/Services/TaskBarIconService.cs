using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

public class TaskBarIconService : IHostedService, ITaskBarIconService
{
    private static readonly TimeSpan TemporaryVisibleDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InteractionHideDelay = TimeSpan.FromMilliseconds(900);

    private readonly SettingsService _settingsService;
    private Settings? _subscribedSettings;
    private NativeMenu? _moreOptionsMenu;
    private bool _isTemporaryVisible;
    private bool _menuOpen;
    private bool _hideAfterMenuClose;
    private int _hideCountdownVersion;

    public TaskBarIconService(ILogger<TaskBarIconService> logger, SettingsService settingsService)
    {
        Logger = logger;
        _settingsService = settingsService;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            MainTaskBarIcon.Icon = new WindowIcon(OperatingSystem.IsMacOS()
                ? "../Resources/Assets/AppLogo_Monochrome.png"
                : "Assets/AppLogo.png");

            // Avalonia 12.1.4 会在注册时绑定菜单，macOS 需等 PostInit 设置最终菜单再注册。
            if (!OperatingSystem.IsMacOS())
            {
                var icons = TrayIcon.GetIcons(AppBase.Current) ?? new TrayIcons();
                icons.Add(MainTaskBarIcon);
                TrayIcon.SetIcons(AppBase.Current, icons);
            }
        }

        MainTaskBarIcon.Clicked += MainTaskBarIconOnClicked;

        // Settings 对象会在加载配置时整体替换，替换后需要重新挂接属性变更。
        _settingsService.PropertyChanged += SettingsServiceOnPropertyChanged;
        SubscribeSettings(_settingsService.Settings);
        ApplyVisibility();
        AppBase.Current.AppStopping += CurrentOnAppStopping;
    }

    private void SettingsServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsService.Settings))
        {
            return;
        }

        SubscribeSettings(_settingsService.Settings);
        ApplyVisibility();
    }

    private void SubscribeSettings(Settings settings)
    {
        if (ReferenceEquals(_subscribedSettings, settings))
        {
            return;
        }

        if (_subscribedSettings != null)
        {
            _subscribedSettings.PropertyChanged -= SettingsOnPropertyChanged;
        }

        _subscribedSettings = settings;
        _subscribedSettings.PropertyChanged += SettingsOnPropertyChanged;
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.IsTaskBarIconVisible))
        {
            ApplyVisibility();
        }
    }

    private void ApplyVisibility()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyVisibilityCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyVisibilityCore);
        }
    }

    private void ApplyVisibilityCore()
    {
        var isVisible = _settingsService.Settings.IsTaskBarIconVisible;
        if (isVisible)
        {
            // 用户改回了常驻显示，临时状态即可作废。
            ResetTemporaryVisibility();
        }

        // 设置要求隐藏时，仍可能在临时显示窗口期内保持可见。
        MainTaskBarIcon.IsVisible = isVisible || _isTemporaryVisible;
    }

    /// <summary>
    /// 临时显示托盘图标，但不改变「显示托盘图标」设置。
    /// 图标会在用户使用一次（点击或关闭菜单）后自动隐藏；若一直没有操作，
    /// 则在 <see cref="TemporaryVisibleDuration"/> 后自动隐藏。
    /// </summary>
    public void ShowTemporarily()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowTemporarilyCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ShowTemporarilyCore);
        }
    }

    private void ShowTemporarilyCore()
    {
        if (_settingsService.Settings.IsTaskBarIconVisible)
        {
            // 用户本来就要求常驻显示，不需要临时窗口期。
            return;
        }

        _isTemporaryVisible = true;
        _hideAfterMenuClose = false;
        MainTaskBarIcon.IsVisible = true;
        RestartHideCountdown(TemporaryVisibleDuration);
        Logger.LogInformation("托盘图标已临时显示，将在 {Seconds} 秒后自动隐藏。",
            TemporaryVisibleDuration.TotalSeconds);
    }

    private void MainTaskBarIconOnClicked(object? sender, EventArgs e)
    {
        if (!_isTemporaryVisible || _menuOpen)
        {
            return;
        }

        // 点击行为可能会打开窗口或菜单，留一点时间让它们出现，再收起图标。
        RestartHideCountdown(InteractionHideDelay);
    }

    private void MoreOptionsMenuOnOpening(object? sender, EventArgs e)
    {
        if (!_isTemporaryVisible)
        {
            return;
        }

        _menuOpen = true;
        // 菜单打开期间不要按点击延迟收起，否则会把刚打开的菜单一起关掉。
        _hideCountdownVersion++;
        _hideAfterMenuClose = true;
    }

    private void MoreOptionsMenuOnClosed(object? sender, EventArgs e)
    {
        _menuOpen = false;
        if (!_isTemporaryVisible || !_hideAfterMenuClose)
        {
            return;
        }

        HideTemporarily();
    }

    private void RestartHideCountdown(TimeSpan delay)
    {
        var version = ++_hideCountdownVersion;
        _ = HideAfterDelayAsync(delay, version);
    }

    private async Task HideAfterDelayAsync(TimeSpan delay, int version)
    {
        await Task.Delay(delay);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _hideCountdownVersion || !_isTemporaryVisible)
            {
                return;
            }

            if (_menuOpen)
            {
                // 菜单还开着，等它关闭时再收起。
                _hideAfterMenuClose = true;
                return;
            }

            HideTemporarily();
        });
    }

    private void HideTemporarily()
    {
        if (!_isTemporaryVisible)
        {
            return;
        }

        _isTemporaryVisible = false;
        _hideAfterMenuClose = false;
        _hideCountdownVersion++;
        MainTaskBarIcon.IsVisible = _settingsService.Settings.IsTaskBarIconVisible;
        Logger.LogInformation("托盘图标已自动隐藏。");
    }

    private void ResetTemporaryVisibility()
    {
        _isTemporaryVisible = false;
        _menuOpen = false;
        _hideAfterMenuClose = false;
        _hideCountdownVersion++;
    }

    private void CurrentOnAppStopping(object? sender, EventArgs e)
    {
        _settingsService.PropertyChanged -= SettingsServiceOnPropertyChanged;
        if (_subscribedSettings != null)
        {
            _subscribedSettings.PropertyChanged -= SettingsOnPropertyChanged;
        }

        ResetTemporaryVisibility();
        MainTaskBarIcon.Clicked -= MainTaskBarIconOnClicked;
        MainTaskBarIcon.IsVisible = false;
    }

    public ILogger<TaskBarIconService> Logger { get; }

    public TrayIcon MainTaskBarIcon
    {
        get;
    } = new()
    {
        ToolTipText = "ClassIsland"
    };

    public IList<NativeMenuItemBase> MoreOptionsMenuItems => MoreOptionsMenu!.Items;

    public NativeMenu? MoreOptionsMenu
    {
        get => _moreOptionsMenu;
        set
        {
            if (ReferenceEquals(_moreOptionsMenu, value))
            {
                return;
            }

            if (_moreOptionsMenu != null)
            {
                _moreOptionsMenu.Opening -= MoreOptionsMenuOnOpening;
                _moreOptionsMenu.Closed -= MoreOptionsMenuOnClosed;
            }

            _moreOptionsMenu = value;

            if (_moreOptionsMenu != null)
            {
                _moreOptionsMenu.Opening += MoreOptionsMenuOnOpening;
                _moreOptionsMenu.Closed += MoreOptionsMenuOnClosed;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        return;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        return;
    }
}
