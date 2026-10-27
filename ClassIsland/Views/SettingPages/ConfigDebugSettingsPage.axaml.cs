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
[SettingsPageInfo("config-debug", "变量调试", "\ue943", "\ue942", SettingsPageCategory.Debug)]
public partial class ConfigDebugSettingsPage : SettingsPageBase
{
    public ConfigDebugSettingsViewModel ViewModel { get; } = IAppHost.GetService<ConfigDebugSettingsViewModel>();

    public ConfigDebugSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
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

    private void ConfigDebugSettingsPage_OnLoaded(object? sender, RoutedEventArgs e)
    {
        ViewModel.EnsureSubscribed();
    }

    private void ConfigDebugSettingsPage_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        ViewModel.Dispose();
    }
}
