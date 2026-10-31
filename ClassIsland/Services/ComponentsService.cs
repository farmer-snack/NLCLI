using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Core.Models.Components;
using ClassIsland.Core.Services.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sentry;

namespace ClassIsland.Services;

using ComponentSettingsList = ObservableCollection<ComponentSettings>;

public class ComponentsService : ObservableRecipient, IComponentsService
{
    public static readonly string ComponentSettingsPath = Path.Combine(CommonDirectories.AppConfigPath, "ComponentLayouts/");

    private ComponentProfile _currentComponents = new();
    private IReadOnlyList<string> _componentConfigs = new List<string>();

    /// <summary>
    /// 当前组件配置对应的文件路径。切换配置方案时固定下来，避免旧方案的集合变更事件
    /// 把内容写进新方案的文件里（集控模式下所有方案互相覆盖就是由此引起的）。
    /// </summary>
    private string? _currentConfigPath;

    /// <summary>
    /// 当前组件配置集合的变更处理器。切换方案时需要解绑，否则每切换一次就多一个订阅。
    /// </summary>
    private NotifyCollectionChangedEventHandler? _currentConfigChangedHandler;

    public static ComponentProfile DefaultComponentProfile { get; } = new()
    {
        Lines =
        {
            new MainWindowLineSettings()
            {
                Children =
                {
                    new ComponentSettings
                    {
                        Id = "df3f8295-21f6-482e-bada-fa0e5f14bb66"
                    },
                    new ComponentSettings
                    {
                        Id = "1db2017d-e374-4bc6-9d57-0b4adf03a6b8"
                    }
                }
            }
        }
    };

    private string SelectedConfigFullPath =>
        Path.GetFullPath(Path.Combine(ComponentSettingsPath, SettingsService.Settings.CurrentComponentConfig + ".json"));

    private string CurrentConfigFullPath =>
        Path.GetFullPath(Path.Combine(ComponentSettingsPath, CurrentConfigName + ".json"));

    private SettingsService SettingsService { get; }
    public ILogger<ComponentsService> Logger { get; }
    public IManagementService ManagementService { get; }

    private string CurrentConfigName { get; set; } = "Default";

    public ComponentsService(SettingsService settingsService, ILogger<ComponentsService> logger, IManagementService managementService)
    {
        SettingsService = settingsService;
        Logger = logger;
        ManagementService = managementService;
        SettingsService.Settings.PropertyChanged += SettingsOnPropertyChanged;

        if (!Directory.Exists(ComponentSettingsPath))
        {
            Directory.CreateDirectory(ComponentSettingsPath);
        }

        RefreshConfigs();
        LoadConfig();
        RefreshConfigs();
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsService.Settings.CurrentComponentConfig))
            return;
        SaveConfig();
        LoadConfig();
    }

    public async Task LoadManagementConfig()
    {
        if (!ManagementService.IsManagementEnabled || ManagementService.Connection == null)
        {
            return;
        }
        
        // 只有集控清单真的下发了组件配置时才算「集控模式」。
        // 清单里没有 ComponentsSource（很多学校只用集控同步课表/科目）时，如果把本地也锁成集控模式，
        // 切换配置方案就不会重新加载本地文件，所有方案会被写成一模一样的内容（#2034）。
        IsManagementMode = !string.IsNullOrWhiteSpace(ManagementService.Manifest.ComponentsSource.Value);
        try
        {
            if (!ManagementService.Manifest.ComponentsSource.IsNewerAndNotNull(ManagementService.Versions
                    .ComponentsVersion))
            {
                return;
            }
            CurrentComponents = await ManagementService.Connection
                .SaveJsonAsync<ComponentProfile>(ManagementService.Manifest.ComponentsSource.Value!,
                    Management.ManagementService.ManagementComponentsPath);
            ManagementService.Versions.ComponentsVersion = ManagementService.Manifest.ComponentsSource.Version;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "无法从集控拉取组件配置");
            CurrentComponents =
                ConfigureFileHelper.LoadConfig<ComponentProfile>(Management.ManagementService
                    .ManagementComponentsPath);
        }
        LoadConfig();
    
    }

    private void LoadConfig()
    {
        // 解除上一次配置方案的集合变更订阅，避免切换方案后旧的集合仍然把内容写进新方案。
        if (_currentComponents is { } previousComponents && _currentConfigChangedHandler is { } previousHandler)
        {
            previousComponents.Lines.CollectionChanged -= previousHandler;
        }
        _currentConfigChangedHandler = null;
        _currentConfigPath = null;

        if (!IsManagementMode)
        {
            if (!File.Exists(SelectedConfigFullPath))
            {
                CurrentComponents = ConfigureFileHelper.CopyObject(DefaultComponentProfile);
                // 此时 CurrentConfigName 还没更新，直接保存会把默认配置写进上一个方案，
                // 必须显式指定目标文件。
                ConfigureFileHelper.SaveConfig(SelectedConfigFullPath, CurrentComponents);
            }
            else
            {
                CurrentComponents = ConfigureFileHelper.LoadConfig<ComponentProfile>(SelectedConfigFullPath);
            }
        }
        
        CurrentConfigName = SettingsService.Settings.CurrentComponentConfig;
        _currentConfigPath = CurrentConfigFullPath;
        var currentProfile = CurrentComponents;
        _currentConfigChangedHandler = (_, _) =>
        {
            if (IsManagementMode || _currentConfigPath is null)
            {
                return;
            }

            ConfigureFileHelper.SaveConfig(_currentConfigPath, currentProfile);
        };
        currentProfile.Lines.CollectionChanged += _currentConfigChangedHandler;

        var migrated = false;
        foreach (var i in CurrentComponents.Lines.SelectMany(x => x.Children))
        {
            if (!ComponentRegistryService.MigrationPairs.TryGetValue(new Guid(i.Id), out var targetGuid))
            {
                if (i.AssociatedComponentInfo.ComponentType != null)
                    LoadComponentSettings(i, i.AssociatedComponentInfo.ComponentType.BaseType!);
                continue;
            }
            
            Logger.LogInformation("迁移组件 {} -> {}", i.Id, targetGuid);
            i.IsMigrated = true;
            i.MigrationSource = new Guid(i.Id);
            i.Id = targetGuid.ToString();
            migrated = true;
        }

        if (migrated)
        {
            SaveConfig();
        }
    }

    public void SaveConfig()
    {
        // 集控模式下的组件配置由集控下发，写回本地方案会把集控内容污染到本地配置里。
        if (IsManagementMode)
        {
            return;
        }

        ConfigureFileHelper.SaveConfig(_currentConfigPath ?? CurrentConfigFullPath, CurrentComponents);
    }

    public IReadOnlyList<string> ComponentConfigs
    {
        get => _componentConfigs;
        set
        {
            if (Equals(value, _componentConfigs)) return;
            _componentConfigs = value;
            OnPropertyChanged();
        }
    }

    public void RefreshConfigs()
    {
        ComponentConfigs = Directory.GetFiles(ComponentSettingsPath, "*.json").Select(Path.GetFileNameWithoutExtension).SkipWhile(x => x is null).ToList()!;
    }

    public bool IsManagementMode { get; set; } = false;


    public ComponentProfile CurrentComponents
    {
        get => _currentComponents;
        set
        {
            if (Equals(value, _currentComponents)) return;
            _currentComponents = value;
            OnPropertyChanged();
        }
    }

    public ComponentBase? GetComponent(ComponentSettings settings, bool isSettings)
    {
        var sb = Stopwatch.StartNew();
        var type = isSettings ? settings.AssociatedComponentInfo.SettingsType : settings.AssociatedComponentInfo.ComponentType;
        if (type == null)
        {
            return null;
        }

        var c = IAppHost.Host?.Services.GetService(type);
        if (c is not ComponentBase component)
        {
            return null;
        }


        var baseType = type.BaseType;
        var migrated = settings.IsMigrated && !isSettings;
        if (migrated)
        {
            if (baseType?.GetGenericArguments().Length > 0)
            {
                var settingsType = baseType.GetGenericArguments().First();
                var componentSettings = Activator.CreateInstance(settingsType);
                settings.Settings = componentSettings;
                component.SettingsInternal = componentSettings;
            }
            component.OnMigrated(settings.MigrationSource, settings.Settings);
        } 
        if (baseType?.GetGenericArguments().Length > 0 && !migrated)
        {
            var componentSettings = LoadComponentSettings(settings, baseType);

            component.SettingsInternal = componentSettings;
        }
        sb.Stop();
        if (sb.Elapsed >= TimeSpan.FromMilliseconds(500))
        {
            Logger.LogWarning("组件 {}/{} ({}) 初始化消耗了太长时间，耗时为 {}ms", settings.Id, isSettings ? "settings" : "component", settings.AssociatedComponentInfo.Name, sb.ElapsedMilliseconds);
        }
        return component;
    }

    internal static object? LoadComponentSettings(ComponentSettings settings, Type baseType)
    {
        var settingsType = baseType.GetGenericArguments().FirstOrDefault();
        if (settingsType == null)
        {
            return null;
        }
        var componentSettings = settings.Settings ?? Activator.CreateInstance(settingsType);
        if (componentSettings is JsonElement json)
        {
            componentSettings = json.Deserialize(settingsType);
        }
        settings.Settings = componentSettings;
        return componentSettings;
    }
}