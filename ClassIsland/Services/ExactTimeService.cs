using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Platforms.Abstraction;
using CommunityToolkit.Mvvm.ComponentModel;

using GuerrillaNtp;

using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

public class ExactTimeService : ObservableRecipient, IExactTimeService
{
    /// <summary>
    /// 备用 NTP 服务器。配置的服务器失败后按顺序尝试，避免单点故障。
    /// </summary>
    private static readonly string[] FallbackServers =
    [
        "ntp.aliyun.com",
        "time.windows.com",
        "pool.ntp.org"
    ];

    /// <summary>同步成功且偏差很小时的间隔。NTP 客户端没必要分钟级轮询。</summary>
    private static readonly TimeSpan NormalInterval = TimeSpan.FromHours(6);

    /// <summary>偏差超过这个值时缩短间隔，尽快再校准一次。</summary>
    private static readonly TimeSpan DriftThreshold = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DriftInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(6);

    private string _syncStatusMessage = "时间尚未同步。";

    private DateTime PrevDateTime { get; set; } = DateTime.MinValue;

    private bool NeedWaiting { get; set; } = false;

    private SettingsService SettingsService { get; }

    private NtpClock NtpClock { get; set; } = NtpClock.LocalFallback;

    private DateTime LastTime { get; set; } = DateTime.Now;

    private bool WaitingForSystemTimeChanged { get; set; } = false;

    private DateTime LastSystemTime { get; set; } = DateTime.Now;

    private Stopwatch TimeGetStopwatch { get; } = Stopwatch.StartNew();

    private ILogger<ExactTimeService> Logger { get; }

    /// <summary>每分钟检查一次是否到点，真正的同步节奏由 <see cref="_nextSyncAt"/> 决定。</summary>
    private DispatcherTimer UpdateTimer { get; } = new DispatcherTimer()
    {
        Interval = TimeSpan.FromMinutes(1)
    };

    /// <summary>下次允许同步的时间点。自适应：成功 6 小时（偏差大时 1 小时），失败 1/5/15/60 分钟。</summary>
    private DateTime _nextSyncAt = DateTime.MinValue;

    private int _failureCount;

    /// <summary>0 = 空闲，1 = 正在同步。用 Interlocked 保证定时器和手动同步不会并发。</summary>
    private int _syncInProgress;

    private string _lastSyncServer = "";

    private DateTime _lastSyncAt = DateTime.MinValue;

    public string SyncStatusMessage
    {
        get => _syncStatusMessage;
        set
        {
            if (value == _syncStatusMessage) return;

            // 同步跑在线程池上，直接改属性会把 PropertyChanged 抛到非 UI 线程，
            // Avalonia 的绑定偶尔会因此不刷新（甚至抛跨线程异常），统一回到 UI 线程再赋值。
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => SyncStatusMessage = value);
                return;
            }

            _syncStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public ExactTimeService(SettingsService settingsService, ILogger<ExactTimeService> logger)
    {
        Logger = logger;
        SettingsService = settingsService;
        SettingsService.Settings.PropertyChanged += SettingsOnPropertyChanged;
        UpdateTimer.Tick += (_, _) =>
        {
            if (DateTime.Now >= _nextSyncAt)
            {
                _ = SyncAsync();
            }
        };
        // DispatcherTimer 属于 UI 线程，启动必须在构造函数所在线程完成，
        // 别塞进 Task.Run —— 从线程池调 Start() 在 Avalonia 里会抛跨线程异常。
        UpdateTimerStatus();
        Task.Run(() =>
        {
            PlatformServices.SystemEventsService.TimeChanged += SystemEventsOnTimeChanged;
            AppBase.Current.AppStopping += (_, _) => PlatformServices.SystemEventsService.TimeChanged -= SystemEventsOnTimeChanged;
        });

        if (SettingsService.Settings.IsTimeAutoAdjustEnabled)
        {
            SettingsService.Settings.TimeOffsetSeconds += SettingsService.Settings.TimeAutoAdjustSeconds *
                                                          Math.Floor((DateTime.Today - SettingsService.Settings
                                                              .LastTimeAdjustDateTime.Date).TotalDays);
            SettingsService.Settings.TimeOffsetSeconds = Math.Round(SettingsService.Settings.TimeOffsetSeconds, 3);
        }
        SettingsService.Settings.LastTimeAdjustDateTime = DateTime.Today;
    }

    private void SystemEventsOnTimeChanged(object? sender, EventArgs e)
    {
        WaitingForSystemTimeChanged = true;
        Logger.LogInformation("系统时间修改，正在重新同步时间");
        // 系统时间刚被改过，立刻校一次，不要等下一个周期。
        _nextSyncAt = DateTime.Now;
        _ = SyncAsync();
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsService.Settings.ExactTimeServer):
                Logger.LogInformation("时间服务器改为 {}，下次同步生效", SettingsService.Settings.ExactTimeServer);
                _nextSyncAt = DateTime.Now;
                break;
            case nameof(SettingsService.Settings.IsExactTimeEnabled):
                UpdateTimerStatus();
                break;
        }
    }

    private void UpdateTimerStatus()
    {
        if (SettingsService.Settings.IsExactTimeEnabled)
        {
            _nextSyncAt = DateTime.Now;
            UpdateTimer.Start();
            _ = SyncAsync();
        }
        else
        {
            UpdateTimer.Stop();
            WaitingForSystemTimeChanged = false;
            SyncStatusMessage = "已关闭时间同步，正在使用系统时间。";
        }
    }

    private void ScheduleNext(TimeSpan delay)
    {
        _nextSyncAt = DateTime.Now + delay;
    }

    private IReadOnlyList<string> BuildServerList()
    {
        var servers = new List<string>();
        void Add(string? server)
        {
            var value = server?.Trim();
            if (!string.IsNullOrEmpty(value) && !servers.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                servers.Add(value);
            }
        }

        Add(SettingsService.Settings.ExactTimeServer);
        foreach (var server in FallbackServers)
        {
            Add(server);
        }

        return servers;
    }

    public void Sync()
    {
        _ = SyncAsync();
    }

    private async Task SyncAsync()
    {
        if (!SettingsService.Settings.IsExactTimeEnabled)
        {
            WaitingForSystemTimeChanged = false;
            return;
        }

        if (Interlocked.CompareExchange(ref _syncInProgress, 1, 0) != 0)
        {
            return;
        }

        var previous = NtpClock.Now.LocalDateTime;
        try
        {
            SyncStatusMessage = "正在同步时间……";
            Exception? lastError = null;

            foreach (var server in BuildServerList())
            {
                try
                {
                    Logger.LogInformation("正在从 {} 同步时间", server);
                    var client = new NtpClient(server);
                    using var cts = new CancellationTokenSource(QueryTimeout);
                    var clock = await client.QueryAsync(cts.Token).ConfigureAwait(false);

                    NtpClock = clock;
                    _lastSyncServer = server;
                    _lastSyncAt = DateTime.Now;
                    _failureCount = 0;

                    var drift = clock.Now.LocalDateTime - DateTime.Now;
                    var interval = Math.Abs(drift.TotalSeconds) > DriftThreshold.TotalSeconds
                        ? DriftInterval
                        : NormalInterval;
                    ScheduleNext(interval);

                    if (Math.Abs((clock.Now.LocalDateTime - previous).TotalSeconds) < 30 &&
                        clock.Now.LocalDateTime < previous)
                    {
                        NeedWaiting = true;
                        PrevDateTime = previous;
                    }

                    Logger.LogInformation("成功从 {} 同步时间，偏差 {} ms，下次 {}",
                        server, drift.TotalMilliseconds, _nextSyncAt);
                    SyncStatusMessage =
                        $"上次同步 {_lastSyncAt:HH:mm}（{server}），与系统时间偏差 {drift.TotalMilliseconds:0} ms，下次 {_nextSyncAt:HH:mm}";
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Logger.LogWarning(ex, "从 {} 同步时间失败，尝试下一个服务器", server);
                }
            }

            _failureCount++;
            var minutes = _failureCount switch
            {
                1 => 1,
                2 => 5,
                3 => 15,
                _ => 60
            };
            ScheduleNext(TimeSpan.FromMinutes(minutes));
            Logger.LogError(lastError, "同步时间失败（已尝试 {} 个服务器）", BuildServerList().Count);
            SyncStatusMessage = $"同步时间失败：{lastError?.Message}，{minutes} 分钟后重试";
        }
        finally
        {
            LastSystemTime = DateTime.Now;
            TimeGetStopwatch.Restart();
            WaitingForSystemTimeChanged = false;
            Interlocked.Exchange(ref _syncInProgress, 0);
        }
    }

    public DateTime GetCurrentLocalDateTime()
    {
        var systemTime = DateTime.Now;
        if (SettingsService.Settings.IsExactTimeEnabled)
        {
            if (Math.Abs((LastSystemTime - systemTime).TotalMilliseconds - TimeGetStopwatch.ElapsedMilliseconds) > 30_000.0 && !WaitingForSystemTimeChanged)
            {
                WaitingForSystemTimeChanged = true;
                Logger.LogInformation("检测到系统时间突变，已暂停返回的时间并重新同步时间");
                _nextSyncAt = DateTime.Now;
                _ = SyncAsync();
            }
            if (WaitingForSystemTimeChanged)
            {
                return LastTime;
            }
        }

        LastSystemTime = systemTime;
        TimeGetStopwatch.Restart();
        var now = SettingsService.Settings.IsExactTimeEnabled ? NtpClock.Now.LocalDateTime : systemTime;
        DateTime baseTime;
        if (now < PrevDateTime && NeedWaiting)
        {
            baseTime = PrevDateTime;
        }
        else
        {
            if (NeedWaiting)
                NeedWaiting = false;
            baseTime = now;
        }
        return LastTime = baseTime + TimeSpan.FromSeconds(SettingsService.Settings.TimeOffsetSeconds +
                                                       SettingsService.Settings.DebugTimeOffsetSeconds);
    }
}
