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

    ILogger<CronTrigger> Logger { get; } = App.GetService<ILogger<CronTrigger>>();
    IExactTimeService ExactTimeService { get; } = App.GetService<IExactTimeService>();
    SettingsService SettingsService { get; } = App.GetService<SettingsService>();

    /// <summary>等待时每次最多睡这么久，便于时间偏移调整后尽快按新时间重算。</summary>
    private static readonly TimeSpan MaxSleepInterval = TimeSpan.FromSeconds(30);

    public override void Loaded()
    {
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
                    await Dispatcher.UIThread.InvokeAsync(() => Trigger());
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
        LoadCron();
    }

    /// <summary>
    /// 时间偏移变化后必须重建等待任务。旧任务手里还攥着按调整前算出的触发时刻，
    /// 不取消的话就会在「调整前的时间」再触发一次，表现为自动化执行两次。
    /// </summary>
    private void GlobalSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClassIsland.Models.Settings.TimeOffsetSeconds)
            or nameof(ClassIsland.Models.Settings.DebugTimeOffsetSeconds))
        {
            LoadCron();
        }
    }

    private void LoadCron()
    {
        // 先取消旧任务，再换新的 token。旧任务持有的是自己的 token（局部变量），
        // 不像以前那样读取字段，所以字段被替换后不会「复活」继续跑。
        _stopCancellationTokenSource?.Cancel();
        var cts = new CancellationTokenSource();
        _stopCancellationTokenSource = cts;

        try
        {
            _crontab = Crontab.Parse(Settings.CronExpression);
        }
        catch (TimeCrontabException ex)
        {
            _crontab = null;
            Logger.LogWarning(ex, "cron 表达式解析失败：");
            return;
        }

        var crontab = _crontab;
        if (crontab == null)
        {
            return;
        }

        var now = ExactTimeService.GetCurrentLocalDateTime();
        Logger.LogInformation("cron 表达式解析成功，下次触发时间：{}", crontab.GetNextOccurrence(now).ToLongTimeString());
        _ = Task.Run(() => CronWorker(crontab, cts.Token), cts.Token);
    }

    public override void UnLoaded()
    {
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
        SettingsService.Settings.PropertyChanged -= GlobalSettingsOnPropertyChanged;
        _stopCancellationTokenSource?.Cancel();
        _stopCancellationTokenSource = null;
    }
}
