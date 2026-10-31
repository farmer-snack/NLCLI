using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIsland.Core;
using ClassIsland.Core.Models.Weather;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Weather;

/// <summary>
/// Open-Meteo 本地缓存条目。
/// </summary>
public class OpenMeteoCacheEntry
{
    /// <summary>
    /// 缓存结构版本。模型改成「全字段字典」后旧缓存读不出内容，版本不一致时直接丢弃重拉。
    /// </summary>
    public const int CurrentVersion = 2;

    [JsonPropertyName("version")] public int Version { get; set; }

    [JsonPropertyName("cachedAt")] public DateTime CachedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("latitude")] public double Latitude { get; set; }

    [JsonPropertyName("longitude")] public double Longitude { get; set; }

    [JsonPropertyName("forecast")] public OpenMeteoForecast? Forecast { get; set; }

    [JsonPropertyName("airQuality")] public OpenMeteoAirQuality? AirQuality { get; set; }

    [JsonIgnore] public TimeSpan Age => DateTime.Now - CachedAt;
}

/// <summary>
/// 把「天气详情」页最近一次成功拉到的 Open-Meteo 数据缓存到本地，
/// 下次打开页面时可以先秒开旧数据，再决定要不要联网刷新。
/// </summary>
public class OpenMeteoCacheService(ILogger<OpenMeteoCacheService> logger)
{
    private readonly ILogger<OpenMeteoCacheService> _logger = logger;
    private readonly object _lock = new();

    private static string CachePath =>
        Path.Combine(CommonDirectories.AppRootFolderPath, "OpenMeteoCache.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// 读取缓存。经纬度与当前设置不一致（换城市了）时视为无缓存。
    /// </summary>
    public OpenMeteoCacheEntry? Load(double latitude, double longitude)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            OpenMeteoCacheEntry? entry;
            lock (_lock)
            {
                entry = JsonSerializer.Deserialize<OpenMeteoCacheEntry>(File.ReadAllText(CachePath), SerializerOptions);
            }

            if (entry?.Forecast == null)
            {
                return null;
            }

            if (entry.Version < OpenMeteoCacheEntry.CurrentVersion)
            {
                _logger.LogInformation("Open-Meteo 缓存结构版本过旧（{} < {}），忽略缓存",
                    entry.Version, OpenMeteoCacheEntry.CurrentVersion);
                return null;
            }

            if (Math.Abs(entry.Latitude - latitude) > 0.01 || Math.Abs(entry.Longitude - longitude) > 0.01)
            {
                _logger.LogInformation("Open-Meteo 缓存的城市与当前设置不一致，忽略缓存");
                return null;
            }

            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 Open-Meteo 缓存失败，将忽略缓存");
            return null;
        }
    }

    public void Save(OpenMeteoCacheEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry, SerializerOptions);
            lock (_lock)
            {
                var directory = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 先写临时文件再替换，避免写一半时应用退出留下半个 JSON。
                var temp = CachePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, CachePath, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 Open-Meteo 缓存失败");
        }
    }

    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(CachePath))
                {
                    File.Delete(CachePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除 Open-Meteo 缓存失败");
        }
    }
}
