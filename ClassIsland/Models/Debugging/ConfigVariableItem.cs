using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.Models.Debugging;

/// <summary>
/// 配置变量的类型分类，用于决定在调试页面里使用哪种编辑器。
/// </summary>
public enum ConfigVariableKind
{
    Bool,
    Integer,
    Integer64,
    Float,
    Text,
    DateTime,
    Color,
    Enum,
    Guid,
    Version,
    /// <summary>
    /// 集合或复杂对象，只做只读预览。
    /// </summary>
    Complex
}

/// <summary>
/// 配置文件（<see cref="Settings"/>）中的一个变量。
/// 读写都直接作用在 <see cref="Settings"/> 实例上，因此修改会立即触发
/// <see cref="Services.SettingsService"/> 的属性变更与自动保存。
/// </summary>
public class ConfigVariableItem : ObservableObject
{
    private readonly object _target;
    private readonly PropertyInfo _property;
    private readonly Type _type;
    private readonly Type _underlyingType;
    private string? _lastError;

    public ConfigVariableItem(object target, PropertyInfo property)
    {
        _target = target;
        _property = property;
        _type = property.PropertyType;
        _underlyingType = Nullable.GetUnderlyingType(_type) ?? _type;
        IsCollection = typeof(IEnumerable).IsAssignableFrom(_underlyingType) && _underlyingType != typeof(string);
        Kind = ResolveKind(_underlyingType, IsCollection);
        CanWrite = property.CanWrite && property.SetMethod is { IsPublic: true } &&
                   Kind != ConfigVariableKind.Complex;
        IsPersisted = property.GetCustomAttribute<JsonIgnoreAttribute>() == null;
    }

    /// <summary>
    /// 变量名（即配置文件中的键名）。
    /// </summary>
    public string Name => _property.Name;

    public ConfigVariableKind Kind { get; }

    public bool CanWrite { get; }

    public bool IsCollection { get; }

    /// <summary>
    /// 是否会写入配置文件。带 <see cref="JsonIgnoreAttribute"/> 的变量不会保存。
    /// </summary>
    public bool IsPersisted { get; }

    public string TypeName => Kind switch
    {
        ConfigVariableKind.Bool => "布尔",
        ConfigVariableKind.Integer => "整数",
        ConfigVariableKind.Integer64 => "长整数",
        ConfigVariableKind.Float => "小数",
        ConfigVariableKind.Text => "文本",
        ConfigVariableKind.DateTime => "日期时间",
        ConfigVariableKind.Color => "颜色",
        ConfigVariableKind.Enum => $"枚举（{_underlyingType.Name}）",
        ConfigVariableKind.Guid => "GUID",
        ConfigVariableKind.Version => "版本号",
        _ => IsCollection ? $"集合（{_underlyingType.Name}）" : _underlyingType.Name
    };

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (value == _lastError) return;
            _lastError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(LastError);

    public object? Value
    {
        get
        {
            try
            {
                return _property.GetValue(_target);
            }
            catch
            {
                return null;
            }
        }
    }

    public string DisplayValue => FormatValue(Value);

    public bool BoolValue
    {
        get => Value is true;
        set
        {
            if (TrySetValue(value))
            {
                NotifyValueChanged();
            }
        }
    }

    public decimal? DecimalValue
    {
        get
        {
            var value = Value;
            if (value is null) return null;
            try
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }
        set
        {
            if (value is null) return;
            object converted = Kind switch
            {
                ConfigVariableKind.Integer => Convert.ToInt32(value.Value),
                ConfigVariableKind.Integer64 => Convert.ToInt64(value.Value),
                ConfigVariableKind.Float => Convert.ToDouble(value.Value),
                _ => value.Value
            };
            if (TrySetValue(converted))
            {
                NotifyValueChanged();
            }
        }
    }

    public string StringValue
    {
        get => FormatValue(Value);
        set
        {
            if (!CanWrite) return;
            if (TryParse(value, out var parsed))
            {
                TrySetValue(parsed);
            }
            else
            {
                LastError = $"无法把「{value}」解析为 {TypeName}";
            }
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<object> EnumValues =>
        Kind == ConfigVariableKind.Enum
            ? Enum.GetValues(_underlyingType).Cast<object>().ToList()
            : Array.Empty<object>();

    public object? EnumValue
    {
        get => Value;
        set
        {
            if (value is null) return;
            if (TrySetValue(value))
            {
                NotifyValueChanged();
            }
        }
    }

    /// <summary>
    /// 把变量恢复为 <paramref name="defaultTarget"/>（一个全新的 <see cref="Settings"/> 实例）里的值。
    /// </summary>
    public void ResetTo(object defaultTarget)
    {
        try
        {
            TrySetValue(_property.GetValue(defaultTarget));
            NotifyValueChanged();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// 外部（例如配置文件被其它途径修改）导致值变化时刷新界面。
    /// </summary>
    public void RefreshFromTarget()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(DecimalValue));
        OnPropertyChanged(nameof(StringValue));
        OnPropertyChanged(nameof(EnumValue));
    }

    private void NotifyValueChanged()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(DecimalValue));
        OnPropertyChanged(nameof(StringValue));
        OnPropertyChanged(nameof(EnumValue));
    }

    private bool TrySetValue(object? value)
    {
        if (!CanWrite) return false;
        try
        {
            if (value is not null && !_type.IsInstanceOfType(value))
            {
                value = Convert.ChangeType(value, _underlyingType, CultureInfo.InvariantCulture);
            }
            _property.SetValue(_target, value);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    private bool TryParse(string text, out object? value)
    {
        value = null;
        switch (Kind)
        {
            case ConfigVariableKind.Text:
                value = text;
                return true;
            case ConfigVariableKind.Integer:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    value = i;
                    return true;
                }
                return false;
            case ConfigVariableKind.Integer64:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    value = l;
                    return true;
                }
                return false;
            case ConfigVariableKind.Float:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    value = d;
                    return true;
                }
                return false;
            case ConfigVariableKind.DateTime:
                if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
                {
                    value = dt;
                    return true;
                }
                return false;
            case ConfigVariableKind.Color:
                if (Color.TryParse(text, out var color))
                {
                    value = color;
                    return true;
                }
                return false;
            case ConfigVariableKind.Guid:
                if (Guid.TryParse(text, out var guid))
                {
                    value = guid;
                    return true;
                }
                return false;
            case ConfigVariableKind.Version:
                if (Version.TryParse(text, out var version))
                {
                    value = version;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private string FormatValue(object? value) => value switch
    {
        null => "",
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        Color color => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}",
        Version version => version.ToString(),
        Guid guid => guid.ToString(),
        Enum @enum => @enum.ToString(),
        string text => text,
        _ => Kind == ConfigVariableKind.Complex ? SafeJson(value) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static string SafeJson(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch
        {
            return value.ToString() ?? "";
        }
    }

    private static ConfigVariableKind ResolveKind(Type type, bool isCollection)
    {
        if (isCollection) return ConfigVariableKind.Complex;
        if (type == typeof(bool)) return ConfigVariableKind.Bool;
        if (type == typeof(int)) return ConfigVariableKind.Integer;
        if (type == typeof(long)) return ConfigVariableKind.Integer64;
        if (type is var t && (t == typeof(double) || t == typeof(float) || t == typeof(decimal)))
            return ConfigVariableKind.Float;
        if (type == typeof(string)) return ConfigVariableKind.Text;
        if (type == typeof(DateTime)) return ConfigVariableKind.DateTime;
        if (type == typeof(Color)) return ConfigVariableKind.Color;
        if (type.IsEnum) return ConfigVariableKind.Enum;
        if (type == typeof(Guid)) return ConfigVariableKind.Guid;
        if (type == typeof(Version)) return ConfigVariableKind.Version;
        return ConfigVariableKind.Complex;
    }
}
