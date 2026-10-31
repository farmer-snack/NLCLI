using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIsland.Core.Models.Weather;

/// <summary>
/// Open-Meteo /v1/forecast 的返回结构。
/// 这里不再逐个声明变量，而是把 current / hourly / daily / minutely_15 各存成
/// 「time + 所有字段」的原始结构，这样请求里加了什么变量，页面上就能显示什么，
/// 不会再出现「接口给了但页面没展示」的情况。
/// 文档：https://open-meteo.com/en/docs
/// </summary>
public class OpenMeteoForecast
{
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "";
    [JsonPropertyName("utc_offset_seconds")] public double UtcOffsetSeconds { get; set; }
    [JsonPropertyName("elevation")] public double? Elevation { get; set; }

    [JsonPropertyName("current")] public OmBlock Current { get; set; } = new();
    [JsonPropertyName("hourly")] public OmBlock Hourly { get; set; } = new();
    [JsonPropertyName("daily")] public OmBlock Daily { get; set; } = new();
    [JsonPropertyName("minutely_15")] public OmBlock Minutely15 { get; set; } = new();

    /// <summary>
    /// 各字段的单位由接口自己返回（°C、mm、km/h、%、s、W/m² …），
    /// 页面直接拿来用，不需要在代码里维护单位表。
    /// </summary>
    [JsonPropertyName("current_units")] public Dictionary<string, string> CurrentUnits { get; set; } = new();
    [JsonPropertyName("hourly_units")] public Dictionary<string, string> HourlyUnits { get; set; } = new();
    [JsonPropertyName("daily_units")] public Dictionary<string, string> DailyUnits { get; set; } = new();
    [JsonPropertyName("minutely_15_units")] public Dictionary<string, string> Minutely15Units { get; set; } = new();
}

/// <summary>
/// 一组时间序列：<see cref="Time"/> 是时间轴，<see cref="Values"/> 是其余所有字段的原始 JSON。
/// </summary>
[JsonConverter(typeof(OmBlockJsonConverter))]
public class OmBlock
{
    public List<string> Time { get; set; } = [];

    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 取某个字段在某一项上的原始值。
    /// 标量（current）忽略 <paramref name="index"/>；数组（hourly / daily / minutely_15）按索引取。
    /// </summary>
    public JsonElement? At(string key, int index = -1)
    {
        if (!Values.TryGetValue(key, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return element;
        }

        if (index < 0 || index >= element.GetArrayLength())
        {
            return null;
        }

        var item = element[index];
        return item.ValueKind == JsonValueKind.Null ? null : item;
    }

    public double? Number(string key, int index = -1)
    {
        var element = At(key, index);
        if (element is null)
        {
            return null;
        }

        return element.Value.ValueKind == JsonValueKind.Number && element.Value.TryGetDouble(out var value)
            ? value
            : null;
    }

    public int? Int(string key, int index = -1)
    {
        var value = Number(key, index);
        return value is null ? null : (int)Math.Round(value.Value);
    }

    public string? Text(string key, int index = -1)
    {
        var element = At(key, index);
        if (element is null)
        {
            return null;
        }

        return element.Value.ValueKind == JsonValueKind.String
            ? element.Value.GetString()
            : element.Value.ToString();
    }

    /// <summary>
    /// 时间轴长度（优先用真正的时间序列，标量块返回 1）。
    /// </summary>
    public int Count
    {
        get
        {
            if (Time.Count > 0)
            {
                return Time.Count;
            }

            foreach (var value in Values.Values)
            {
                if (value.ValueKind == JsonValueKind.Array)
                {
                    return value.GetArrayLength();
                }
            }

            return 1;
        }
    }
}

/// <summary>
/// 把一组时间序列读成 <see cref="OmBlock"/>，并把原样写回（缓存需要）。
/// </summary>
public sealed class OmBlockJsonConverter : JsonConverter<OmBlock>
{
    public override OmBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var block = new OmBlock();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return block;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("time") || property.NameEquals("interval"))
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        block.Time.Add(item.ValueKind == JsonValueKind.String
                            ? item.GetString() ?? ""
                            : item.ToString());
                    }
                }
                else if (property.Value.ValueKind == JsonValueKind.String)
                {
                    block.Time.Add(property.Value.GetString() ?? "");
                }

                continue;
            }

            block.Values[property.Name] = property.Value.Clone();
        }

        return block;
    }

    public override void Write(Utf8JsonWriter writer, OmBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("time");
        writer.WriteStartArray();
        foreach (var time in value.Time)
        {
            writer.WriteStringValue(time);
        }

        writer.WriteEndArray();

        foreach (var (key, element) in value.Values)
        {
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Open-Meteo 空气质量接口（air-quality-api.open-meteo.com）的返回结构。
/// 同样把全部字段收进 <see cref="OmBlock"/>，页面按字段自动展示。
/// </summary>
public class OpenMeteoAirQuality
{
    [JsonPropertyName("current")] public OmBlock Current { get; set; } = new();
    [JsonPropertyName("current_units")] public Dictionary<string, string> CurrentUnits { get; set; } = new();
}
