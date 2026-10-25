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
    [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
    [JsonPropertyName("apparent_temperature")] public double ApparentTemperature { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double Humidity { get; set; }
    [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double WindSpeed { get; set; }
}

public class OmHourly
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("temperature_2m")] public List<double> Temperature { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public List<int?> PrecipitationProbability { get; set; } = [];
}

public class OmDaily
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("temperature_2m_max")] public List<double> TemperatureMax { get; set; } = [];
    [JsonPropertyName("temperature_2m_min")] public List<double> TemperatureMin { get; set; } = [];
    [JsonPropertyName("precipitation_probability_max")] public List<int?> PrecipitationProbabilityMax { get; set; } = [];
}

public class OmMinutely15
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("precipitation")] public List<double> Precipitation { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public List<int?> PrecipitationProbability { get; set; } = [];
}
