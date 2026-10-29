using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.ViewModels.SettingsPages;
using ClassIsland.Shared;

namespace ClassIsland.Views.SettingPages;

/// <summary>
/// WeatherDetailSettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("weather-detail", "天气详情", "\uf465", "\uf464", SettingsPageCategory.Internal)]
public partial class WeatherDetailSettingsPage : SettingsPageBase
{
    public WeatherDetailSettingsViewModel ViewModel { get; } = IAppHost.GetService<WeatherDetailSettingsViewModel>();

    public WeatherDetailSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private async void WeatherDetailSettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadOpenMeteoAsync();
    }

    private async void ButtonRefresh_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
    }
}
