using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassIsland.Core;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Weather;

/// <summary>
/// Open-Meteo 免费额度的本地快照。
/// </summary>
public sealed record OpenMeteoQuotaInfo(int MinuteUsed, int HourUsed, int DayUsed)
{
    public const int PerMinuteLimit = 600;
    public const int PerHourLimit = 5000;
    public const int PerDayLimit = 10000;

    public int MinuteRemaining => Math.Max(0, PerMinuteLimit - MinuteUsed);

    public int HourRemaining => Math.Max(0, PerHourLimit - HourUsed);

    public int DayRemaining => Math.Max(0, PerDayLimit - DayUsed);

    /// <summary>
    /// 三个窗口里最紧的那个，就是「现在还能发几次请求」。
    /// </summary>
    public int Remaining => Math.Min(MinuteRemaining, Math.Min(HourRemaining, DayRemaining));
}

/// <summary>
/// 在本地记录发往 Open-Meteo 的每一次请求，用滚动窗口估算剩余的免费请求额度。
/// 免费额度：每分钟 600 次、每小时 5000 次、每天 10000 次。
/// </summary>
public class OpenMeteoQuotaService
{
    private readonly ILogger<OpenMeteoQuotaService> _logger;
    private readonly object _lock = new();
    private readonly List<DateTime> _calls = [];

    private static string DataPath =>
        Path.Combine(CommonDirectories.AppRootFolderPath, "OpenMeteoQuota.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public OpenMeteoQuotaService(ILogger<OpenMeteoQuotaService> logger)
    {
        _logger = logger;
        // 重启后把最近 24 小时的调用记录读回来，额度统计不会归零。
        Restore();
    }

    /// <summary>
    /// 记一次请求，并返回记录之后的额度快照。发请求前调用。
    /// </summary>
    public OpenMeteoQuotaInfo Record()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            _calls.Add(now);
            Prune(now);
            Persist();
            return Snapshot(now);
        }
    }

    /// <summary>
    /// 只读快照，不产生请求计数。
    /// </summary>
    public OpenMeteoQuotaInfo Current
    {
        get
        {
            lock (_lock)
            {
                Prune(DateTime.Now);
                return Snapshot(DateTime.Now);
            }
        }
    }

    private void Prune(DateTime now)
    {
        var threshold = now.AddHours(-24);
        var keepFrom = 0;
        while (keepFrom < _calls.Count && _calls[keepFrom] < threshold)
        {
            keepFrom++;
        }

        if (keepFrom > 0)
        {
            _calls.RemoveRange(0, keepFrom);
        }
    }

    private OpenMeteoQuotaInfo Snapshot(DateTime now)
    {
        var minute = 0;
        var hour = 0;
        var minuteThreshold = now.AddMinutes(-1);
        var hourThreshold = now.AddHours(-1);
        for (var i = _calls.Count - 1; i >= 0; i--)
        {
            var call = _calls[i];
            if (call < hourThreshold)
            {
                break;
            }

            hour++;
            if (call >= minuteThreshold)
            {
                minute++;
            }
        }

        return new OpenMeteoQuotaInfo(minute, hour, _calls.Count);
    }

    public void Restore()
    {
        try
        {
            if (!File.Exists(DataPath))
            {
                return;
            }

            var raw = JsonSerializer.Deserialize<List<long>>(File.ReadAllText(DataPath), SerializerOptions);
            if (raw == null)
            {
                return;
            }

            lock (_lock)
            {
                _calls.Clear();
                foreach (var ms in raw)
                {
                    _calls.Add(DateTime.UnixEpoch.AddMilliseconds(ms).ToLocalTime());
                }

                Prune(DateTime.Now);
            }

            _logger.LogInformation("已恢复 Open-Meteo 请求计数：{} 次（最近 24 小时）", _calls.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 Open-Meteo 请求计数失败，将从 0 开始统计");
        }
    }

    private void Persist()
    {
        try
        {
            var data = _calls.Select(x => new DateTimeOffset(x).ToUnixTimeMilliseconds()).ToList();
            var directory = Path.GetDirectoryName(DataPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = DataPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, SerializerOptions));
            File.Move(temp, DataPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 Open-Meteo 请求计数失败");
        }
    }
}
