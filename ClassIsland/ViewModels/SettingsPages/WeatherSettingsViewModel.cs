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

public sealed record OmDayItem(string Label, string Emoji, string TempRange, string Precip, string Wind, string Uv);
public sealed record OmHourItem(string Label, string Emoji, string Temp, string Precip);
public sealed record OmMinutelyItem(string Label, string Precip, string Probability);
public sealed record OmMetricItem(string Label, string Value);
public sealed record OmAqiItem(string Label, string Value);

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
    [ObservableProperty] private List<OmMetricItem> _omMetrics = new();
    [ObservableProperty] private List<OmAqiItem> _omAqi = new();
    [ObservableProperty] private string _omSunTimes = "";
    [ObservableProperty] private string _omAqiLevel = "";
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
    /// 拉取 Open-Meteo 的详细预报（16 天日预报、24 小时逐小时、未来 2 小时逐 15 分钟），
    /// 再单独拉一次空气质量。空气质量失败不影响主预报。
    /// 与小米天气那条链路互不影响：这里失败只影响这个面板，不影响桌面组件和提醒。
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
                      + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,weather_code,is_day,precipitation,rain,showers,snowfall,cloud_cover,pressure_msl,surface_pressure,wind_speed_10m,wind_direction_10m,wind_gusts_10m"
                      + "&hourly=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,weather_code,precipitation_probability,precipitation,cloud_cover,visibility,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index,is_day"
                      + "&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,sunrise,sunset,daylight_duration,sunshine_duration,uv_index_max,precipitation_sum,rain_sum,snowfall_sum,precipitation_hours,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant"
                      + "&minutely_15=temperature_2m,weather_code,precipitation,rain,snowfall,wind_speed_10m"
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

        await LoadAirQualityAsync();
    }

    private async Task LoadAirQualityAsync()
    {
        try
        {
            var lat = _settingsService.Settings.WeatherLatitude.ToString(CultureInfo.InvariantCulture);
            var lon = _settingsService.Settings.WeatherLongitude.ToString(CultureInfo.InvariantCulture);
            var url = "https://air-quality-api.open-meteo.com/v1/air-quality"
                      + $"?latitude={lat}&longitude={lon}"
                      + "&current=european_aqi,us_aqi,pm10,pm2_5,carbon_monoxide,nitrogen_dioxide,sulphur_dioxide,ozone"
                      + "&timezone=auto";
            var data = await WebRequestHelper.Default.GetJson<OpenMeteoAirQuality>(new Uri(url));
            ApplyAirQuality(data);
        }
        catch (Exception ex)
        {
            OmAqi = new List<OmAqiItem>();
            OmAqiLevel = "";
            _logger.LogWarning(ex, "Open-Meteo 空气质量请求失败");
        }
    }

    private void ApplyAirQuality(OpenMeteoAirQuality data)
    {
        var a = data.Current;
        OmAqiLevel = AqiLevel(a.EuropeanAqi);
        OmAqi = new List<OmAqiItem>
        {
            new("欧标 AQI", F(a.EuropeanAqi, "0")),
            new("美标 AQI", F(a.UsAqi, "0")),
            new("PM2.5", F(a.Pm2_5, "0", " μg/m³")),
            new("PM10", F(a.Pm10, "0", " μg/m³")),
            new("臭氧 O₃", F(a.Ozone, "0", " μg/m³")),
            new("二氧化氮 NO₂", F(a.NitrogenDioxide, "0", " μg/m³")),
            new("二氧化硫 SO₂", F(a.SulphurDioxide, "0", " μg/m³")),
            new("一氧化碳 CO", F(a.CarbonMonoxide, "0", " μg/m³"))
        };
    }

    private void ApplyOpenMeteo(OpenMeteoForecast data)
    {
        var current = data.Current;
        var daily = data.Daily;
        var hourly = data.Hourly;

        OmCurrentTemp = F(current.Temperature, "0");
        OmCurrentText = WeatherCodeHelper.GetText(current.WeatherCode);
        OmCurrentEmoji = WeatherCodeHelper.GetEmoji(current.WeatherCode);
        OmUpdatedAt = string.IsNullOrWhiteSpace(current.Time)
            ? ""
            : $"更新于 {current.Time.Replace('T', ' ')}";

        // 今天的高低温 / 日出日落
        var high = daily.TemperatureMax.Count > 0 ? F(daily.TemperatureMax[0], "0", "°") : "--";
        var low = daily.TemperatureMin.Count > 0 ? F(daily.TemperatureMin[0], "0", "°") : "--";
        var sunrise = daily.Sunrise.Count > 0 ? Clock(daily.Sunrise[0]) : "--";
        var sunset = daily.Sunset.Count > 0 ? Clock(daily.Sunset[0]) : "--";
        OmSunTimes = $"日出 {sunrise} · 日落 {sunset}";
        OmCurrentDetail = $"体感 {F(current.ApparentTemperature, "0", "°")} · 今日 {low} ~ {high} · {OmSunTimes}";

        var sunshineHours = daily.SunshineDuration.Count > 0 && daily.SunshineDuration[0] is { } seconds
            ? seconds / 3600
            : (double?)null;
        var visibility = hourly.Visibility.Count > 0 && hourly.Visibility[0] is { } vis ? vis / 1000 : (double?)null;
        var uv = hourly.UvIndex.Count > 0 ? hourly.UvIndex[0] : null;

        OmMetrics = new List<OmMetricItem>
        {
            new("湿度", F(current.Humidity, "0", "%")),
            new("露点", F(current.DewPoint, "0", "°")),
            new("气压", F(current.PressureMsl, "0", " hPa")),
            new("能见度", F(visibility, "0.0", " km")),
            new("风速", F(current.WindSpeed, "0", " km/h")),
            new("阵风", F(current.WindGusts, "0", " km/h")),
            new("风向", Dir(current.WindDirection)),
            new("体感", F(current.ApparentTemperature, "0", "°")),
            new("云量", F(current.CloudCover, "0", "%")),
            new("降水量", F(current.Precipitation, "0.0", " mm")),
            new("日照时长", F(sunshineHours, "0.0", " h")),
            new("紫外线", F(uv, "0.0"))
        };

        var hours = new List<OmHourItem>();
        for (var i = 0; i < hourly.Time.Count && hours.Count < 24; i++)
        {
            var time = ParseTime(hourly.Time[i]);
            if (time is null) continue;
            var code = At(hourly.WeatherCode, i);
            var temp = At(hourly.Temperature, i);
            var pop = At(hourly.PrecipitationProbability, i);
            hours.Add(new OmHourItem(time.Value.ToString("HH时"), WeatherCodeHelper.GetEmoji(code),
                F(temp, "0", "°"), pop is null ? "" : pop + "%"));
        }
        OmHours = hours;

        var minutes = new List<OmMinutelyItem>();
        for (var i = 0; i < data.Minutely15.Time.Count; i++)
        {
            var time = ParseTime(data.Minutely15.Time[i]);
            if (time is null) continue;
            var precipitation = At(data.Minutely15.Precipitation, i);
            // 15 分钟粒度没有 precipitation_probability（那是 hourly 才有的字段），这里只显示降水量。
            minutes.Add(new OmMinutelyItem(time.Value.ToString("HH:mm"),
                F(precipitation, "0.0", " mm"), ""));
        }
        OmMinutes = minutes;

        var days = new List<OmDayItem>();
        for (var i = 0; i < daily.Time.Count; i++)
        {
            var date = ParseTime(daily.Time[i]);
            if (date is null) continue;
            var code = At(daily.WeatherCode, i);
            var max = At(daily.TemperatureMax, i);
            var min = At(daily.TemperatureMin, i);
            var pop = At(daily.PrecipitationProbabilityMax, i);
            var sum = At(daily.PrecipitationSum, i);
            var wind = At(daily.WindSpeedMax, i);
            var windDir = At(daily.WindDirectionDominant, i);
            var uvMax = At(daily.UvIndexMax, i);
            var precipText = (pop is null ? "--" : pop + "%") + " · " + F(sum, "0.0", "mm");
            var windText = F(wind, "0", " km/h") + " " + Dir(windDir);
            var uvText = F(uvMax, "0.0");
            days.Add(new OmDayItem(DayLabel(date.Value.Date), WeatherCodeHelper.GetEmoji(code),
                $"{F(min, "0", "°")} ~ {F(max, "0", "°")}", precipText, windText, uvText));
        }
        OmDays = days;
    }

    private static string F(double? value, string format, string unit = "")
        => value is null ? "--" : value.Value.ToString(format, CultureInfo.InvariantCulture) + unit;

    private static string Dir(double? degrees)
    {
        if (degrees is null) return "--";
        string[] names = ["北", "东北", "东", "东南", "南", "西南", "西", "西北"];
        var index = (int)Math.Round(degrees.Value / 45) % 8;
        return names[(index + 8) % 8] + "风";
    }

    private static string AqiLevel(double? aqi)
    {
        if (aqi is null) return "—";
        var v = aqi.Value;
        return v <= 20 ? "优" : v <= 40 ? "良" : v <= 60 ? "中等" : v <= 80 ? "差" : v <= 100 ? "很差" : "极差";
    }

    private static string Clock(string value)
    {
        var time = ParseTime(value);
        return time?.ToString("HH:mm") ?? "--";
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
