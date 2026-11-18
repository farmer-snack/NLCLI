using System;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

using ClassIsland.Models.Debugging;

namespace ClassIsland.Controls;

/// <summary>
/// 根据 <see cref="ConfigVariableItem"/> 的类型生成对应的编辑控件。
/// 只读变量会显示为不可编辑的文本框。
/// </summary>
public class SettingsVariableEditor : UserControl
{
    private ConfigVariableItem? _item;

    public SettingsVariableEditor()
    {
        DataContextChanged += (_, _) => BuildEditor();
        AttachedToVisualTree += (_, _) => BuildEditor();
    }

    private void BuildEditor()
    {
        if (DataContext is not ConfigVariableItem item)
        {
            _item = null;
            Content = null;
            return;
        }

        if (ReferenceEquals(_item, item))
        {
            return;
        }

        _item = item;
        Content = CreateEditor(item);
    }

    private static Control CreateEditor(ConfigVariableItem item)
    {
        if (!item.CanWrite)
        {
            // 只读值（集合、复杂对象等）经常是一大串 JSON，单行文本框只能看到开头几个字符。
            // 改成多行换行显示，超过一定高度后在框内滚动，既能看全又不会把页面撑爆。
            var readOnlyBox = new TextBox
            {
                IsReadOnly = true,
                Watermark = item.IsCollection ? "集合（只读预览）" : "只读",
                MinWidth = 220,
                MinHeight = 34,
                MaxHeight = 160,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetVerticalScrollBarVisibility(readOnlyBox, ScrollBarVisibility.Auto);
            readOnlyBox.Bind(TextBox.TextProperty, new Binding(nameof(ConfigVariableItem.DisplayValue))
            {
                Source = item,
                Mode = BindingMode.OneWay
            });
            return readOnlyBox;
        }

        switch (item.Kind)
        {
            case ConfigVariableKind.Bool:
            {
                var toggleSwitch = new ToggleSwitch
                {
                    OnContent = "开",
                    OffContent = "关",
                    MinWidth = 0
                };
                toggleSwitch.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(ConfigVariableItem.BoolValue))
                {
                    Source = item,
                    Mode = BindingMode.TwoWay
                });
                return toggleSwitch;
            }
            case ConfigVariableKind.Integer:
            case ConfigVariableKind.Integer64:
            case ConfigVariableKind.Float:
            {
                var numeric = new NumericUpDown
                {
                    Minimum = -999_999_999_999m,
                    Maximum = 999_999_999_999m,
                    Increment = item.Kind == ConfigVariableKind.Float ? 0.1m : 1m,
                    FormatString = item.Kind == ConfigVariableKind.Float ? "0.###" : "0",
                    MinWidth = 220,
                    MaxWidth = 340,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ShowButtonSpinner = true
                };
                numeric.Bind(NumericUpDown.ValueProperty, new Binding(nameof(ConfigVariableItem.DecimalValue))
                {
                    Source = item,
                    Mode = BindingMode.TwoWay
                });
                return numeric;
            }
            case ConfigVariableKind.Enum:
            {
                var comboBox = new ComboBox
                {
                    ItemsSource = item.EnumValues,
                    SelectedItem = item.EnumValue,
                    MinWidth = 220,
                    MaxWidth = 340,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                comboBox.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(ConfigVariableItem.EnumValue))
                {
                    Source = item,
                    Mode = BindingMode.TwoWay
                });
                return comboBox;
            }
            default:
            {
                var textBox = new TextBox
                {
                    Text = item.StringValue,
                    Watermark = item.TypeName,
                    MinWidth = 220,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                textBox.Bind(TextBox.TextProperty, new Binding(nameof(ConfigVariableItem.StringValue))
                {
                    Source = item,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                });
                return textBox;
            }
        }
    }
}
