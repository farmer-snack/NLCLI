using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using Avalonia.Controls;
using Avalonia.Interactivity;

using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Models.Debugging;
using ClassIsland.Shared;
using ClassIsland.ViewModels.SettingsPages;

namespace ClassIsland.Views.SettingPages;

/// <summary>
/// 「变量调试」设置页：列出并编辑 Settings.json 中的全部配置变量。
/// </summary>
[SettingsPageInfo(PageId, "变量调试", "\ue943", "\ue942", SettingsPageCategory.Debug)]
public partial class ConfigDebugSettingsPage : SettingsPageBase
{
    /// <summary>
    /// 本页面的注册 ID。设置窗口会用它做例外处理：本页不受「启用调试菜单」开关影响，始终显示。
    /// </summary>
    public const string PageId = "config-debug";

    public ConfigDebugSettingsViewModel ViewModel { get; } = IAppHost.GetService<ConfigDebugSettingsViewModel>();

    public ConfigDebugSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfigDebugSettingsViewModel.DevelopmentUpdateLog))
        {
            return;
        }

        // 日志是持续追加的，把光标放到末尾让视图自动跟随最新一行。
        DevelopmentUpdateLogBox.CaretIndex = DevelopmentUpdateLogBox.Text?.Length ?? 0;
    }

    private async void ButtonDevelopmentUpdate_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.StartDevelopmentUpdateAsync();
    }

    private async void ButtonPublish_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.StartPublishAsync();
    }

    private async void ButtonRefreshRemoteBranches_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshRemoteBranchesAsync();
    }

    private void ButtonCancelDevelopmentUpdate_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.CancelDevelopmentUpdate();
    }

    private async void ButtonCheckNativeDependencies_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CheckNativeDependenciesAsync();
    }

    private async void ButtonRepairNativeDependencies_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RepairNativeDependenciesAsync();
    }

    private void ButtonOpenRuntimesDirectory_OnClick(object? sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", directory);
            }
            else
            {
                Process.Start("xdg-open", directory);
            }
        }
        catch
        {
            // 打不开文件管理器时忽略，路径会显示在状态文本里。
        }
    }

    private void ButtonResetVariable_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigVariableItem item })
        {
            ViewModel.ResetVariable(item);
        }
    }

    private void ButtonRestart_OnClick(object? sender, RoutedEventArgs e)
    {
        RequestRestart();
    }

    /// <summary>
    /// 打开 fixxed.txt 的 GitHub 主源或 jsDelivr CDN 备用源。
    /// </summary>
    private void HyperlinkOpenLink_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // 打不开浏览器时忽略；页面上的完整地址可以直接复制。
        }
    }

    private void ConfigDebugSettingsPage_OnLoaded(object? sender, RoutedEventArgs e)
    {
        ViewModel.EnsureSubscribed();
        // 不再在进入页面时自动探测远程分支：GetRemoteBranchesAsync 会串行执行
        // git ls-remote（直连失败还要逐个尝试镜像），网络慢时点进本页会明显卡住。
        // 分支列表改由用户点击「刷新」按钮时按需获取，避免无谓的阻塞和网络开销。
    }

    private void ConfigDebugSettingsPage_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        ViewModel.Dispose();
    }
}
