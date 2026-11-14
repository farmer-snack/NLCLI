using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Models;
using ClassIsland.Models.Automation.Triggers;
using Microsoft.Extensions.Logging;
using TimeCrontab;

namespace ClassIsland.Services.Automation.Triggers;

[TriggerInfo("classisland.cron", "cron", "\ue125")]
public class CronTrigger : TriggerBase<CronTriggerSettings>
{
    private Crontab? _crontab;

    private CancellationTokenSource? _stopCancellationTokenSource;

    /// <summary>
    /// 串行化「取消旧 worker + 建新 worker」。设置变更与全局时间偏移变更可能来自不同线程，
    /// 靠 tsc 的读-取消-写三步裸奔会让两个线程各起一个 worker，前一个从此没人取消 ——
    /// 结果就是同一时刻触发两次。
    /// </summary>
    private readonly object _reloadLock = new();

    ILogger<CronTrigger> Logger { get; } = App.GetService<ILogger<CronTrigger>>();
    IExactTimeService ExactTimeService { get; } = App.GetService<IExactTimeService>();
    SettingsService SettingsService { get; } = App.GetService<SettingsService>();

    /// <summary>等待时每次最多睡这么久，便于时间偏移调整后尽快按新时间重算。</summary>
    private static readonly TimeSpan MaxSleepInterval = TimeSpan.FromSeconds(30);

    public override void Loaded()
    {
        // 触发器可能因为切换配置方案/重载自动化被重复 Loaded。
        // 不先去重的话，每加载一次就多一组订阅，一次设置变更会把 LoadCron 调用 N 次，
        // 每次都要取消并重建 worker，极端情况下还会同时存在多个 worker。
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
        SettingsService.Settings.PropertyChanged -= GlobalSettingsOnPropertyChanged;
        Settings.PropertyChanged += SettingsOnPropertyChanged;
        SettingsService.Settings.PropertyChanged += GlobalSettingsOnPropertyChanged;
        LoadCron();
    }

    private async Task CronWorker(Crontab crontab, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = ExactTimeService.GetCurrentLocalDateTime();
                var next = crontab.GetNextOccurrence(now);
                var sleep = next - now;
                if (sleep < TimeSpan.FromMilliseconds(50))
                {
                    sleep = TimeSpan.FromMilliseconds(50);
                }
                else if (sleep > MaxSleepInterval)
                {
                    sleep = MaxSleepInterval;
                }

                await Task.Delay(sleep, token);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                // 分段睡眠后要重新确认是否真的到点：只有当前时间越过算出的触发时刻才触发，
                // 这样既不会提前触发，触发之后 now 也已经大于 next，不会重复触发。
                if (ExactTimeService.GetCurrentLocalDateTime() >= next)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // 从「判定到点」到 UI 线程真正执行之间有一个窗口：
                        // 这期间触发器可能已经被卸载（改表达式、关自动化、换配置方案）。
                        // 不在这里补一次检查，取消之后仍会补触发一次，表现为「改了规则还执行」。
                        if (!token.IsCancellationRequested)
                        {
                            Trigger();
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出：表达式变更、时间偏移变更或触发器被卸载。
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "cron 触发器运行失败。");
        }
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只认表达式本身。设置界面的 TextBox 每敲一个字符就会写回一次，
        // 中间态（"* *"、"* * *"）全都会被当成一次变更，反复取消并重建 worker，
        // 输入稍微慢一点，中途到点的那次就会被吞掉。
        if (e.PropertyName is not (nameof(CronTriggerSettings.CronExpression) or null))
        {
            return;
        }

        LoadCron();
    }

    /// <summary>
    /// 时间偏移变化后必须重建等待任务。旧任务手里还攥着按调整前算出的触发时刻，
    /// 不取消的话就会在「调整前的时间」再触发一次，表现为自动化执行两次。
    /// </summary>
    private void GlobalSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ClassIsland.Models.Settings.TimeOffsetSeconds)
            or nameof(ClassIsland.Models.Settings.DebugTimeOffsetSeconds)))
        {
            return;
        }

        // 开启调试时间加速后，DebugTimeOffsetSeconds 每 50ms 就被改写一次。
        // 每次都跟着重建 worker，旧 worker 活不过 50ms，永远睡不满一轮 ——
        // cron 规则一条都不会触发，日志还会以每秒 20 条的速度增长。
        // 加速期间时间本来就不可信，干脆沿用手上的 worker。
        if (SettingsService.Settings.DebugTimeSpeed != 0)
        {
            return;
        }

        LoadCron();
    }

    private void LoadCron()
    {
        lock (_reloadLock)
        {
            // 先取消旧任务，再换新的 token。旧任务持有的是自己的 token（局部变量），
            // 不像以前那样读取字段，所以字段被替换后不会「复活」继续跑。
            _stopCancellationTokenSource?.Cancel();
            _stopCancellationTokenSource?.Dispose();
            var cts = new CancellationTokenSource();
            _stopCancellationTokenSource = cts;

            if (string.IsNullOrWhiteSpace(Settings.CronExpression))
            {
                _crontab = null;
                Logger.LogWarning("cron 表达式为空，触发器不会生效。");
                return;
            }

            try
            {
                // Parse(null) 抛的是 NullReferenceException，不是 TimeCrontabException。
                // 只捕获后者的话，异常会一路冒到 Loaded() → 自动化配置加载 → 启动流程，
                // 而且每次启动都在同一处复现，用户只能安全模式删配置才能进程序。
                _crontab = Crontab.Parse(Settings.CronExpression);
            }
            catch (Exception ex)
            {
                _crontab = null;
                Logger.LogWarning(ex, "cron 表达式解析失败：{Expression}", Settings.CronExpression);
                return;
            }

            var crontab = _crontab;
            if (crontab == null)
            {
                return;
            }

            try
            {
                var now = ExactTimeService.GetCurrentLocalDateTime();
                Logger.LogInformation("cron 表达式解析成功，下次触发时间：{}", crontab.GetNextOccurrence(now).ToLongTimeString());
            }
            catch (Exception ex)
            {
                // 纯粹为了打日志而多算的一次，不能因为它把 LoadCron 带崩。
                // 表达式合法但算不出下一次触发（例如永不命中）时就是这条路。
                Logger.LogWarning(ex, "cron 表达式无法计算下次触发时间：{Expression}", Settings.CronExpression);
                return;
            }

            _ = Task.Run(() => CronWorker(crontab, cts.Token), cts.Token);
        }
    }

    public override void UnLoaded()
    {
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
        SettingsService.Settings.PropertyChanged -= GlobalSettingsOnPropertyChanged;
        lock (_reloadLock)
        {
            _stopCancellationTokenSource?.Cancel();
            _stopCancellationTokenSource?.Dispose();
            _stopCancellationTokenSource = null;
        }
    }
}
