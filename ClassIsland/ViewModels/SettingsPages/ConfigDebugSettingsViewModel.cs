using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

using ClassIsland.Models;
using ClassIsland.Models.Debugging;
using ClassIsland.Services;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

namespace ClassIsland.ViewModels.SettingsPages;

/// <summary>
/// 「变量调试」设置页的 ViewModel。
/// 通过反射列出 <see cref="Settings"/> 中的全部配置变量，并支持直接修改。
/// 修改会走 <see cref="Settings"/> 的属性 setter，因此 <see cref="SettingsService"/> 会自动保存到 Settings.json。
/// </summary>
public partial class ConfigDebugSettingsViewModel : ObservableObject, IDisposable
{
    public const string AllTypesFilter = "全部类型";

    private readonly SettingsService _settingsService;
    private readonly ILogger<ConfigDebugSettingsViewModel> _logger;
    private readonly List<ConfigVariableItem> _allVariables = [];
    private readonly Dictionary<string, ConfigVariableItem> _variablesByName = new(StringComparer.Ordinal);
    private readonly Settings _defaultSettings = new();
    private bool _subscribed;

    public ConfigDebugSettingsViewModel(SettingsService settingsService,
        ILogger<ConfigDebugSettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        BuildVariables();
        EnsureSubscribed();
        ApplyFilter();
    }

    public SettingsService SettingsService => _settingsService;

    public string[] TypeFilters { get; } =
        [AllTypesFilter, "布尔", "数字", "文本", "日期时间", "颜色", "枚举", "其他"];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedTypeFilter = AllTypesFilter;

    [ObservableProperty]
    private bool _showReadOnly = true;

    [ObservableProperty]
    private IReadOnlyList<ConfigVariableItem> _variables = Array.Empty<ConfigVariableItem>();

    [ObservableProperty]
    private string _statusText = "";

    public Settings DefaultSettings => _defaultSettings;

    private void BuildVariables()
    {
        _allVariables.Clear();
        _variablesByName.Clear();

        var properties = typeof(Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => x.GetIndexParameters().Length == 0 && x.GetMethod is { IsPublic: true })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties)
        {
            try
            {
                var item = new ConfigVariableItem(_settingsService.Settings, property);
                _allVariables.Add(item);
                _variablesByName[item.Name] = item;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "无法读取配置变量 {Property}。", property.Name);
            }
        }

        _logger.LogInformation("已加载 {Count} 个配置变量。", _allVariables.Count);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTypeFilterChanged(string value) => ApplyFilter();

    partial void OnShowReadOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<ConfigVariableItem> query = _allVariables;

        if (!ShowReadOnly)
        {
            query = query.Where(x => x.CanWrite);
        }

        var keyword = SearchText?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.TypeName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        query = SelectedTypeFilter switch
        {
            "布尔" => query.Where(x => x.Kind == ConfigVariableKind.Bool),
            "数字" => query.Where(x => x.Kind is ConfigVariableKind.Integer or ConfigVariableKind.Integer64
                or ConfigVariableKind.Float),
            "文本" => query.Where(x => x.Kind == ConfigVariableKind.Text),
            "日期时间" => query.Where(x => x.Kind == ConfigVariableKind.DateTime),
            "颜色" => query.Where(x => x.Kind == ConfigVariableKind.Color),
            "枚举" => query.Where(x => x.Kind == ConfigVariableKind.Enum),
            "其他" => query.Where(x => x.Kind is ConfigVariableKind.Complex or ConfigVariableKind.Guid
                or ConfigVariableKind.Version),
            _ => query
        };

        var list = query.ToList();
        Variables = list;
        StatusText = $"共 {_allVariables.Count} 个变量，当前显示 {list.Count} 个。";
    }

    public void ResetVariable(ConfigVariableItem item)
    {
        item.ResetTo(_defaultSettings);
        _logger.LogInformation("已将配置变量 {Name} 重置为默认值。", item.Name);
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || !_variablesByName.TryGetValue(e.PropertyName, out var item))
        {
            return;
        }

        item.RefreshFromTarget();
    }

    /// <summary>
    /// 订阅配置变更。页面被缓存后重新显示时需要重新订阅。
    /// </summary>
    public void EnsureSubscribed()
    {
        if (_subscribed) return;
        _settingsService.Settings.PropertyChanged += SettingsOnPropertyChanged;
        _subscribed = true;
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _settingsService.Settings.PropertyChanged -= SettingsOnPropertyChanged;
        _subscribed = false;
    }
}
