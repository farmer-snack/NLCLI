using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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

public sealed record OmMetricItem(string Label, string Value);
public sealed record OmHourItem(string Label, string Emoji, string Temp, string Precip, List<OmMetricItem> Detail);
public sealed record OmMinutelyItem(string Label, string Temp, string Precip, List<OmMetricItem> Detail);
public sealed record OmAqiItem(string Label, string Value);

/// <summary>
/// 一天的预报：概览 + 该日全部字段 + 这一天的小时列表。
/// <see cref="SelectedHour"/> 用于「点一个小时看它的全部数据」。
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
    List<OmMetricItem> detail,
    bool isToday)
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

    /// <summary>今天默认展开，其它天默认收起。</summary>
    [ObservableProperty] private bool _isExpanded = isToday;

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

    /// <summary>
    /// 请求的变量集合。这里把 Open-Meteo forecast 接口支持的全部字段都拉下来，
    /// 页面再按返回结果自动展示，避免「接口给了但页面漏掉」。
    /// </summary>
    private const string CurrentVariables =
        "temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,rain,showers,snowfall," +
        "weather_code,cloud_cover,pressure_msl,surface_pressure,wind_speed_10m,wind_direction_10m,wind_gusts_10m";

    private const string HourlyVariables =
        "temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability," +
        "precipitation,rain,showers,snowfall,snow_depth,weather_code,pressure_msl,surface_pressure," +
        "cloud_cover,cloud_cover_low,cloud_cover_mid,cloud_cover_high,visibility,evapotranspiration," +
        "et0_fao_evapotranspiration,vapour_pressure_deficit,wind_speed_10m,wind_speed_80m,wind_speed_120m," +
        "wind_speed_180m,wind_direction_10m,wind_direction_80m,wind_direction_120m,wind_direction_180m," +
        "wind_gusts_10m,temperature_80m,temperature_120m,temperature_180m,soil_temperature_0cm," +
        "soil_temperature_6cm,soil_temperature_18cm,soil_temperature_54cm,soil_moisture_0_to_1cm," +
        "soil_moisture_1_to_3cm,soil_moisture_3_to_9cm,soil_moisture_9_to_27cm,soil_moisture_27_to_81cm," +
        "uv_index,uv_index_clear_sky,is_day,sunshine_duration,wet_bulb_temperature_2m," +
        "total_column_integrated_water_vapour,cape,lifted_index,convective_inhibition," +
        "freezing_level_height,boundary_layer_height";

    private const string DailyVariables =
        "weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min," +
        "sunrise,sunset,daylight_duration,sunshine_duration,uv_index_max,uv_index_clear_sky_max," +
        "precipitation_sum,rain_sum,showers_sum,snowfall_sum,precipitation_hours,precipitation_probability_max," +
        "wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant,shortwave_radiation_sum," +
        "et0_fao_evapotranspiration";

    private const string Minutely15Variables =
        "temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation,rain,showers," +
        "snowfall,snowfall_height,weather_code,wind_speed_10m,wind_speed_80m,wind_direction_10m," +
        "wind_direction_80m,wind_gusts_10m,visibility,evapotranspiration,et0_fao_evapotranspiration," +
        "vapour_pressure_deficit,cape,freezing_level_height,shortwave_radiation,direct_radiation," +
        "diffuse_radiation,direct_normal_irradiance,global_tilted_irradiance,terrestrial_radiation,is_day," +
        "sunshine_duration,uv_index,uv_index_clear_sky";

    private const string AirQualityVariables =
        "european_aqi,european_aqi_pm2_5,european_aqi_pm10,european_aqi_nitrogen_dioxide,european_aqi_ozone," +
        "european_aqi_sulphur_dioxide,us_aqi,us_aqi_pm2_5,us_aqi_pm10,us_aqi_nitrogen_dioxide,us_aqi_ozone," +
        "us_aqi_sulphur_dioxide,us_aqi_carbon_monoxide,pm10,pm2_5,carbon_monoxide,nitrogen_dioxide," +
        "sulphur_dioxide,ozone,dust,ammonia,aerosol_optical_depth,uv_index,uv_index_clear_sky," +
        "alder_pollen,birch_pollen,grass_pollen,mugwort_pollen,olive_pollen,ragweed_pollen";

    /// <summary>
    /// 字段显示顺序：常用的排前面，其余字段自动按字母序追加到后面。
    /// </summary>
    private static readonly string[] PreferredFieldOrder =
    [
        "temperature_2m", "apparent_temperature", "relative_humidity_2m", "dew_point_2m",
        "weather_code", "is_day", "precipitation", "rain", "showers", "snowfall", "snow_depth",
        "precipitation_probability", "precipitation_probability_max", "precipitation_hours",
        "cloud_cover", "cloud_cover_low", "cloud_cover_mid", "cloud_cover_high",
        "pressure_msl", "surface_pressure", "visibility", "wind_speed_10m", "wind_direction_10m",
        "wind_gusts_10m", "wind_speed_80m", "wind_direction_80m", "wind_speed_120m", "wind_direction_120m",
        "wind_speed_180m", "wind_direction_180m", "temperature_80m", "temperature_120m", "temperature_180m",
        "uv_index", "uv_index_clear_sky", "sunshine_duration", "sunrise", "sunset", "daylight_duration",
        "temperature_2m_max", "temperature_2m_min", "apparent_temperature_max", "apparent_temperature_min",
        "uv_index_max", "uv_index_clear_sky_max", "precipitation_sum", "rain_sum", "showers_sum",
        "snowfall_sum", "wind_speed_10m_max", "wind_gusts_10m_max", "wind_direction_10m_dominant",
        "shortwave_radiation_sum", "shortwave_radiation", "direct_radiation", "diffuse_radiation",
        "direct_normal_irradiance", "global_tilted_irradiance", "terrestrial_radiation",
        "evapotranspiration", "et0_fao_evapotranspiration", "vapour_pressure_deficit",
        "wet_bulb_temperature_2m", "total_column_integrated_water_vapour", "cape", "lifted_index",
        "convective_inhibition", "freezing_level_height", "boundary_layer_height", "snowfall_height",
        "soil_temperature_0cm", "soil_temperature_6cm", "soil_temperature_18cm", "soil_temperature_54cm",
        "soil_moisture_0_to_1cm", "soil_moisture_1_to_3cm", "soil_moisture_3_to_9cm",
        "soil_moisture_9_to_27cm", "soil_moisture_27_to_81cm",
        "european_aqi", "us_aqi", "pm2_5", "pm10", "ozone", "nitrogen_dioxide", "sulphur_dioxide",
        "carbon_monoxide", "dust", "ammonia", "aerosol_optical_depth",
        "european_aqi_pm2_5", "european_aqi_pm10", "european_aqi_nitrogen_dioxide",
        "european_aqi_ozone", "european_aqi_sulphur_dioxide", "us_aqi_pm2_5", "us_aqi_pm10",
        "us_aqi_nitrogen_dioxide", "us_aqi_ozone", "us_aqi_sulphur_dioxide", "us_aqi_carbon_monoxide",
        "alder_pollen", "birch_pollen", "grass_pollen", "mugwort_pollen", "olive_pollen", "ragweed_pollen"
    ];

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.Ordinal)
    {
        ["temperature_2m"] = "温度",
        ["apparent_temperature"] = "体感温度",
        ["relative_humidity_2m"] = "相对湿度",
        ["dew_point_2m"] = "露点",
        ["weather_code"] = "天气",
        ["is_day"] = "时段",
        ["precipitation"] = "降水量",
        ["rain"] = "降雨量",
        ["showers"] = "阵雨量",
        ["snowfall"] = "降雪量",
        ["snow_depth"] = "积雪深度",
        ["snowfall_height"] = "降雪高度",
        ["precipitation_probability"] = "降水概率",
        ["precipitation_probability_max"] = "最大降水概率",
        ["precipitation_hours"] = "降水时长",
        ["precipitation_sum"] = "总降水量",
        ["rain_sum"] = "总降雨量",
        ["showers_sum"] = "总阵雨量",
        ["snowfall_sum"] = "总降雪量",
        ["cloud_cover"] = "总云量",
        ["cloud_cover_low"] = "低云量",
        ["cloud_cover_mid"] = "中云量",
        ["cloud_cover_high"] = "高云量",
        ["pressure_msl"] = "海平面气压",
        ["surface_pressure"] = "地面气压",
        ["visibility"] = "能见度",
        ["wind_speed_10m"] = "风速 10m",
        ["wind_direction_10m"] = "风向 10m",
        ["wind_gusts_10m"] = "阵风",
        ["wind_speed_80m"] = "风速 80m",
        ["wind_direction_80m"] = "风向 80m",
        ["wind_speed_120m"] = "风速 120m",
        ["wind_direction_120m"] = "风向 120m",
        ["wind_speed_180m"] = "风速 180m",
        ["wind_direction_180m"] = "风向 180m",
        ["wind_speed_10m_max"] = "最大风速",
        ["wind_gusts_10m_max"] = "最大阵风",
        ["wind_direction_10m_dominant"] = "主导风向",
        ["temperature_80m"] = "温度 80m",
        ["temperature_120m"] = "温度 120m",
        ["temperature_180m"] = "温度 180m",
        ["uv_index"] = "紫外线",
        ["uv_index_clear_sky"] = "晴天紫外线",
        ["uv_index_max"] = "最大紫外线",
        ["uv_index_clear_sky_max"] = "晴天最大紫外线",
        ["sunshine_duration"] = "日照时长",
        ["sunrise"] = "日出",
        ["sunset"] = "日落",
        ["daylight_duration"] = "白天时长",
        ["temperature_2m_max"] = "最高温",
        ["temperature_2m_min"] = "最低温",
        ["apparent_temperature_max"] = "体感最高",
        ["apparent_temperature_min"] = "体感最低",
        ["shortwave_radiation"] = "短波辐射",
        ["shortwave_radiation_sum"] = "短波辐射总量",
        ["direct_radiation"] = "直接辐射",
        ["diffuse_radiation"] = "散射辐射",
        ["direct_normal_irradiance"] = "法向直射",
        ["global_tilted_irradiance"] = "倾斜面辐射",
        ["terrestrial_radiation"] = "地面辐射",
        ["evapotranspiration"] = "蒸散量",
        ["et0_fao_evapotranspiration"] = "参考蒸散量",
        ["vapour_pressure_deficit"] = "蒸气压亏缺",
        ["wet_bulb_temperature_2m"] = "湿球温度",
        ["total_column_integrated_water_vapour"] = "总水汽",
        ["cape"] = "对流有效位能",
        ["lifted_index"] = "抬升指数",
        ["convective_inhibition"] = "对流抑制",
        ["freezing_level_height"] = "0℃ 层高度",
        ["boundary_layer_height"] = "边界层高度",
        ["soil_temperature_0cm"] = "地表温度",
        ["soil_temperature_6cm"] = "土壤温度 6cm",
        ["soil_temperature_18cm"] = "土壤温度 18cm",
        ["soil_temperature_54cm"] = "土壤温度 54cm",
        ["soil_moisture_0_to_1cm"] = "土壤湿度 0-1cm",
        ["soil_moisture_1_to_3cm"] = "土壤湿度 1-3cm",
        ["soil_moisture_3_to_9cm"] = "土壤湿度 3-9cm",
        ["soil_moisture_9_to_27cm"] = "土壤湿度 9-27cm",
        ["soil_moisture_27_to_81cm"] = "土壤湿度 27-81cm",
        ["european_aqi"] = "欧标 AQI",
        ["us_aqi"] = "美标 AQI",
        ["pm2_5"] = "PM2.5",
        ["pm10"] = "PM10",
        ["ozone"] = "臭氧 O₃",
        ["nitrogen_dioxide"] = "二氧化氮 NO₂",
        ["sulphur_dioxide"] = "二氧化硫 SO₂",
        ["carbon_monoxide"] = "一氧化碳 CO",
        ["dust"] = "沙尘",
        ["ammonia"] = "氨气 NH₃",
        ["aerosol_optical_depth"] = "气溶胶光学厚度",
        ["european_aqi_pm2_5"] = "欧标 PM2.5 AQI",
        ["european_aqi_pm10"] = "欧标 PM10 AQI",
        ["european_aqi_nitrogen_dioxide"] = "欧标 NO₂ AQI",
        ["european_aqi_ozone"] = "欧标 O₃ AQI",
        ["european_aqi_sulphur_dioxide"] = "欧标 SO₂ AQI",
        ["us_aqi_pm2_5"] = "美标 PM2.5 AQI",
        ["us_aqi_pm10"] = "美标 PM10 AQI",
        ["us_aqi_nitrogen_dioxide"] = "美标 NO₂ AQI",
        ["us_aqi_ozone"] = "美标 O₃ AQI",
        ["us_aqi_sulphur_dioxide"] = "美标 SO₂ AQI",
        ["us_aqi_carbon_monoxide"] = "美标 CO AQI",
        ["alder_pollen"] = "赤杨花粉",
        ["birch_pollen"] = "桦树花粉",
        ["grass_pollen"] = "禾本科花粉",
        ["mugwort_pollen"] = "艾蒿花粉",
        ["olive_pollen"] = "橄榄花粉",
        ["ragweed_pollen"] = "豚草花粉"
    };

    private readonly SettingsService _settingsService;
    private readonly OpenMeteoCacheService _cacheService;
    private readonly OpenMeteoQuotaService _quotaService;
    private readonly ILogger<WeatherDetailSettingsPage> _logger;

    /// <summary>
    /// Open-Meteo 专用 HttpClient：
    /// 开启 gzip、限制连接/整体超时，避免走全局 HttpClient 时既没有压缩也没有明确超时。
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
    [ObservableProperty] private string _omFieldCountText = "";
    [ObservableProperty] private List<OmMinutelyItem> _omMinutes = new();
    [ObservableProperty] private OmMinutelyItem? _selectedMinute;
    [ObservableProperty] private List<OmDayItem> _omDays = new();
    [ObservableProperty] private List<OmMetricItem> _omMetrics = new();
    [ObservableProperty] private List<OmMetricItem> _omCurrentAll = new();
    [ObservableProperty] private List<OmAqiItem> _omAqi = new();
    [ObservableProperty] private List<OmMetricItem> _omAqiAll = new();

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
                Version = OpenMeteoCacheEntry.CurrentVersion,
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
                  + $"&current={CurrentVariables}"
                  + $"&hourly={HourlyVariables}"
                  + $"&daily={DailyVariables}"
                  + $"&minutely_15={Minutely15Variables}"
                  + "&timezone=auto&forecast_days=16&forecast_minutely_15=96";

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
                      + $"&current={AirQualityVariables}"
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
    /// 带重试的 JSON 请求。只重试 1 次（退避 1 秒），失败时把耗时和状态码写进异常信息。
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
        var current = data.Current;
        OmAqiLevel = AqiLevel(current.Number("european_aqi"));
        OmAqi = new List<OmAqiItem>
        {
            new("欧标 AQI", F(current.Number("european_aqi"), "0")),
            new("美标 AQI", F(current.Number("us_aqi"), "0")),
            new("PM2.5", F(current.Number("pm2_5"), "0", " μg/m³")),
            new("PM10", F(current.Number("pm10"), "0", " μg/m³")),
            new("臭氧 O₃", F(current.Number("ozone"), "0", " μg/m³")),
            new("二氧化氮 NO₂", F(current.Number("nitrogen_dioxide"), "0", " μg/m³")),
            new("二氧化硫 SO₂", F(current.Number("sulphur_dioxide"), "0", " μg/m³")),
            new("一氧化碳 CO", F(current.Number("carbon_monoxide"), "0", " μg/m³"))
        };

        // 空气质量接口能给的字段全部列出来（花粉、沙尘、气溶胶等按地区可能为空）。
        OmAqiAll = BuildFieldList(current, -1, data.CurrentUnits);
    }

    private void ApplyOpenMeteo(OpenMeteoForecast data, DateTime timestamp)
    {
        var current = data.Current;
        var daily = data.Daily;
        var hourly = data.Hourly;

        var currentCode = current.Int("weather_code") ?? 0;
        OmCurrentTemp = F(current.Number("temperature_2m"), "0");
        OmCurrentText = WeatherCodeHelper.GetText(currentCode);
        OmCurrentEmoji = WeatherCodeHelper.GetEmoji(currentCode);
        var currentTime = current.Text("time");
        OmUpdatedAt = string.IsNullOrWhiteSpace(currentTime)
            ? $"数据时间 {timestamp:HH:mm}"
            : $"数据时间 {currentTime.Replace('T', ' ')}";

        var high = daily.Number("temperature_2m_max", 0);
        var low = daily.Number("temperature_2m_min", 0);
        var sunrise = daily.Text("sunrise", 0);
        var sunset = daily.Text("sunset", 0);
        OmSunTimes = $"日出 {Clock(sunrise ?? "")} · 日落 {Clock(sunset ?? "")}";
        OmCurrentDetail = $"体感 {F(current.Number("apparent_temperature"), "0", "°")} · "
                          + $"今日 {F(low, "0", "°")} ~ {F(high, "0", "°")} · {OmSunTimes}";

        // 顶部常用指标（其余字段在「当前全部数据」里）
        var visibility = hourly.Number("visibility", 0);
        var uv = hourly.Number("uv_index", 0);
        var sunshine = hourly.Number("sunshine_duration", 0);
        OmMetrics = new List<OmMetricItem>
        {
            new("湿度", F(current.Number("relative_humidity_2m"), "0", "%")),
            new("露点", F(current.Number("dew_point_2m"), "0", "°")),
            new("气压", F(current.Number("pressure_msl"), "0", " hPa")),
            new("能见度", F(visibility is null ? null : visibility / 1000, "0.0", " km")),
            new("风速", F(current.Number("wind_speed_10m"), "0", " km/h")),
            new("阵风", F(current.Number("wind_gusts_10m"), "0", " km/h")),
            new("风向", Dir(current.Number("wind_direction_10m"))),
            new("体感", F(current.Number("apparent_temperature"), "0", "°")),
            new("云量", F(current.Number("cloud_cover"), "0", "%")),
            new("降水量", F(current.Number("precipitation"), "0.0", " mm")),
            new("日照时长", F(sunshine is null ? null : sunshine / 3600, "0.0", " h")),
            new("紫外线", F(uv, "0.0"))
        };

        // 当前时刻的全部字段
        OmCurrentAll = BuildFieldList(current, -1, data.CurrentUnits);

        // ---- 每 15 分钟：未来 24 小时（96 条），每条都带完整字段 ----
        var minutes = new List<OmMinutelyItem>();
        for (var i = 0; i < data.Minutely15.Time.Count; i++)
        {
            var time = ParseTime(data.Minutely15.Time[i]);
            if (time is null)
            {
                continue;
            }

            minutes.Add(new OmMinutelyItem(
                time.Value.ToString("HH:mm"),
                F(data.Minutely15.Number("temperature_2m", i), "0", "°"),
                F(data.Minutely15.Number("precipitation", i), "0.0", "mm"),
                BuildFieldList(data.Minutely15, i, data.Minutely15Units)));
        }

        OmMinutes = minutes;
        SelectedMinute = minutes.FirstOrDefault();

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

            var code = hourly.Int("weather_code", i) ?? 0;
            var isNow = time.Value == nowHour;
            var item = new OmHourItem(
                isNow ? "现在" : time.Value.ToString("HH时"),
                WeatherCodeHelper.GetEmoji(code),
                F(hourly.Number("temperature_2m", i), "0", "°"),
                hourly.Int("precipitation_probability", i) is { } pop ? pop + "%" : "",
                BuildFieldList(hourly, i, data.HourlyUnits));

            var date = time.Value.Date;
            if (!hoursByDate.TryGetValue(date, out var list))
            {
                list = [];
                hoursByDate[date] = list;
            }

            list.Add(item);
        }

        // ---- 天级：每一天带「该日全部字段 + 24 小时」 ----
        var days = new List<OmDayItem>();
        for (var i = 0; i < daily.Time.Count; i++)
        {
            var date = ParseTime(daily.Time[i]);
            if (date is null)
            {
                continue;
            }

            var code = daily.Int("weather_code", i) ?? 0;
            var max = daily.Number("temperature_2m_max", i);
            var min = daily.Number("temperature_2m_min", i);
            var pop = daily.Int("precipitation_probability_max", i);
            var sum = daily.Number("precipitation_sum", i);
            var wind = daily.Number("wind_speed_10m_max", i);
            var windDir = daily.Number("wind_direction_10m_dominant", i);
            var uvMax = daily.Number("uv_index_max", i);
            var precipText = (pop is null ? "--" : pop + "%") + " · " + F(sum, "0.0", "mm");
            var windText = F(wind, "0", " km/h") + " " + Dir(windDir);

            hoursByDate.TryGetValue(date.Value.Date, out var dayHours);

            days.Add(new OmDayItem(
                DayLabel(date.Value.Date),
                date.Value.ToString("MM-dd"),
                WeatherCodeHelper.GetEmoji(code),
                $"{F(min, "0", "°")} ~ {F(max, "0", "°")}",
                precipText,
                windText,
                F(uvMax, "0.0"),
                WeatherCodeHelper.GetText(code),
                dayHours ?? [],
                BuildFieldList(daily, i, data.DailyUnits),
                date.Value.Date == DateTime.Today));
        }

        OmDays = days;
        OmFieldCountText = $"当前 {data.Current.Values.Count} 项 · 每小时 {data.Hourly.Values.Count} 项 · "
                           + $"每天 {data.Daily.Values.Count} 项 · 15 分钟 {data.Minutely15.Values.Count} 项";
    }

    /// <summary>
    /// 把一组数据里的所有字段按「常用优先 + 其余字母序」排好，值用接口给的单位格式化。
    /// </summary>
    private static List<OmMetricItem> BuildFieldList(OmBlock block, int index, Dictionary<string, string> units)
    {
        var result = new List<OmMetricItem>(block.Values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in PreferredFieldOrder)
        {
            if (block.Values.ContainsKey(key) && seen.Add(key))
            {
                result.Add(new OmMetricItem(LabelOf(key), FormatField(key, block, index, UnitOf(units, key))));
            }
        }

        foreach (var key in block.Values.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (seen.Add(key))
            {
                result.Add(new OmMetricItem(LabelOf(key), FormatField(key, block, index, UnitOf(units, key))));
            }
        }

        return result;
    }

    private static string LabelOf(string key) => FieldLabels.TryGetValue(key, out var label) ? label : key;

    private static string UnitOf(Dictionary<string, string> units, string key)
        => units.TryGetValue(key, out var unit) ? unit : "";

    private static string FormatField(string key, OmBlock block, int index, string unit)
    {
        switch (key)
        {
            case "weather_code":
            {
                var code = block.Int(key, index);
                return code is null ? "--" : WeatherCodeHelper.GetText(code.Value);
            }
            case "is_day":
            {
                var value = block.Int(key, index);
                return value is null ? "--" : value.Value == 0 ? "夜间" : "白天";
            }
            case "sunrise":
            case "sunset":
                return Clock(block.Text(key, index) ?? "");
        }

        if (key.StartsWith("wind_direction", StringComparison.Ordinal))
        {
            return Dir(block.Number(key, index));
        }

        if (key.EndsWith("_duration", StringComparison.Ordinal))
        {
            var seconds = block.Number(key, index);
            return seconds is null
                ? "--"
                : (seconds.Value / 3600).ToString("0.0", CultureInfo.InvariantCulture) + " h";
        }

        if (key.EndsWith("_probability", StringComparison.Ordinal)
            || key.EndsWith("_probability_max", StringComparison.Ordinal))
        {
            var probability = block.Number(key, index);
            return probability is null
                ? "--"
                : probability.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        if (key == "visibility")
        {
            var meters = block.Number(key, index);
            return meters is null
                ? "--"
                : (meters.Value / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        }

        var number = block.Number(key, index);
        if (number is null)
        {
            var text = block.Text(key, index);
            return string.IsNullOrWhiteSpace(text) ? "--" : text;
        }

        return FormatNumber(number.Value, unit);
    }

    private static string FormatNumber(double value, string unit)
    {
        if (string.IsNullOrEmpty(unit))
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        if (unit == "%")
        {
            return value.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        var format = Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.#" : "0.0#";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + unit;
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
