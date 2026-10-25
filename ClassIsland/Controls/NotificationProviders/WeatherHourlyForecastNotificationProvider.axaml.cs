using System;
using System.Collections.Generic;
using Avalonia.Controls;
using ClassIsland.Core.Models.Weather;

namespace ClassIsland.Controls.NotificationProviders;

/// <summary>
/// 某一个小时的预报条目（供 12 小时列表绑定）。
/// </summary>
public sealed record HourForecastItem(string Label, string WeatherCode, int Temperature);

/// <summary>
/// WeatherHourlyForecastNotificationProvider.xaml 的交互逻辑
/// </summary>
public partial class WeatherHourlyForecastNotificationProvider : UserControl
{
    private const int HourCount = 12;
    private const int FallbackWeatherCode = 99;

    public bool IsOverlay { get; }
    public WeatherInfo Info { get; }
    public DateTime BaseTime { get; }

    public List<DateTime> DateTimes { get; } = [];

    /// <summary>
    /// 接下来 12 小时的预报。
    /// </summary>
    public List<HourForecastItem> Hours { get; } = [];

    public WeatherHourlyForecastNotificationProvider(bool isOverlay, WeatherInfo info, DateTime baseTime)
    {
        IsOverlay = isOverlay;
        Info = info;
        BaseTime = baseTime;

        var weather = info.ForecastHourly.Weather.Value;
        var temperature = info.ForecastHourly.Temperature.Value;

        for (var i = 0; i < HourCount; i++)
        {
            var time = baseTime.AddHours(i);
            DateTimes.Add(time);

            // 上游数组长度不固定，短了就退化成"未知天气 + 0℃"，别让模板绑定抛越界。
            var code = i < weather.Count ? weather[i] : FallbackWeatherCode;
            var temp = i < temperature.Count ? temperature[i] : 0;
            Hours.Add(new HourForecastItem(time.ToString("HH:00"), code.ToString(), temp));
        }

        InitializeComponent();
    }
}
