namespace ClassIsland.Helpers;

/// <summary>
/// WMO 天气代码 → 中文描述 / 图标。
/// 代码表：https://open-meteo.com/en/docs (Weather variable documentation)
/// </summary>
public static class WeatherCodeHelper
{
    public static string GetText(int code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "冻雨",
        71 or 73 or 75 => "雪",
        77 => "雪粒",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 => "雷暴",
        96 or 99 => "雷暴伴冰雹",
        _ => "未知"
    };

    public static string GetEmoji(int code) => code switch
    {
        0 => "☀️",
        1 => "🌤️",
        2 => "⛅",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 or 56 or 57 => "🌦️",
        61 or 63 or 65 or 66 or 67 => "🌧️",
        71 or 73 or 75 or 77 => "🌨️",
        80 or 81 or 82 => "🌦️",
        85 or 86 => "🌨️",
        95 or 96 or 99 => "⛈️",
        _ => "❔"
    };
}
