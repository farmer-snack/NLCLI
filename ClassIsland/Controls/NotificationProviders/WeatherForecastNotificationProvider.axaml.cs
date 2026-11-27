using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ClassIsland.Core.Models.Weather;

namespace ClassIsland.Controls.NotificationProviders;

/// <summary>
/// 某一天的预报条目（供 7 天列表绑定）。
/// </summary>
public sealed record DayForecastItem(string Label, RangedValue Weather, string TempFrom, string TempTo);

/// <summary>
/// WeatherForecastNotificationProvider.xaml 的交互逻辑
/// </summary>
public partial class WeatherForecastNotificationProvider : UserControl, INotifyPropertyChanged
{
    private const int DayCount = 7;

    private bool _isOverlay = false;
    private WeatherInfo _info = new();

    public bool IsOverlay
    {
        get => _isOverlay;
        set
        {
            if (value == _isOverlay) return;
            _isOverlay = value;
            OnPropertyChanged();
        }
    }

    public WeatherInfo Info
    {
        get => _info;
        set
        {
            if (Equals(value, _info)) return;
            _info = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 未来 7 天的预报（今天起）。数据不足 7 天时按实际条数给。
    /// </summary>
    public List<DayForecastItem> Days { get; } = [];

    public WeatherForecastNotificationProvider(bool isOverlay, WeatherInfo info)
    {
        IsOverlay = isOverlay;
        Info = info;
        BuildDays();
        InitializeComponent();
    }

    private void BuildDays()
    {
        // 接口在字段缺失时会把这些集合留成 null（StatusValueBase.Value 声明为 default!），
        // 直接取 .Count 会在构造提醒时抛 NullReferenceException。
        // 这里是在提醒提供方的构造函数里，崩掉的不只是天气卡片，而是整条提醒链路。
        // ForecastDaily 本身也可能是 null，同样要兜住。
        var weather = Info?.ForecastDaily?.Weather?.Value;
        var temperature = Info?.ForecastDaily?.Temperature?.Value;
        if (weather == null || temperature == null)
        {
            return;
        }

        var count = Math.Min(DayCount, Math.Min(weather.Count, temperature.Count));
        for (var i = 0; i < count; i++)
        {
            var item = temperature[i];
            // 温度条目本身也可能缺失（默认值 0），取 From/To 前必须判空，
            // 否则一条缺数据的预报就能把整个提醒构造过程打断。
            if (item == null)
            {
                Days.Add(new DayForecastItem(DayLabel(i), weather[i], "", ""));
                continue;
            }
            Days.Add(new DayForecastItem(DayLabel(i), weather[i], item.From, item.To));
        }
    }

    private static string DayLabel(int offset) => offset switch
    {
        0 => "今天",
        1 => "明天",
        2 => "后天",
        _ => "周" + "日一二三四五六"[(int)DateTime.Today.AddDays(offset).DayOfWeek]
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
