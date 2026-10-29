using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClassIsland.Core.Models.Weather;
using ClassIsland.Helpers;
using ClassIsland.Services;
using ClassIsland.Services.Weather;
using ClassIsland.Views.SettingPages;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ViewModels.SettingsPages;

public sealed record OmHourItem(string Label, string Emoji, string Temp, string Precip, List<OmMetricItem> Detail);
public sealed record OmMinutelyItem(string Label, string Precip, string Probability);
public sealed record OmMetricItem(string Label, string Value);
public sealed record OmAqiItem(string Label, string Value);

/// <summary>
/// 一天的预报：概览 + 这一天的小时列表 + 该日的完整字段。
/// 内部的 <see cref="SelectedHour"/> 用于「点一个小时看它的全部数据」。
/// </summary>
public partial class OmDayItem(
    string label,
    string dateText,
    string emoji,
    string tempRange,
    string precip,
    string wind,
    string uv,
    string summary,
    List<OmHourItem> hours,
    List<OmMetricItem> detail)
    : ObservableObject
{
    public string Label { get; } = label;
    public string DateText { get; } = dateText;
    public string Emoji { get; } = emoji;
    public string TempRange { get; } = tempRange;
    public string Precip { get; } = precip;
    public string Wind { get; } = wind;
    public string Uv { get; } = uv;
    public string Summary { get; } = summary;
    public List<OmHourItem> Hours { get; } = hours;
    public List<OmMetricItem> Detail { get; } = detail;
    public string HourCountText => $"{Hours.Count} 小时";

    [ObservableProperty] private OmHourItem? _selectedHour;
}

/// <summary>
/// 「天气详情」设置页的 ViewModel：只负责 Open-Meteo 的数据拉取与格式化，
/// 不读写 <see cref="Settings.LastWeatherInfo"/>，与原有的天气链路完全隔离。
/// </summary>
public partial class WeatherDetailSettingsViewModel : ObservableObject
{
    /// <summary>
    /// 本地缓存的保鲜期：30 分钟内再次打开页面直接使用本地结果，不发起请求。
    /// </summary>
    private const int CacheTtlMinutes = 30;

    private readonly SettingsService _settingsService;
    private readonly OpenMeteoCacheService _cacheService;
    private readonly OpenMeteoQuotaService _quotaService;
    private readonly ILogger<WeatherDetailSettingsPage> _logger;

    /// <summary>
    /// Open-Meteo 专用 HttpClient：
    /// 开启 gzip（38 KB → 8.6 KB，实测能快一半以上）、限制连接/整体超时，
    /// 避免走全局 HttpClient 时既没有压缩也没有明确超时。
    /// </summary>
    private static readonly HttpClient OpenMeteoHttpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly JsonSerializerOptions OpenMeteoJsonOptions = new(JsonSerializerDefaults.Web);

    public WeatherDetailSettingsViewModel(SettingsService settingsService,
        OpenMeteoCacheService cacheService,
        OpenMeteoQuotaService quotaService,
        ILogger<WeatherDetailSettingsPage> logger)
    {
        _settingsService = settingsService;
        _cacheService = cacheService;
        _quotaService = quotaService;
        _logger = logger;
    }

    public string CityName => string.IsNullOrWhiteSpace(_settingsService.Settings.CityName)
        ? "当前定位"
        : _settingsService.Settings.CityName;

    /// <summary>
    /// 本页的维护者标识。
    /// </summary>
    public string MaintainerText => "该页面由 farmer-snack/NLCLI 维护";

    [ObservableProperty] private bool _isOpenMeteoLoading;
    [ObservableProperty] private string? _openMeteoError;
    [ObservableProperty] private string _omCurrentTemp = "--";
    [ObservableProperty] private string _omCurrentText = "";
    [ObservableProperty] private string _omCurrentEmoji = "";
    [ObservableProperty] private string _omCurrentDetail = "";
    [ObservableProperty] private string _omUpdatedAt = "";
    [ObservableProperty] private string _omCacheStatus = "";
    [ObservableProperty] private string _omQuotaText = "";
    [ObservableProperty] private string _omSunTimes = "";
    [ObservableProperty] private string _omAqiLevel = "";
    [ObservableProperty] private List<OmMinutelyItem> _omMinutes = new();
    [ObservableProperty] private List<OmDayItem> _omDays = new();
    [ObservableProperty] private List<OmMetricItem> _omMetrics = new();
    [ObservableProperty] private List<OmAqiItem> _omAqi = new();

    /// <summary>
    /// 页面加载入口。
    /// 1. 先读本地缓存并立刻渲染（缓存写在 Settings.json 同目录，重启客户端后仍然有效）；
    /// 2. 缓存不到 30 分钟就直接用，不联网；
    /// 3. 超过 30 分钟才在后台刷新，刷新回来之前屏幕上仍然是本地数据。
    /// </summary>
    public async Task LoadOpenMeteoAsync(bool forceRefresh = false)
    {
        OnPropertyChanged(nameof(CityName));
        UpdateQuotaText();

        var latitude = _settingsService.Settings.WeatherLatitude;
        var longitude = _settingsService.Settings.WeatherLongitude;

        var cache = _cacheService.Load(latitude, longitude);
        var hasCache = cache?.Forecast != null;
        if (hasCache)
        {
            ApplyOpenMeteo(cache!.Forecast!, cache.CachedAt);
            if (cache.AirQuality != null)
            {
                ApplyAirQuality(cache.AirQuality);
            }

            OmCacheStatus = DescribeAge(cache.Age);
            _logger.LogInformation("天气详情使用本地缓存，缓存时间：{}", cache.CachedAt);
        }

        if (!forceRefresh && hasCache && cache!.Age < TimeSpan.FromMinutes(CacheTtlMinutes))
        {
            // 半小时以内：直接用本地结果，不消耗任何请求次数。
            return;
        }

        await RefreshAsync(latitude, longitude, hasCache);
    }

    /// <summary>
    /// 强制联网刷新（跳过 30 分钟判断），失败时保留屏幕上已有的数据。
    /// </summary>
    public async Task RefreshAsync()
    {
        await RefreshAsync(_settingsService.Settings.WeatherLatitude,
            _settingsService.Settings.WeatherLongitude, OmDays.Count > 0);
    }

    private async Task RefreshAsync(double latitude, double longitude, bool keepOldDataOnFailure)
    {
        IsOpenMeteoLoading = true;
        OpenMeteoError = null;
        try
        {
            var forecast = await FetchForecastAsync(latitude, longitude);
            var airQuality = await FetchAirQualityAsync(latitude, longitude);
            var now = DateTime.Now;

            ApplyOpenMeteo(forecast, now);
            if (airQuality != null)
            {
                ApplyAirQuality(airQuality);
            }

            OmCacheStatus = "刚刚更新";
            _cacheService.Save(new OpenMeteoCacheEntry
            {
                CachedAt = now,
                Latitude = latitude,
                Longitude = longitude,
                Forecast = forecast,
                AirQuality = airQuality
            });
        }
        catch (Exception ex)
        {
            OpenMeteoError = keepOldDataOnFailure
                ? $"刷新失败，继续显示本地缓存：{ex.Message}"
                : $"详细预报加载失败：{ex.Message}";
            _logger.LogWarning(ex, "Open-Meteo 请求失败");
        }
        finally
        {
            IsOpenMeteoLoading = false;
            UpdateQuotaText();
        }
    }

    private async Task<OpenMeteoForecast> FetchForecastAsync(double latitude, double longitude)
    {
        var lat = latitude.ToString(CultureInfo.InvariantCulture);
        var lon = longitude.ToString(CultureInfo.InvariantCulture);
        var url = "https://api.open-meteo.com/v1/forecast"
                  + $"?latitude={lat}&longitude={lon}"
                  + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,weather_code,is_day,precipitation,rain,showers,snowfall,cloud_cover,pressure_msl,surface_pressure,wind_speed_10m,wind_direction_10m,wind_gusts_10m"
                  + "&hourly=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,weather_code,precipitation_probability,precipitation,pressure_msl,cloud_cover,visibility,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index,is_day"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,sunrise,sunset,daylight_duration,sunshine_duration,uv_index_max,precipitation_sum,rain_sum,snowfall_sum,precipitation_hours,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant"
                  + "&minutely_15=temperature_2m,weather_code,precipitation,rain,snowfall,wind_speed_10m"
                  + "&timezone=auto&forecast_days=16&forecast_minutely_15=48";

        _quotaService.Record();
        UpdateQuotaText();
        return await GetJsonAsync<OpenMeteoForecast>(url);
    }

    private async Task<OpenMeteoAirQuality?> FetchAirQualityAsync(double latitude, double longitude)
    {
        try
        {
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var url = "https://air-quality-api.open-meteo.com/v1/air-quality"
                      + $"?latitude={lat}&longitude={lon}"
                      + "&current=european_aqi,us_aqi,pm10,pm2_5,carbon_monoxide,nitrogen_dioxide,sulphur_dioxide,ozone"
                      + "&timezone=auto";

            _quotaService.Record();
            UpdateQuotaText();
            return await GetJsonAsync<OpenMeteoAirQuality>(url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-Meteo 空气质量请求失败");
            return null;
        }
    }

    /// <summary>
    /// 带重试的 JSON 请求。只重试 1 次（退避 1 秒），失败时把耗时和状态码写进异常信息，
    /// 避免像原来的全局请求那样连试 4 次、每次等好几秒，用户只看到「一直在转圈」。
    /// </summary>
    private async Task<T> GetJsonAsync<T>(string url) where T : class
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await OpenMeteoHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                var result = await JsonSerializer.DeserializeAsync<T>(stream, OpenMeteoJsonOptions);
                sw.Stop();
                _logger.LogInformation("Open-Meteo 请求成功：{} ms，{}", sw.ElapsedMilliseconds, typeof(T).Name);
                return result ?? throw new InvalidOperationException("Open-Meteo 返回了空数据");
            }
            catch (Exception ex)
            {
                sw.Stop();
                last = ex;
                _logger.LogWarning(ex, "Open-Meteo 请求失败（第 {} 次，耗时 {} ms）", attempt + 1, sw.ElapsedMilliseconds);
                if (attempt == 0)
                {
                    await Task.Delay(1000);
                }
            }
        }

        throw new InvalidOperationException($"请求 Open-Meteo 失败：{last?.Message}", last);
    }

    private void UpdateQuotaText()
    {
        var quota = _quotaService.Current;
        OmQuotaText = $"剩余请求机会 {quota.Remaining:N0} 次"
                      + $"（本分钟 {quota.MinuteRemaining:N0} · 本小时 {quota.HourRemaining:N0} · 24 小时 {quota.DayRemaining:N0}）";
    }

    private static string DescribeAge(TimeSpan age) => age.TotalMinutes < 1
        ? "本地缓存 · 刚刚"
        : $"本地缓存 · {(int)age.TotalMinutes} 分钟前";

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

    private void ApplyOpenMeteo(OpenMeteoForecast data, DateTime timestamp)
    {
        var current = data.Current;
        var daily = data.Daily;
        var hourly = data.Hourly;

        OmCurrentTemp = F(current.Temperature, "0");
        OmCurrentText = WeatherCodeHelper.GetText(current.WeatherCode);
        OmCurrentEmoji = WeatherCodeHelper.GetEmoji(current.WeatherCode);
        OmUpdatedAt = string.IsNullOrWhiteSpace(current.Time)
            ? $"数据时间 {timestamp:HH:mm}"
            : $"数据时间 {current.Time.Replace('T', ' ')}";

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

        // ---- 逐小时：先剪掉已经过去的小时，再按自然日分组 ----
        var nowHour = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, 0, 0);
        var hoursByDate = new Dictionary<DateTime, List<OmHourItem>>();
        for (var i = 0; i < hourly.Time.Count; i++)
        {
            var time = ParseTime(hourly.Time[i]);
            if (time is null)
            {
                continue;
            }

            // Open-Meteo 的 hourly 永远从当天 00:00 开始返回，这里只保留「当前整点及以后」。
            if (time.Value < nowHour)
            {
                continue;
            }

            var code = At(hourly.WeatherCode, i);
            var temp = At(hourly.Temperature, i);
            var pop = At(hourly.PrecipitationProbability, i);
            var isNow = time.Value == nowHour;
            var hourDetail = new List<OmMetricItem>
            {
                new("天气", WeatherCodeHelper.GetText(code)),
                new("湿度", F(At(hourly.Humidity, i), "0", "%")),
                new("露点", F(At(hourly.DewPoint, i), "0", "°")),
                new("体感", F(At(hourly.ApparentTemperature, i), "0", "°")),
                new("气压", F(At(hourly.PressureMsl, i), "0", " hPa")),
                new("云量", F(At(hourly.CloudCover, i), "0", "%")),
                new("能见度", F(At(hourly.Visibility, i) is { } v ? v / 1000 : null, "0.0", " km")),
                new("风速", F(At(hourly.WindSpeed, i), "0", " km/h")),
                new("阵风", F(At(hourly.WindGusts, i), "0", " km/h")),
                new("风向", Dir(At(hourly.WindDirection, i))),
                new("降水量", F(At(hourly.Precipitation, i), "0.0", " mm")),
                new("降水概率", pop is null ? "--" : pop + "%"),
                new("紫外线", F(At(hourly.UvIndex, i), "0.0")),
                new("时段", At(hourly.IsDay, i) == 0 ? "夜间" : "白天")
            };

            var item = new OmHourItem(isNow ? "现在" : time.Value.ToString("HH时"),
                WeatherCodeHelper.GetEmoji(code),
                F(temp, "0", "°"),
                pop is null ? "" : pop + "%",
                hourDetail);

            var date = time.Value.Date;
            if (!hoursByDate.TryGetValue(date, out var list))
            {
                list = [];
                hoursByDate[date] = list;
            }

            list.Add(item);
        }

        var minutes = new List<OmMinutelyItem>();
        for (var i = 0; i < data.Minutely15.Time.Count; i++)
        {
            var time = ParseTime(data.Minutely15.Time[i]);
            if (time is null)
            {
                continue;
            }

            var precipitation = At(data.Minutely15.Precipitation, i);
            // 15 分钟粒度没有 precipitation_probability（那是 hourly 才有的字段），这里只显示降水量。
            minutes.Add(new OmMinutelyItem(time.Value.ToString("HH:mm"),
                F(precipitation, "0.0", " mm"), ""));
        }
        OmMinutes = minutes;

        // ---- 天级：带上这一天的 24 小时列表 ----
        var days = new List<OmDayItem>();
        for (var i = 0; i < daily.Time.Count; i++)
        {
            var date = ParseTime(daily.Time[i]);
            if (date is null)
            {
                continue;
            }

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
            var dayDetail = new List<OmMetricItem>
            {
                new("最高温", F(max, "0", "°")),
                new("最低温", F(min, "0", "°")),
                new("日出", daily.Sunrise.Count > i ? Clock(daily.Sunrise[i]) : "--"),
                new("日落", daily.Sunset.Count > i ? Clock(daily.Sunset[i]) : "--"),
                new("白天时长", F(At(daily.DaylightDuration, i) is { } dl ? dl / 3600 : null, "0.0", " h")),
                new("日照时长", F(At(daily.SunshineDuration, i) is { } sd ? sd / 3600 : null, "0.0", " h")),
                new("降雨量", F(At(daily.RainSum, i), "0.0", " mm")),
                new("降雪量", F(At(daily.SnowfallSum, i), "0.0", " cm")),
                new("降水时长", F(At(daily.PrecipitationHours, i), "0.0", " h")),
                new("最大阵风", F(At(daily.WindGustsMax, i), "0", " km/h")),
                new("体感最高", F(At(daily.ApparentMax, i), "0", "°")),
                new("体感最低", F(At(daily.ApparentMin, i), "0", "°")),
                new("最大风速", windText),
                new("紫外线", F(uvMax, "0.0")),
                new("降水概率", pop is null ? "--" : pop + "%"),
                new("降水量", F(sum, "0.0", " mm"))
            };

            hoursByDate.TryGetValue(date.Value.Date, out var dayHours);
            days.Add(new OmDayItem(DayLabel(date.Value.Date),
                date.Value.ToString("MM-dd"),
                WeatherCodeHelper.GetEmoji(code),
                $"{F(min, "0", "°")} ~ {F(max, "0", "°")}",
                precipText,
                windText,
                F(uvMax, "0.0"),
                WeatherCodeHelper.GetText(code),
                dayHours ?? [],
                dayDetail));
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
}
