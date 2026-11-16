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
    private readonly SettingsService _settingsService;
    private Settings? _subscribedSettings;

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
        var isVisible = _settingsService.Settings.IsTaskBarIconVisible;
        if (Dispatcher.UIThread.CheckAccess())
        {
            MainTaskBarIcon.IsVisible = isVisible;
        }
        else
        {
            Dispatcher.UIThread.Post(() => MainTaskBarIcon.IsVisible = isVisible);
        }
    }

    private void CurrentOnAppStopping(object? sender, EventArgs e)
    {
        _settingsService.PropertyChanged -= SettingsServiceOnPropertyChanged;
        if (_subscribedSettings != null)
        {
            _subscribedSettings.PropertyChanged -= SettingsOnPropertyChanged;
        }

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
    
    public NativeMenu? MoreOptionsMenu { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        return;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        return;
    }
}
