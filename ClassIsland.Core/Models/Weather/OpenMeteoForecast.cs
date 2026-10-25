using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClassIsland.Core.Models.Weather;

/// <summary>
/// Open-Meteo /v1/forecast 的返回结构（只声明用得到的字段）。
/// 文档：https://open-meteo.com/en/docs
/// </summary>
public class OpenMeteoForecast
{
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "";

    [JsonPropertyName("current")] public OmCurrent Current { get; set; } = new();
    [JsonPropertyName("hourly")] public OmHourly Hourly { get; set; } = new();
    [JsonPropertyName("daily")] public OmDaily Daily { get; set; } = new();
    [JsonPropertyName("minutely_15")] public OmMinutely15 Minutely15 { get; set; } = new();
}

public class OmCurrent
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("temperature_2m")] public double? Temperature { get; set; }
    [JsonPropertyName("apparent_temperature")] public double? ApparentTemperature { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double? Humidity { get; set; }
    [JsonPropertyName("dew_point_2m")] public double? DewPoint { get; set; }
    [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
    [JsonPropertyName("is_day")] public int? IsDay { get; set; }
    [JsonPropertyName("precipitation")] public double? Precipitation { get; set; }
    [JsonPropertyName("rain")] public double? Rain { get; set; }
    [JsonPropertyName("showers")] public double? Showers { get; set; }
    [JsonPropertyName("snowfall")] public double? Snowfall { get; set; }
    [JsonPropertyName("cloud_cover")] public double? CloudCover { get; set; }
    [JsonPropertyName("pressure_msl")] public double? PressureMsl { get; set; }
    [JsonPropertyName("surface_pressure")] public double? SurfacePressure { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double? WindSpeed { get; set; }
    [JsonPropertyName("wind_direction_10m")] public double? WindDirection { get; set; }
    [JsonPropertyName("wind_gusts_10m")] public double? WindGusts { get; set; }
}

public class OmHourly
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("temperature_2m")] public List<double?> Temperature { get; set; } = [];
    [JsonPropertyName("apparent_temperature")] public List<double?> ApparentTemperature { get; set; } = [];
    [JsonPropertyName("relative_humidity_2m")] public List<double?> Humidity { get; set; } = [];
    [JsonPropertyName("dew_point_2m")] public List<double?> DewPoint { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public List<int?> PrecipitationProbability { get; set; } = [];
    [JsonPropertyName("precipitation")] public List<double?> Precipitation { get; set; } = [];
    [JsonPropertyName("pressure_msl")] public List<double?> PressureMsl { get; set; } = [];
    [JsonPropertyName("cloud_cover")] public List<double?> CloudCover { get; set; } = [];
    [JsonPropertyName("visibility")] public List<double?> Visibility { get; set; } = [];
    [JsonPropertyName("wind_speed_10m")] public List<double?> WindSpeed { get; set; } = [];
    [JsonPropertyName("wind_direction_10m")] public List<double?> WindDirection { get; set; } = [];
    [JsonPropertyName("wind_gusts_10m")] public List<double?> WindGusts { get; set; } = [];
    [JsonPropertyName("uv_index")] public List<double?> UvIndex { get; set; } = [];
    [JsonPropertyName("is_day")] public List<int?> IsDay { get; set; } = [];
}

public class OmDaily
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("temperature_2m_max")] public List<double?> TemperatureMax { get; set; } = [];
    [JsonPropertyName("temperature_2m_min")] public List<double?> TemperatureMin { get; set; } = [];
    [JsonPropertyName("apparent_temperature_max")] public List<double?> ApparentMax { get; set; } = [];
    [JsonPropertyName("apparent_temperature_min")] public List<double?> ApparentMin { get; set; } = [];
    [JsonPropertyName("sunrise")] public List<string> Sunrise { get; set; } = [];
    [JsonPropertyName("sunset")] public List<string> Sunset { get; set; } = [];
    [JsonPropertyName("daylight_duration")] public List<double?> DaylightDuration { get; set; } = [];
    [JsonPropertyName("sunshine_duration")] public List<double?> SunshineDuration { get; set; } = [];
    [JsonPropertyName("uv_index_max")] public List<double?> UvIndexMax { get; set; } = [];
    [JsonPropertyName("precipitation_sum")] public List<double?> PrecipitationSum { get; set; } = [];
    [JsonPropertyName("rain_sum")] public List<double?> RainSum { get; set; } = [];
    [JsonPropertyName("snowfall_sum")] public List<double?> SnowfallSum { get; set; } = [];
    [JsonPropertyName("precipitation_hours")] public List<double?> PrecipitationHours { get; set; } = [];
    [JsonPropertyName("precipitation_probability_max")] public List<int?> PrecipitationProbabilityMax { get; set; } = [];
    [JsonPropertyName("wind_speed_10m_max")] public List<double?> WindSpeedMax { get; set; } = [];
    [JsonPropertyName("wind_gusts_10m_max")] public List<double?> WindGustsMax { get; set; } = [];
    [JsonPropertyName("wind_direction_10m_dominant")] public List<double?> WindDirectionDominant { get; set; } = [];
}

public class OmMinutely15
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("temperature_2m")] public List<double?> Temperature { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("precipitation")] public List<double?> Precipitation { get; set; } = [];
    [JsonPropertyName("rain")] public List<double?> Rain { get; set; } = [];
    [JsonPropertyName("snowfall")] public List<double?> Snowfall { get; set; } = [];
    [JsonPropertyName("wind_speed_10m")] public List<double?> WindSpeed { get; set; } = [];
}

/// <summary>
/// Open-Meteo 空气质量接口（air-quality-api.open-meteo.com）的返回结构。
/// </summary>
public class OpenMeteoAirQuality
{
    [JsonPropertyName("current")] public OmAirCurrent Current { get; set; } = new();
}

public class OmAirCurrent
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("european_aqi")] public double? EuropeanAqi { get; set; }
    [JsonPropertyName("us_aqi")] public double? UsAqi { get; set; }
    [JsonPropertyName("pm10")] public double? Pm10 { get; set; }
    [JsonPropertyName("pm2_5")] public double? Pm2_5 { get; set; }
    [JsonPropertyName("carbon_monoxide")] public double? CarbonMonoxide { get; set; }
    [JsonPropertyName("nitrogen_dioxide")] public double? NitrogenDioxide { get; set; }
    [JsonPropertyName("sulphur_dioxide")] public double? SulphurDioxide { get; set; }
    [JsonPropertyName("ozone")] public double? Ozone { get; set; }
}
