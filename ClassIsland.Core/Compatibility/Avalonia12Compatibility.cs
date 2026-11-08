using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace ClassIsland.Core.Compatibility;

/// <summary>
/// Avalonia 12 兼容垫片。
/// </summary>
/// <remarks>
/// 个别 Avalonia 11 API 在 12 中并不只是改了名字，而是语义发生了变化，无法靠类型/成员重定向直接实现。
/// 这些 API 统一由本类提供等价入口，供插件兼容层把旧调用点重写到这里。
/// </remarks>
public static class Avalonia12Compatibility
{
    /// <summary>
    /// 创建带路径和模式的绑定。Avalonia 12 移除了 <c>Binding(string, BindingMode)</c> 构造函数。
    /// </summary>
    public static Binding CreateBinding(string path, BindingMode mode) => new(path) { Mode = mode };

    /// <summary>
    /// 取 <see cref="ToggleButton"/> 的选中事件。Avalonia 12 将 Checked/Unchecked 合并为 IsCheckedChanged。
    /// </summary>
    public static RoutedEvent<RoutedEventArgs> GetCheckedEvent(ToggleButton button) =>
        ToggleButton.IsCheckedChangedEvent;

    /// <summary>
    /// 取 <see cref="ToggleButton"/> 的取消选中事件。Avalonia 12 将 Checked/Unchecked 合并为 IsCheckedChanged。
    /// </summary>
    public static RoutedEvent<RoutedEventArgs> GetUncheckedEvent(ToggleButton button) =>
        ToggleButton.IsCheckedChangedEvent;

    /// <summary>
    /// 绑定属性到数据源。Avalonia 12 移除了允许 <c>IBinding</c> 的静态扩展重载。
    /// </summary>
    public static IDisposable Bind(
        AvaloniaObject target,
        AvaloniaProperty property,
        BindingBase binding,
        object? anchor) =>
        target.Bind(property, binding);

    /// <summary>
    /// 判断数据传输对象是否包含指定格式的数据。
    /// </summary>
    public static bool Contains(this IDataTransfer data, string format)
    {
        var target = ResolveFormat(format);
        return data.Formats.Any(x => x.Equals(target));
    }

    /// <summary>
    /// 从数据传输对象读取指定格式的数据。
    /// </summary>
    public static object? Get(this IDataTransfer data, string format)
    {
        var target = ResolveFormat(format);
        foreach (var item in data.Items)
        {
            var value = item.TryGetRaw(target);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// 向数据传输对象写入指定格式的数据。
    /// </summary>
    public static void Set(this DataTransfer data, string format, object? value)
    {
        switch (format)
        {
            case "Text":
            case "text":
            case "UnicodeText":
            case "unicodeText":
                data.Add(DataTransferItem.CreateText(value as string ?? value?.ToString() ?? ""));
                return;
            case "Bitmap":
            case "BitmapImage":
                if (value is Bitmap bitmap)
                {
                    data.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
                    return;
                }

                break;
            case "Files":
            case "FileNames":
            case "FileDrop":
                if (value is IEnumerable<IStorageItem> files)
                {
                    foreach (var file in files)
                    {
                        data.Add(DataTransferItem.CreateFile(file));
                    }

                    return;
                }

                break;
        }

        data.Add(DataTransferItem.Create(DataFormat.CreateInProcessFormat<object>(format), value ?? new object()));
    }

    /// <summary>
    /// 把 Avalonia 11 的剪贴板格式名映射到 Avalonia 12 的 <see cref="DataFormat"/>。
    /// 未知格式按进程内自定义格式处理。
    /// </summary>
    private static DataFormat ResolveFormat(string format) => format switch
    {
        "Text" or "text" or "UnicodeText" or "unicodeText" => DataFormat.Text,
        "Bitmap" or "BitmapImage" => DataFormat.Bitmap,
        "Files" or "FileNames" or "FileDrop" => DataFormat.File,
        _ => DataFormat.CreateInProcessFormat<object>(format)
    };
}
