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
        var daily = Info.ForecastDaily;
        var count = Math.Min(DayCount, Math.Min(daily.Weather.Value.Count, daily.Temperature.Value.Count));
        for (var i = 0; i < count; i++)
        {
            var temperature = daily.Temperature.Value[i];
            Days.Add(new DayForecastItem(DayLabel(i), daily.Weather.Value[i], temperature.From, temperature.To));
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
