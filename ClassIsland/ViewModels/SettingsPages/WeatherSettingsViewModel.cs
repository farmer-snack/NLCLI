using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Models.Weather;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Helpers;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Services;
using ClassIsland.Views.SettingPages;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ViewModels.SettingsPages;

public sealed record OmDayItem(string Label, string Emoji, string TempRange, string Precip);
public sealed record OmHourItem(string Label, string Emoji, string Temp, string Precip);
public sealed record OmMinutelyItem(string Label, string Precip, string Probability);

public partial class WeatherSettingsViewModel : ObservableRecipient
{
    private readonly SettingsService _settingsService;
    private readonly ILocationService _locationService;
    private readonly IWeatherService _weatherService;
    private readonly ILogger<WeatherSettingsPage> _logger;
    private readonly DispatcherTimer _searchDebounceTimer;
    private string _pendingSearchText = string.Empty;
    private bool _isActivated;

    public SettingsService SettingsService => _settingsService;
    public ILocationService LocationService => _locationService;
    public IWeatherService WeatherService => _weatherService;
    public ILogger<WeatherSettingsPage> Logger => _logger;

    [ObservableProperty] private List<City> _citySearchResults = new();
    [ObservableProperty] private bool _isSearchingWeather;
    [ObservableProperty] private bool _isRefreshingWeather;
    [ObservableProperty] private bool _isLocationUpdating;
    [ObservableProperty] private int _selectedLocationSource;
    [ObservableProperty] private bool _hideLocationPos = true;

    // ---- Open-Meteo 详细预报（16 天 / 24 小时 / 15 分钟）----
    [ObservableProperty] private bool _isOpenMeteoLoading;
    [ObservableProperty] private string? _openMeteoError;
    [ObservableProperty] private string _omCurrentTemp = "--";
    [ObservableProperty] private string _omCurrentText = "";
    [ObservableProperty] private string _omCurrentEmoji = "";
    [ObservableProperty] private string _omCurrentDetail = "";
    [ObservableProperty] private string _omUpdatedAt = "";
    [ObservableProperty] private List<OmHourItem> _omHours = new();
    [ObservableProperty] private List<OmMinutelyItem> _omMinutes = new();
    [ObservableProperty] private List<OmDayItem> _omDays = new();
    public bool IsLocationPosVisible => !_hideLocationPos;
        partial void OnHideLocationPosChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocationPosVisible));
    }


    public WeatherSettingsViewModel(
        SettingsService settingsService,
        ILocationService locationService,
        IWeatherService weatherService,
        ILogger<WeatherSettingsPage> logger)
    {
        _settingsService = settingsService;
        _locationService = locationService;
        _weatherService = weatherService;
        _logger = logger;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        SelectedLocationSource = settingsService.Settings.WeatherLocationSource;
    }

    /// <summary>
    /// 订阅全局设置变更并启动搜索防抖计时。页面加载时调用。
    /// </summary>
    public void Activate()
    {
        if (_isActivated)
        {
            return;
        }

        _isActivated = true;
        _settingsService.Settings.PropertyChanged += SettingsOnPropertyChanged;
    }

    /// <summary>
    /// 释放对全局设置对象的订阅，避免 ViewModel 被静态服务长期保留。页面卸载时调用。
    /// </summary>
    public void Deactivate()
    {
        if (!_isActivated)
        {
            return;
        }

        _isActivated = false;
        _searchDebounceTimer.Stop();
        _settingsService.Settings.PropertyChanged -= SettingsOnPropertyChanged;
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsService.Settings.WeatherLocationSource))
        {
            SelectedLocationSource = _settingsService.Settings.WeatherLocationSource;
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            CitySearchResults = await _weatherService.GetCitiesByName(string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化搜索失败");
        }

        await LoadOpenMeteoAsync();
    }

    /// <summary>
    /// 拉取 Open-Meteo 的详细预报（16 天日预报、24 小时逐小时、未来 2 小时逐 15 分钟）。
    /// 与小米天气那条链路互不影响：这里失败只影响这个面板，不影响桌面组件。
    /// </summary>
    public async Task LoadOpenMeteoAsync()
    {
        IsOpenMeteoLoading = true;
        OpenMeteoError = null;
        try
        {
            var lat = _settingsService.Settings.WeatherLatitude.ToString(CultureInfo.InvariantCulture);
            var lon = _settingsService.Settings.WeatherLongitude.ToString(CultureInfo.InvariantCulture);
            var url = "https://api.open-meteo.com/v1/forecast"
                      + $"?latitude={lat}&longitude={lon}"
                      + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m"
                      + "&hourly=temperature_2m,weather_code,precipitation_probability"
                      + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                      + "&minutely_15=precipitation,precipitation_probability"
                      + "&timezone=auto&forecast_days=16&forecast_hours=24&forecast_minutely_15=8";

            var data = await WebRequestHelper.Default.GetJson<OpenMeteoForecast>(new Uri(url));
            ApplyOpenMeteo(data);
        }
        catch (Exception ex)
        {
            OpenMeteoError = $"详细预报加载失败：{ex.Message}";
            _logger.LogWarning(ex, "Open-Meteo 请求失败");
        }
        finally
        {
            IsOpenMeteoLoading = false;
        }
    }

    private void ApplyOpenMeteo(OpenMeteoForecast data)
    {
        var current = data.Current;
        OmCurrentTemp = current.Temperature.ToString("0");
        OmCurrentText = WeatherCodeHelper.GetText(current.WeatherCode);
        OmCurrentEmoji = WeatherCodeHelper.GetEmoji(current.WeatherCode);
        OmCurrentDetail = string.Format(CultureInfo.InvariantCulture,
            "体感 {0:0}° · 湿度 {1:0}% · 风速 {2:0} km/h",
            current.ApparentTemperature, current.Humidity, current.WindSpeed);
        OmUpdatedAt = string.IsNullOrWhiteSpace(current.Time)
            ? ""
            : $"更新于 {current.Time.Replace('T', ' ')}";

        var hours = new List<OmHourItem>();
        for (var i = 0; i < data.Hourly.Time.Count && hours.Count < 24; i++)
        {
            var time = ParseTime(data.Hourly.Time[i]);
            if (time is null) continue;
            var code = At(data.Hourly.WeatherCode, i);
            var temp = At(data.Hourly.Temperature, i);
            var pop = At(data.Hourly.PrecipitationProbability, i);
            hours.Add(new OmHourItem(time.Value.ToString("HH时"), WeatherCodeHelper.GetEmoji(code),
                temp.ToString("0") + "°", pop is null ? "" : pop + "%"));
        }
        OmHours = hours;

        var minutes = new List<OmMinutelyItem>();
        for (var i = 0; i < data.Minutely15.Time.Count; i++)
        {
            var time = ParseTime(data.Minutely15.Time[i]);
            if (time is null) continue;
            var precipitation = At(data.Minutely15.Precipitation, i);
            var pop = At(data.Minutely15.PrecipitationProbability, i);
            minutes.Add(new OmMinutelyItem(time.Value.ToString("HH:mm"),
                precipitation.ToString("0.0") + " mm", pop is null ? "" : pop + "%"));
        }
        OmMinutes = minutes;

        var days = new List<OmDayItem>();
        for (var i = 0; i < data.Daily.Time.Count; i++)
        {
            var date = ParseTime(data.Daily.Time[i]);
            if (date is null) continue;
            var code = At(data.Daily.WeatherCode, i);
            var max = At(data.Daily.TemperatureMax, i);
            var min = At(data.Daily.TemperatureMin, i);
            var pop = At(data.Daily.PrecipitationProbabilityMax, i);
            days.Add(new OmDayItem(DayLabel(date.Value.Date),
                WeatherCodeHelper.GetEmoji(code),
                $"{min:0}° ~ {max:0}°",
                pop is null ? "" : pop + "%"));
        }
        OmDays = days;
    }

    private static DateTime? ParseTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static T At<T>(List<T> list, int index) => index >= 0 && index < list.Count ? list[index] : default!;

    private static string DayLabel(DateTime date)
    {
        var offset = (date - DateTime.Today).Days;
        return offset switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            _ => "周" + "日一二三四五六"[(int)date.DayOfWeek]
        };
    }

    public void OnSearchTextChanged(string? searchText)
    {
        _pendingSearchText = searchText ?? string.Empty;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        IsSearchingWeather = true;
        try
        {
            CitySearchResults = await _weatherService.GetCitiesByName(_pendingSearchText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索城市失败");
        }
        finally
        {
            IsSearchingWeather = false;
        }
    }

    [RelayCommand]
    private async Task RefreshWeatherAsync()
    {
        IsRefreshingWeather = true;
        try
        {
            await _weatherService.QueryWeatherAsync();
            await LoadOpenMeteoAsync();
        }
        finally
        {
            IsRefreshingWeather = false;
        }
    }

    public async Task<WeatherQueryResult> RefreshWeatherWithResultAsync()
    {
        IsRefreshingWeather = true;
        try
        {
            return await _weatherService.QueryWeatherWithResultAsync();
        }
        finally
        {
            IsRefreshingWeather = false;
        }
    }

    public async Task SelectCityAsync(City? city)
    {
        if (city == null) return;
        _settingsService.Settings.CityName = city.Name;
        await RefreshWeatherAsync();
    }

    [RelayCommand]
    public async Task GetCurrentPositionAsync()
    {
        IsLocationUpdating = true;
        try
        {
            var pos = await _locationService.GetLocationAsync();
            _settingsService.Settings.WeatherLongitude = Math.Round(pos.Longitude, 4);
            _settingsService.Settings.WeatherLatitude = Math.Round(pos.Latitude, 4);
            await RefreshWeatherAsync();
        }
        finally
        {
            IsLocationUpdating = false;
        }
    }

    public void ShowLocationCoordinates()
    {
        HideLocationPos = false;
    }

    partial void OnSelectedLocationSourceChanged(int value)
    {
        if (value is not (0 or 1) ||
            value == _settingsService.Settings.WeatherLocationSource)
        {
            return;
        }
        _settingsService.Settings.WeatherLocationSource = value;
        if (value == 1)
        {
            _ = RefreshWeatherAsync();
        }
    }
}
