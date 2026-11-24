using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

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

    /// <summary>
    /// 开发构建日志最多保留的字符数。编译输出可能非常长，
    /// 不设上限会让文本控件持续变慢，最终拖住整个设置窗口。
    /// </summary>
    private const int MaxDevelopmentUpdateLogLength = 60000;

    /// <summary>
    /// 变量列表每批渲染的数量。226 个变量若一次性全部塞给 ItemsControl，会在 UI 线程
    /// 同步实例化几千个可视化对象，表现为「点进页面卡一下」。分批后每帧只渲染一批。
    /// </summary>
    private const int VariableRenderBatchSize = 40;

    private readonly SettingsService _settingsService;
    private readonly ILogger<ConfigDebugSettingsViewModel> _logger;
    private readonly List<ConfigVariableItem> _allVariables = [];
    private readonly Dictionary<string, ConfigVariableItem> _variablesByName = new(StringComparer.Ordinal);
    private readonly Settings _defaultSettings = new();
    private readonly StringBuilder _developmentUpdateLogBuilder = new();
    private CancellationTokenSource? _developmentUpdateCancellation;
    private int _developmentUpdateLogFlushScheduled;
    private bool _subscribed;

    // 变量列表分批渲染用的状态：_filteredVariables 是筛选后的完整结果，
    // 按批追加进 Variables；筛选条件变化时用 _variableRenderGeneration 作废旧批次。
    private List<ConfigVariableItem> _filteredVariables = [];
    private int _variableRenderIndex;
    private int _variableRenderGeneration;

    public ConfigDebugSettingsViewModel(SettingsService settingsService,
        ILogger<ConfigDebugSettingsViewModel> logger,
        DevelopmentUpdateService developmentUpdateService,
        NativeDependencyService nativeDependencyService)
    {
        _settingsService = settingsService;
        _logger = logger;
        DevelopmentUpdateService = developmentUpdateService;
        NativeDependencyService = nativeDependencyService;

        // 页面切换后 ViewModel 会重建，从服务里恢复当前状态。
        DevelopmentUpdateStatus = developmentUpdateService.LastMessage;
        IsDevelopmentUpdateRunning = developmentUpdateService.IsRunning && !developmentUpdateService.IsPublishing;
        IsPublishRunning = developmentUpdateService.IsPublishing;
        NativeDependencyStatus = nativeDependencyService.LastReport is { } lastReport
            ? lastReport.IsHealthy
                ? $"上次检查通过，共检查 {lastReport.ScannedFileCount} 个原生库。"
                : $"上次检查发现 {lastReport.Issues.Count} 项问题。"
            : "尚未检查。";

        // 页面不再在进入时自动跑 git ls-remote（会卡 UI）。这里用当前配置的分支做
        // 轻量初始化，保证下拉框始终有一个可选项；完整分支列表由用户点「刷新」获取。
        var configuredBranch = settingsService.Settings.DevelopmentUpdateBranch;
        RemoteBranches = string.IsNullOrWhiteSpace(configuredBranch)
            ? Array.Empty<string>()
            : [configuredBranch];

        BuildVariables();
        EnsureSubscribed();
        ApplyFilter();
    }

    public SettingsService SettingsService => _settingsService;

    public DevelopmentUpdateService DevelopmentUpdateService { get; }

    public NativeDependencyService NativeDependencyService { get; }

    public string[] TypeFilters { get; } =
        [AllTypesFilter, "布尔", "数字", "文本", "日期时间", "颜色", "枚举", "其他"];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedTypeFilter = AllTypesFilter;

    [ObservableProperty]
    private bool _showReadOnly = true;

    // 用 ObservableCollection 支持增量追加：首屏先渲染少量项，其余分批加载，
    // 避免一次性实例化 226 套复杂编辑器控件把 UI 线程卡住。
    [ObservableProperty]
    private ObservableCollection<ConfigVariableItem> _variables = [];

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isDevelopmentUpdateRunning;

    [ObservableProperty]
    private bool _isPublishRunning;

    /// <summary>
    /// 更新编译或打包任一正在进行。用于驱动进度条与取消按钮。
    /// </summary>
    public bool IsAnyBuildRunning => IsDevelopmentUpdateRunning || IsPublishRunning;

    partial void OnIsDevelopmentUpdateRunningChanged(bool value) =>
        OnPropertyChanged(nameof(IsAnyBuildRunning));

    partial void OnIsPublishRunningChanged(bool value) =>
        OnPropertyChanged(nameof(IsAnyBuildRunning));

    [ObservableProperty]
    private string _developmentUpdateStatus = "尚未开始。";

    [ObservableProperty]
    private string _developmentUpdateLog = "";

    [ObservableProperty]
    private bool _hasDevelopmentUpdateLog;

    [ObservableProperty]
    private bool _isNativeDependencyBusy;

    [ObservableProperty]
    private string _nativeDependencyStatus = "尚未检查。";

    [ObservableProperty]
    private IReadOnlyList<NativeDependencyIssue> _nativeDependencyIssues =
        Array.Empty<NativeDependencyIssue>();

    [ObservableProperty]
    private bool _hasNativeDependencyIssues;

    [ObservableProperty]
    private IReadOnlyList<string> _remoteBranches = Array.Empty<string>();

    [ObservableProperty]
    private bool _isRemoteBranchesLoading;

    public Settings DefaultSettings => _defaultSettings;

    /// <summary>
    /// 探测远程分支供下拉框选择。只查询一次仓库地址，不碰本地 remote。
    /// </summary>
    public async Task RefreshRemoteBranchesAsync()
    {
        if (IsRemoteBranchesLoading)
        {
            return;
        }

        IsRemoteBranchesLoading = true;
        try
        {
            var branches = await DevelopmentUpdateService.GetRemoteBranchesAsync(CancellationToken.None);
            RemoteBranches = branches.Count > 0
                ? branches
                : [SettingsService.Settings.DevelopmentUpdateBranch];

            // 选中值要在列表里；否则保留当前值让用户可以手动输入分支名。
            if (!RemoteBranches.Contains(SettingsService.Settings.DevelopmentUpdateBranch))
            {
                OnPropertyChanged(nameof(SettingsService));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "探测远程分支失败。");
            RemoteBranches = [SettingsService.Settings.DevelopmentUpdateBranch];
        }
        finally
        {
            IsRemoteBranchesLoading = false;
        }
    }

    public async Task StartDevelopmentUpdateAsync()
    {
        if (IsAnyBuildRunning)
        {
            return;
        }

        IsDevelopmentUpdateRunning = true;
        _developmentUpdateLogBuilder.Clear();
        DevelopmentUpdateLog = "";
        HasDevelopmentUpdateLog = false;
        DevelopmentUpdateStatus = "正在准备…";
        _developmentUpdateCancellation = new CancellationTokenSource();
        var progress = new Progress<DevelopmentUpdateProgress>(OnDevelopmentUpdateProgress);

        try
        {
            await DevelopmentUpdateService.RunAsync(progress, _developmentUpdateCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            DevelopmentUpdateStatus = "已取消。";
        }
        catch (Exception ex)
        {
            DevelopmentUpdateStatus = $"更新失败：{ex.Message}";
            AppendDevelopmentUpdateLog(ex.ToString());
        }
        finally
        {
            IsDevelopmentUpdateRunning = false;
            _developmentUpdateCancellation?.Dispose();
            _developmentUpdateCancellation = null;
            FlushDevelopmentUpdateLog();
        }
    }

    /// <summary>
    /// 打包编译产物：环境与拉取流程同“更新并编译”，最后改为 dotnet publish 打包并生成 out.zip。
    /// 日志、状态、取消令牌与更新流程共用同一套展示。
    /// </summary>
    public async Task StartPublishAsync()
    {
        if (IsAnyBuildRunning)
        {
            return;
        }

        IsPublishRunning = true;
        _developmentUpdateLogBuilder.Clear();
        DevelopmentUpdateLog = "";
        HasDevelopmentUpdateLog = false;
        DevelopmentUpdateStatus = "正在准备打包…";
        _developmentUpdateCancellation = new CancellationTokenSource();
        var progress = new Progress<DevelopmentUpdateProgress>(OnDevelopmentUpdateProgress);

        try
        {
            await DevelopmentUpdateService.RunPublishAsync(progress, _developmentUpdateCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            DevelopmentUpdateStatus = "已取消。";
        }
        catch (Exception ex)
        {
            DevelopmentUpdateStatus = $"打包失败：{ex.Message}";
            AppendDevelopmentUpdateLog(ex.ToString());
        }
        finally
        {
            IsPublishRunning = false;
            _developmentUpdateCancellation?.Dispose();
            _developmentUpdateCancellation = null;
            FlushDevelopmentUpdateLog();
        }
    }

    public void CancelDevelopmentUpdate()
    {
        _developmentUpdateCancellation?.Cancel();
    }

    /// <summary>
    /// 扫描 runtimes 目录下的原生库，检查它们依赖的运行库是否齐全。
    /// </summary>
    public async Task CheckNativeDependenciesAsync()
    {
        if (IsNativeDependencyBusy)
        {
            return;
        }

        IsNativeDependencyBusy = true;
        NativeDependencyStatus = "正在检查原生库依赖…";
        try
        {
            var report = await NativeDependencyService.ProbeAsync();
            ApplyNativeDependencyReport(report);
        }
        catch (Exception ex)
        {
            NativeDependencyStatus = $"检查失败：{ex.Message}";
            _logger.LogError(ex, "检查原生库依赖失败。");
        }
        finally
        {
            IsNativeDependencyBusy = false;
        }
    }

    /// <summary>
    /// 检查并修复原生库依赖：缺失的 VC++ 运行库从微软下载安装，缺失的 NuGet 原生库从 nuget.org 还原。
    /// </summary>
    public async Task RepairNativeDependenciesAsync()
    {
        if (IsNativeDependencyBusy)
        {
            return;
        }

        IsNativeDependencyBusy = true;
        NativeDependencyStatus = "正在检查原生库依赖…";
        try
        {
            var report = await NativeDependencyService.ProbeAsync();
            if (report.IsHealthy)
            {
                ApplyNativeDependencyReport(report);
                return;
            }

            var progress = new Progress<string>(message => NativeDependencyStatus = message);
            var result = await NativeDependencyService.RepairAsync(report, progress);
            ApplyNativeDependencyReport(new NativeDependencyReport(result.RemainingIssues,
                report.ScannedFileCount, report.BaseDirectory));

            var summary = new List<string>();
            if (result.RestoredFileCount > 0)
            {
                summary.Add($"已还原 {result.RestoredFileCount} 个原生库");
            }

            if (result.VisualCppRuntimeInstalled)
            {
                summary.Add("已安装 Microsoft Visual C++ 运行库");
            }

            if (!string.IsNullOrEmpty(result.Failure))
            {
                summary.Add(result.Failure);
            }

            // 系统组件缺失（如 d3dcompiler_47.dll）不属于应用能自动修复的范围，
            // 只要可自动处理的问题都消失了，就应该告诉用户修复是成功的。
            var remainingFixable = result.RemainingIssues.Count(x =>
                x.Kind == NativeDependencyKind.VisualCppRuntime ||
                (x.Kind == NativeDependencyKind.ApplicationNative &&
                 !string.IsNullOrEmpty(x.PackageId)));
            if (remainingFixable == 0 && result.Failure == null)
            {
                summary.Add(result.Succeeded ? "全部依赖已就绪" : "可自动修复的依赖已全部处理");
            }
            else if (remainingFixable > 0)
            {
                summary.Add($"仍有 {remainingFixable} 项可自动修复的问题未解决");
            }

            NativeDependencyStatus = summary.Count > 0
                ? string.Join("；", summary) + "。"
                : "修复完成。";
        }
        catch (Exception ex)
        {
            NativeDependencyStatus = $"修复失败：{ex.Message}";
            _logger.LogError(ex, "修复原生库依赖失败。");
        }
        finally
        {
            IsNativeDependencyBusy = false;
        }
    }

    private void ApplyNativeDependencyReport(NativeDependencyReport report)
    {
        NativeDependencyIssues = report.Issues;
        HasNativeDependencyIssues = report.Issues.Count > 0;
        NativeDependencyStatus = report.IsHealthy
            ? $"检查通过，共检查 {report.ScannedFileCount} 个原生库。"
            : $"发现 {report.Issues.Count} 项问题，共检查 {report.ScannedFileCount} 个原生库。";
    }

    private void OnDevelopmentUpdateProgress(DevelopmentUpdateProgress progress)
    {
        if (!string.IsNullOrEmpty(progress.Message))
        {
            DevelopmentUpdateStatus = progress.Message;
        }

        if (!string.IsNullOrEmpty(progress.Detail))
        {
            AppendDevelopmentUpdateLog(progress.Detail);
        }
    }

    private void AppendDevelopmentUpdateLog(string line)
    {
        _developmentUpdateLogBuilder.AppendLine(line);
        if (_developmentUpdateLogBuilder.Length > MaxDevelopmentUpdateLogLength)
        {
            var text = _developmentUpdateLogBuilder.ToString();
            var cut = text.Length - MaxDevelopmentUpdateLogLength / 2;
            var lineBreak = text.IndexOf('\n', cut);
            _developmentUpdateLogBuilder.Clear()
                .Append(lineBreak >= 0 ? text[(lineBreak + 1)..] : text[cut..]);
        }

        ScheduleDevelopmentUpdateLogFlush();
    }

    /// <summary>
    /// 编译输出是逐行推送的，每一行都刷新一次文本控件会让界面明显卡顿。
    /// 这里合并成约 8 帧一次的刷新频率。
    /// </summary>
    private void ScheduleDevelopmentUpdateLogFlush()
    {
        if (Interlocked.Exchange(ref _developmentUpdateLogFlushScheduled, 1) == 1)
        {
            return;
        }

        DispatcherTimer.RunOnce(() =>
        {
            Interlocked.Exchange(ref _developmentUpdateLogFlushScheduled, 0);
            FlushDevelopmentUpdateLog();
        }, TimeSpan.FromMilliseconds(120));
    }

    private void FlushDevelopmentUpdateLog()
    {
        DevelopmentUpdateLog = _developmentUpdateLogBuilder.ToString();
        HasDevelopmentUpdateLog = DevelopmentUpdateLog.Length > 0;
    }

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
        StatusText = $"共 {_allVariables.Count} 个变量，当前显示 {list.Count} 个。";

        // 分批渲染：先把完整结果缓存下来，本次只同步放入第一批（首屏可见），
        // 余下的通过 UI 线程的分批回调逐批追加。这样进页面时只渲染几十项，
        // 不会因一次性创建 226 套编辑器控件而卡顿。
        _filteredVariables = list;
        var generation = ++_variableRenderGeneration;
        Variables.Clear();
        _variableRenderIndex = 0;
        if (list.Count == 0)
        {
            return;
        }

        // 首屏同步渲染第一批，保证进页面立刻有内容；其余分批异步追加。
        var firstBatchCount = Math.Min(VariableRenderBatchSize, list.Count);
        for (var i = 0; i < firstBatchCount; i++)
        {
            Variables.Add(list[i]);
        }
        _variableRenderIndex = firstBatchCount;

        ScheduleNextVariableBatch(generation);
    }

    /// <summary>
    /// 若还有未渲染的变量，投递下一批到 UI 线程。generation 用于作废旧批次，
    /// 供首次筛选与「缓存页面复用后续跑」两条路径共用。
    /// </summary>
    private void ScheduleNextVariableBatch(int generation)
    {
        if (_variableRenderIndex < _filteredVariables.Count)
        {
            Dispatcher.UIThread.Post(() => AppendNextVariableBatch(generation));
        }
    }

    /// <summary>
    /// 追加下一批变量。每批只处理 <see cref="VariableRenderBatchSize"/> 项，处理完继续投递
    /// 下一批，让 UI 线程在批次之间有机会绘制，避免长时间占用导致页面卡住。
    /// generation 变了说明筛选条件已更新，当前这批过期，直接停止。
    /// </summary>
    private void AppendNextVariableBatch(int generation)
    {
        if (generation != _variableRenderGeneration || _variableRenderIndex >= _filteredVariables.Count)
        {
            return;
        }

        var end = Math.Min(_variableRenderIndex + VariableRenderBatchSize, _filteredVariables.Count);
        for (var i = _variableRenderIndex; i < end; i++)
        {
            Variables.Add(_filteredVariables[i]);
        }
        _variableRenderIndex = end;
        ScheduleNextVariableBatch(generation);
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
    /// 订阅配置变更。页面被缓存后重新显示时需要重新订阅，并续跑可能被打断的分批渲染。
    /// </summary>
    public void EnsureSubscribed()
    {
        // 页面缓存复用后，OnUnloaded 里的 Dispose() 递增了 generation，会让尚未执行的
        // 分批回调作废。若当时还有变量没渲染完，这里必须重新投递一批从断点继续，
        // 否则切回来时列表会永远停在首批的几十项。递增 generation 同时让任何残留
        // 的旧批次失效，避免与续跑的批次重复添加。
        if (_variableRenderIndex < _filteredVariables.Count)
        {
            ScheduleNextVariableBatch(++_variableRenderGeneration);
        }

        if (_subscribed) return;
        _settingsService.Settings.PropertyChanged += SettingsOnPropertyChanged;
        _subscribed = true;
    }

    public void Dispose()
    {
        // 让尚未执行的分批渲染回调失效，避免页面销毁后还往集合里加项。
        _variableRenderGeneration++;
        if (!_subscribed) return;
        _settingsService.Settings.PropertyChanged -= SettingsOnPropertyChanged;
        _subscribed = false;
    }
}
