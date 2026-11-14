using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Enums.Notification;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Services.Registry;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Interfaces;
using ClassIsland.Shared.Models.Profile;
using ClassIsland.Models;
using ClassIsland.Models.Notification;
using ClassIsland.Shared.Models.Notification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace ClassIsland.Services;

/// <summary>
/// 提醒主机服务。
/// </summary>
public class NotificationHostService(SettingsService settingsService, ILogger<NotificationHostService> logger, INotificationWorkerService notificationWorkerService)
    : IHostedService, INotifyPropertyChanged, INotificationHostService
{
    private SettingsService SettingsService { get; } = settingsService;
    private ILogger<NotificationHostService> Logger { get; } = logger;
    private INotificationWorkerService NotificationWorkerService { get; } = notificationWorkerService;
    private Settings Settings => SettingsService.Settings;

    public PriorityQueue<NotificationRequest, NotificationPriority> RequestQueue { get; } = new();

    private int _queueIndex = 0;
    private bool _isNotificationsPlaying = false;

    /// <summary>
    /// 等待票据取消收尾的最长时间。正常情况下提醒音效在几十毫秒内就能停下，
    /// 这里只是兜底：超过这个时间还没收尾，就不再指望 worker 的完成信号。
    /// </summary>
    private static readonly TimeSpan CancellationSettledTimeout = TimeSpan.FromSeconds(3);

    public ObservableCollection<NotificationProviderRegisterInfo> NotificationProviders { get; } = new();

    private List<NotificationConsumerRegisterInfo> RegisteredConsumers { get; } = [];

    private HashSet<NotificationRequest> PlayingNotifications { get; } = [];

    private HashSet<NotificationRequest> PoppedRequests { get; } = [];

    private HashSet<NotificationRequest> EnqueuedRequests { get; } = [];

    private List<NotificationPlayingTicket> PlayingTickets { get; } = [];

    /// <summary>
    /// 提醒队列的硬上限。正常情况下队列长度只有个位数；只有消费者卡死、
    /// 或者上游以极高频率推送（例如 cron 触发器被反复重建）时才会无限增长。
    /// 不设上限的话，每一条积压的提醒都会连同它的 CancellationTokenSource、
    /// 链式请求和播放票据一起留在内存里，最终表现为内存持续上涨直至 OOM。
    /// </summary>
    private const int MaxQueuedRequests = 256;

    public NotificationRequest? CurrentRequest { get; set; }

    public NotificationRequest GetRequest()
    {
        if (!RequestQueue.TryDequeue(out var request, out _) || request == null)
        {
            // 队列空时直接 Dequeue 会抛 InvalidOperationException，而接口签名不允许返回 null。
            // 返回一个空请求，调用方通过 State 能看出它并没有真正被调度。
            return new NotificationRequest();
        }
        CurrentRequest = request;
        return request;
    }

    /// <summary>
    /// 注册提醒服务。
    /// </summary>
    /// <param name="provider">要注册的服务实例。</param>
    /// <example>
    /// <code>
    /// NotificationHostService.RegisterNotificationProvider(this);
    /// </code>
    /// </example>
    public void RegisterNotificationProvider(INotificationProvider provider)
    {
        Logger.LogInformation("注册提醒提供方：{}（{}）", provider.ProviderGuid, provider.Name);
        if (!Settings.NotificationProvidersPriority.Contains(provider.ProviderGuid.ToString()))
        {
            Settings.NotificationProvidersPriority.Add(provider.ProviderGuid.ToString());
        }
        if (!Settings.NotificationProvidersSettings.ContainsKey(provider.ProviderGuid.ToString()))
        {
            Settings.NotificationProvidersSettings.Add(provider.ProviderGuid.ToString(), null);
        }
        if (!Settings.NotificationProvidersEnableStates.ContainsKey(provider.ProviderGuid.ToString()))
        {
            Settings.NotificationProvidersEnableStates.Add(provider.ProviderGuid.ToString(), true);
        }
        if (!Settings.NotificationProvidersNotifySettings.ContainsKey(provider.ProviderGuid.ToString()))
        {
            Settings.NotificationProvidersNotifySettings.Add(provider.ProviderGuid.ToString(), new());
        }

        NotificationProviders.Add(new NotificationProviderRegisterInfo(provider)
        {
            ProviderSettings = Settings.NotificationProvidersNotifySettings[provider.ProviderGuid.ToString()]
        });

        if (provider is not NotificationProviderBase providerBase)
        {
            return;
        }

        var providerInfo = NotificationProviderRegistryService.RegisteredProviders.First(x => x.Guid == provider.ProviderGuid);
        foreach (var channelInfo in providerInfo.RegisteredChannels)
        {
            providerBase.Channels[channelInfo.Guid] = new NotificationChannel(providerBase, providerInfo, channelInfo);
        }
    }

    private void UpdateNotificationPlayingState()
    {
        IsNotificationsPlaying = PlayingNotifications.Count > 0;
    }

    private void FinishNotificationPlaying(NotificationRequest request)
    {
        Logger.LogTrace("提醒 #{} 已播放完成", request.GetHashCode());
        PlayingNotifications.Remove(request);
        UpdateNotificationPlayingState();
    }
    
    private void SetupNotificationRequest(NotificationRequest request, Guid providerGuid, Guid channelGuid)
    {
        if (request.NotificationSetupCompleted)
        {
            return;
        }
        request.NotificationSourceGuid = providerGuid;
        request.NotificationSource = (from i in NotificationProviders where i.ProviderGuid == providerGuid select i)
            .FirstOrDefault();
        request.ProviderSettings = request.NotificationSource?.ProviderSettings ?? request.ProviderSettings;
        if (request.InitialQueueIndex == -1)
        {
            request.InitialQueueIndex = Interlocked.Increment(ref _queueIndex);
        }
        
        if (channelGuid != Guid.Empty && request.ChannelId == Guid.Empty)
        {
            request.ChannelId = channelGuid;
        }

        channelGuid = request.ChannelId;

        var channel =
            request.NotificationSource?.NotificationChannels.FirstOrDefault(x => x.ProviderGuid == channelGuid);
        request.ChannelSettings = channel?.ProviderSettings;
        request.State = NotificationState.Queued;
        // 播放完成（而不是被取消）时要把它从「正在播放」里摘掉。这里注册而不是在
        // ShowNotification 里注册：同一条请求可能被链式推送、退回重排、fallback 入队
        // 等多条路径反复经过，注册多次会让回调在同一个令牌上越挂越多。
        request.CompletedToken.Register(() => FinishNotificationPlaying(request));
        request.NotificationSetupCompleted = true;
    }

    public void ShowNotification(NotificationRequest request, Guid providerGuid, Guid channelGuid, bool pushNotifications, bool isPlayed)
    {
        RunOnUiThread(() => ShowNotificationCore(request, providerGuid, channelGuid, pushNotifications, isPlayed));
    }

    private void ShowNotificationCore(NotificationRequest request, Guid providerGuid, Guid channelGuid, bool pushNotifications, bool isPlayed)
    {
        if (!Settings.IsNotificationEnabled)
        {
            request.CompletedTokenSource.Cancel();
            return;
        }
        UpdateNotificationPlayingState();
        if (!isPlayed)
        {
            SetupNotificationRequest(request, providerGuid, channelGuid);
        }
        // 必须先确认有消费者接单，再创建票据。CreateTicket 会把票据登记进 PlayingTickets、
        // 注册取消回调并创建 CTS；没有消费者时这张票永远不会被播放，
        // 之后 PopRequests 又会为同一条请求再造一张票，形成「一请求两票据」的泄漏。
        if (pushNotifications && HasAvailableConsumer())
        {
            var ticket = CreateTicket(request);
            if (PushNotificationRequests([ticket]))
            {
                return;
            }
            CancelOrphanTicket(ticket);
        }
        // 如果没有消费者接收推送的提醒，则会加入提醒队列。
        EnqueueNotification(request, isPlayed);
    }

    private void EnqueueNotification(NotificationRequest request, bool isPlayed)
    {
        if (!EnqueuedRequests.Add(request))
        {
            return;
        }

        if (RequestQueue.Count >= MaxQueuedRequests)
        {
            // 丢弃新来的提醒而不是最旧的：最旧的那条往往正在被消费者处理或
            // 属于一条链的头，从中间抽走它会让链断掉。
            Logger.LogWarning("提醒队列已满（{Count}），丢弃提醒 {Request}", MaxQueuedRequests, request);
            EnqueuedRequests.Remove(request);
            request.CompletedTokenSource.Cancel();
            return;
        }

        RequestQueue.Enqueue(request,
            new NotificationPriority(
                Settings.NotificationProvidersPriority.IndexOf(request.NotificationSourceGuid.ToString()),
                request.InitialQueueIndex,
                request.IsPriorityOverride,
                isPlayed));
    }

    public async Task ShowNotificationAsync(NotificationRequest request, Guid providerGuid, Guid channelGuid)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowNotification(request, providerGuid, channelGuid, true, false);
        });
        await WaitForCompletionAsync(request.CompletedToken);
    }

    /// <summary>
    /// 异步等待提醒生命周期结束。取消时正常返回，不抛异常。
    /// </summary>
    /// <remarks>
    /// 不要用 <see cref="CancellationToken.WaitHandle"/> + <c>WaitOne()</c>：
    /// 那个属性会在每次访问时惰性分配一个 <see cref="ManualResetEvent"/> 内核句柄，
    /// 且永远不会被释放。提醒是长期高频触发的功能（每节课、每个课间各一次），
    /// 句柄会随着运行时长单调上涨。<see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// 是纯托管的等待，取消时立即返回。
    /// </remarks>
    private static async Task WaitForCompletionAsync(CancellationToken completionToken)
    {
        if (!completionToken.CanBeCanceled)
        {
            return;
        }
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, completionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 提醒完成（或被取消）时取消令牌，属于正常结束路径。
        }
    }

    public void ShowChainedNotifications(NotificationRequest[] requests, Guid providerGuid, Guid channelGuid)
    {
        RunOnUiThread(() => ShowChainedNotificationsCore(requests, providerGuid, channelGuid));
    }

    private void ShowChainedNotificationsCore(NotificationRequest[] requests, Guid providerGuid, Guid channelGuid)
    {
        if (requests.Length <= 0)
        {
            return;
        }
        if (!Settings.IsNotificationEnabled)
        {
            foreach (var request in requests)
            {
                request.CompletedTokenSource.Cancel();
            }
            return;
        }

        var rootCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(requests.Select(x => x.CancellationTokenSource.Token).ToArray());
        var rootCompletedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(requests.Select(x => x.CompletedTokenSource.Token).ToArray());
        rootCancellationTokenSource.Token.Register(() =>
        {
            foreach (var request in requests.Where(x => !x.CancellationToken.IsCancellationRequested))
            {
                request.CancellationTokenSource.Cancel();
            }
        });
        NotificationRequest? prevRequest = null;
        var head = requests[0];
        foreach (var request in requests)
        {
            request.RootCancellationTokenSource = rootCancellationTokenSource;
            request.RootCompletedTokenSource = rootCompletedTokenSource;
            request.ChainedHeadRequest = head;
            if (prevRequest != null)
            {
                prevRequest.ChainedNextRequest = request;
            }
            SetupNotificationRequest(request, providerGuid, channelGuid);
            prevRequest = request;
        }

        if (HasAvailableConsumer())
        {
            var tickets = requests.Select(CreateTicket).ToList();
            if (PushNotificationRequests(tickets))
            {
                return;
            }
            // 消费者在接单瞬间失效：票据已经建好但不会被任何人播放，必须取消掉，
            // 否则它们会一直留在 PlayingTickets 里，其 CTS 与取消回调也永不释放。
            foreach (var ticket in tickets)
            {
                CancelOrphanTicket(ticket);
            }
        }
        // 如果没有消费者接收推送的提醒，则会加入提醒队列。
        foreach (var request in requests)
        {
            ShowNotification(request, providerGuid, channelGuid, false, false);
        }
    }

    public async Task ShowChainedNotificationsAsync(NotificationRequest[] requests, Guid providerGuid, Guid channelGuid)
    {
        if (requests.Length <= 0)
        {
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowChainedNotifications(requests, providerGuid, channelGuid);
        });
        await WaitForCompletionAsync(requests.Last().CompletedToken);
    }

    public void RegisterNotificationChannel(NotificationChannel channel)
    {
        Logger.LogInformation("注册提醒渠道：{}（{}）", channel.ChannelInfo.Guid, channel.ChannelInfo.Name);
        if (!Settings.NotificationChannelsNotifySettings.ContainsKey(channel.ChannelInfo.Guid.ToString()))
        {
            Settings.NotificationChannelsNotifySettings.Add(channel.ChannelInfo.Guid.ToString(), new());
        }
        NotificationProviders.FirstOrDefault(x => x.ProviderGuid == channel.ChannelInfo.AssociatedProviderGuid)?.NotificationChannels.Add(new NotificationChannelRegisterInfo(channel)
        {
            ProviderSettings = Settings.NotificationChannelsNotifySettings[channel.ChannelInfo.Guid.ToString()]
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return new Task(()=>{});
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return new Task(()=>{});
    }

    /// <summary>
    /// 获取提醒服务实例。
    /// </summary>
    /// <typeparam name="T">提醒服务类型</typeparam>
    /// <param name="id">提醒服务id</param>
    /// <returns>对应提醒服务实例。若不存在，则返回null。</returns>
    public T GetNotificationProviderSettings<T>(Guid id) where T : class
    {
        Logger.LogInformation("获取提醒提供方设置：{}", id);
        var o = Settings.NotificationProvidersSettings[id.ToString()];
        var settings = o switch
        {
            JsonElement json => json.Deserialize<T>() ?? Activator.CreateInstance<T>(),
            T s => s,
            _ => Activator.CreateInstance<T>()
        };
        Settings.NotificationProvidersSettings[id.ToString()] = settings;
        return settings;
    }

    public void WriteNotificationProviderSettings<T>(Guid id, T settings)
    {
        Logger.LogInformation("写入提醒提供方设置：{}", id);
        Settings.NotificationProvidersSettings[id.ToString()] = settings;
    }

    public void CancelAllNotifications()
    {
        RunOnUiThread(CancelAllNotificationsCore);
    }

    private void CancelAllNotificationsCore()
    {
        while (RequestQueue.Count > 0)
        {
            var r = RequestQueue.Dequeue();
            // 只出队不清理登记表，EnqueuedRequests 会永久保留这些已经不在队列里的请求，
            // 既泄漏引用，也会让这些请求之后再也无法重新入队（Add 返回 false）。
            EnqueuedRequests.Remove(r);
            r.CompletedTokenSource.Cancel();
        }
        foreach (var request in PlayingNotifications.ToList())
        {
            // 先放行完成信号再取消播放：宿主看到 CompletedToken 已取消就不会把这条请求
            // 当成「被移交」重新入队 —— 用户点了「清除全部提醒」，不应该过一会儿又弹出来。
            request.CompletedTokenSource.Cancel();
            request.CancellationTokenSource.Cancel();
        }
    }

    /// <summary>
    /// 是否存在可以立刻接单的提醒消费者。
    /// </summary>
    private bool HasAvailableConsumer() =>
        RegisteredConsumers.Any(x => x.Consumer.AcceptsNotificationRequests && x.Consumer.QueuedNotificationCount <= 0);

    /// <summary>
    /// 取消一张「建好了但没能推送出去」的票据。
    /// </summary>
    /// <remarks>
    /// 这种票据不会被任何消费者播放，不取消就会一直留在 <c>PlayingTickets</c> 里，
    /// 它的 CTS、取消回调和链式令牌也永远不会释放。
    /// 取消回调里会看到请求还没被终结，从而把它重新入队 —— 这正是想要的：
    /// 请求退回队列，等下一次有消费者时再派发。
    /// </remarks>
    private void CancelOrphanTicket(NotificationPlayingTicket ticket)
    {
        try
        {
            ticket.Cancel();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "清理未能推送的提醒票据时发生错误");
        }
    }

    private bool PushNotificationRequests(List<NotificationPlayingTicket> requests)
    {
        Logger.LogTrace("开始推送提醒 ({})", requests.Count);
        if (requests.Count <= 0)
        {
            return false;
        }

        var consumer = RegisteredConsumers
            .FirstOrDefault(x => x.Consumer.AcceptsNotificationRequests && x.Consumer.QueuedNotificationCount <= 0);
        if (consumer != null)
        {
            Logger.LogTrace("将推送的提醒消费者：{}(#{})", consumer.Consumer, consumer.Consumer.GetHashCode());
            foreach (var request in requests)
            {
                PlayingNotifications.Add(request.Request);
                Logger.LogTrace("将推送提醒 {}", request);
            }
            UpdateNotificationPlayingState();
            try
            {
                consumer.Consumer.ReceiveNotifications(requests);
                return true;
            }
            catch (Exception ex)
            {
                // 消费者接单时抛异常（插件实现、界面已销毁……）。如果不回滚，
                // 这些请求会永远留在 PlayingNotifications 里，再也不会被重新派发。
                Logger.LogError(ex, "向提醒消费者推送提醒时发生错误");
                foreach (var request in requests)
                {
                    PlayingNotifications.Remove(request.Request);
                }
                UpdateNotificationPlayingState();
                return false;
            }
        }

        Logger.LogTrace("找不到接受提醒的提醒消费者");
        return false;
    }

    private List<NotificationPlayingTicket> PopRequests()
    {
        NotificationRequest? head;
        bool headPopped;
        do
        {
            if (RequestQueue.Count <= 0)
            {
                return [];
            }
            head = RequestQueue.Dequeue();
            EnqueuedRequests.Remove(head);
            if (head.ChainedHeadRequest is {} chainedHead)
            {
                if (chainedHead != head)
                {
                    PoppedRequests.Remove(head);
                }
                head = chainedHead;
            }
            // 这里必须是「只读」的判重。之前用的是 PoppedRequests.Remove(head)，
            // 而 do-while 会在同一轮里反复检查同一个链头：第一次 Remove 返回 true 并把记录删掉，
            // 第二次就返回 false，循环误判为「没弹过」，于是把已经投递过的整条链再投一遍。
            // 表现为链式提醒（如「预备铃 + 上课铃」）的链尾被重复播放。
            headPopped = PoppedRequests.Contains(head);
        } while (headPopped);

        List<NotificationRequest> requests = [];

        while (head != null)
        {
            var isTerminal = head.State is NotificationState.Cancelled or NotificationState.Completed;
            // 已经有活跃票据的请求不能再派发一次：hand-off 重排时链头会被重新弹出，
            // 此时链上其它成员可能还在播放中（State 仍是 Playing，躲不过状态过滤），
            // 不拦的话同一条提醒会被两个 processor 同时处理。
            var hasActiveTicket = PlayingTickets.Any(x => ReferenceEquals(x.Request, head));
            if (!isTerminal && !hasActiveTicket)
            {
                PoppedRequests.Add(head);
                requests.Add(head);
                Logger.LogTrace("添加 Head {}", head);
                PlayingNotifications.Add(head);
            }
            else if (isTerminal)
            {
                // 已经结束的链仍然要记成「弹过」，否则队列里残留的同链成员会让
                // 这条已经播完的链被反复取出、反复过滤。
                PoppedRequests.Add(head);
            }
            head = head.ChainedNextRequest;
        }
        
        UpdateNotificationPlayingState();
        return requests
            .Select(CreateTicket)
            .ToList();
    }

    private void PopRequestsToConsumers()
    {
        while (true)
        {
            if (!HasAvailableConsumer())
            {
                break;
            }
            var tickets = PopRequests();
            if (tickets.Count <= 0)
            {
                break;
            }
            if (PushNotificationRequests(tickets))
            {
                continue;
            }

            // 推送失败（消费者恰好在接单瞬间失效）：请求已经出队、票据也已经建好，
            // 直接退出会让这些提醒彻底消失。取消票据把它交回取消回调重新排队。
            foreach (var ticket in tickets)
            {
                try
                {
                    ticket.Cancel();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "回滚推送失败的提醒票据时发生错误");
                }
            }
            break;
        }
    }

    /// <summary>
    /// 派发队列中的提醒，并吞掉派发过程中的异常。
    /// </summary>
    /// <remarks>
    /// 这个方法经常从票据取消回调、后台线程的 async 续体里调用。一旦异常穿透到
    /// <c>async void</c> 或 <see cref="CancellationTokenSource.Cancel()"/> 的调用方，
    /// 会变成未观察异常（进程级崩溃），或者在取消链上把更重要的收尾步骤跳过。
    /// </remarks>
    private void TryPopRequestsToConsumers()
    {
        RunOnUiThread(() =>
        {
            try
            {
                PopRequestsToConsumers();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "派发提醒队列时发生错误");
            }
        });
    }

    public void RegisterNotificationConsumer(INotificationConsumer consumer, int priority)
    {
        RunOnUiThread(() =>
        {
            try
            {
                var registerInfo = new NotificationConsumerRegisterInfo(consumer, priority);

                for (var i = 0; i < RegisteredConsumers.Count; i++)
                {
                    if (RegisteredConsumers[i].Priority <= registerInfo.Priority)
                        continue;
                    RegisteredConsumers.Insert(i, registerInfo);
                    return;
                }

                RegisteredConsumers.Add(registerInfo);  // 当列表中什么都没有或者插入项的优先级比列表里所有元素都大时，插入到最后一项。
            }
            finally
            {
                TryPopRequestsToConsumers();
            }
        });
    }

    public void UnregisterNotificationConsumer(INotificationConsumer consumer)
    {
        RunOnUiThread(() =>
        {
            var registerInfo = RegisteredConsumers.FirstOrDefault(x => x.Consumer == consumer);
            if (registerInfo == null)
            {
                return;
            }

            RegisteredConsumers.Remove(registerInfo);
        });
    }

    public IList<NotificationPlayingTicket> PullNotificationRequests()
    {
        return PopRequests();
    }

    public bool IsNotificationsPlaying
    {
        get => _isNotificationsPlaying;
        set
        {
            if (value == _isNotificationsPlaying) return;
            _isNotificationsPlaying = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 在 UI 线程执行动作；已经从 UI 线程调用时同步执行，避免行为差异。
    /// </summary>
    /// <remarks>
    /// 提醒主机持有的队列、票据集合以及 <see cref="IsNotificationsPlaying"/> 都直接绑定到界面，
    /// 本质上只能在 UI 线程上操作。但 <see cref="ShowNotification"/> 是公开接口，
    /// 插件可以在任意线程调用；票据也可能被后台线程上的链式取消触发。
    /// 统一在这里收口，避免裸集合被跨线程并发修改。
    /// </remarks>
    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private NotificationPlayingTicket CreateTicket(NotificationRequest request)
    {
        var ticket = NotificationWorkerService.CreateTicket(request);
        PlayingTickets.Add(ticket);
        ticket.CancellationToken.Register(() =>
            RunOnUiThread(() => OnTicketCancelled(ticket, request)));
        request.CompletedToken.Register(() =>
        {
            RunOnUiThread(() =>
            {
                PlayingTickets.Remove(ticket);
                // 播放正常结束（而不是被取消）时不会走 CancellationToken 的回调，
                // PoppedRequests 里的这条记录就永远留着。下一次同一条请求重新入队时，
                // PopRequests 会把它当成「已经弹出过」直接丢掉；即使不会重入队，
                // 每条播过的提醒也都会一直被集合引用，长时间运行后持续上涨。
                PoppedRequests.Remove(request);
            });
        });
        return ticket;
    }

    /// <summary>
    /// 票据被取消（移交其它消费者、窗口卸载、丢弃）时的收尾与重排。
    /// </summary>
    private void OnTicketCancelled(NotificationPlayingTicket ticket, NotificationRequest request)
    {
        try
        {
            PlayingTickets.Remove(ticket);
            PlayingNotifications.Remove(request);
            PoppedRequests.Remove(request);
            UpdateNotificationPlayingState();
            Logger.LogTrace("票据 {} 已取消，准备重新加入提醒队列，{}", ticket.GetHashCode(), request);
            if (request.CancellationToken.IsCancellationRequested || request.CompletedToken.IsCancellationRequested)
            {
                // 请求本身已经被终结（提醒被关闭、界面卸载、被丢弃），不需要再重排。
                return;
            }

            // 注意不能在这里 await：这是 CancellationToken.Register 的回调，
            // 阻塞它等于阻塞调用 Cancel() 的线程（窗口卸载、链式取消都会卡住）。
            _ = RescheduleCancelledRequestAsync(request, ticket);
        }
        catch (Exception ex)
        {
            // 异常若从 Cancel() 的回调里穿出去，会打断调用方的收尾流程（例如卸载时的清理）。
            Logger.LogError(ex, "处理提醒票据取消时发生错误");
        }
    }

    /// <summary>
    /// 等上一条票据的播放真正收尾后，把请求重新加入提醒队列。
    /// </summary>
    private async Task RescheduleCancelledRequestAsync(NotificationRequest request, NotificationPlayingTicket ticket)
    {
        var settled = false;
        try
        {
            // worker 的完成信号在播放收尾（停音效、退会话）之后才给。
            // 不等它就把请求重新入队，同一条提醒会被两个 processor 同时处理（重复播放）。
            await ticket.CancellationCompletedCompletionSource.Task
                .WaitAsync(CancellationSettledTimeout)
                .ConfigureAwait(false);
            settled = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "等待票据 {} 取消收尾超时", ticket.GetHashCode());
        }

        try
        {
            if (request.CancellationToken.IsCancellationRequested || request.CompletedToken.IsCancellationRequested)
            {
                return;
            }

            if (!settled)
            {
                // 收尾没有确认：worker 可能还在播这条提醒，重排会让同一条提醒播两遍。
                // 但也不能把请求丢掉 —— 通过 ShowNotificationAsync / 自动化行动等待的调用方
                // 会永远收不到完成信号。这里把请求标记为结束并放行完成信号。
                Logger.LogWarning("票据 {} 未能在 {Timeout} 内收尾，放弃重排", ticket.GetHashCode(),
                    CancellationSettledTimeout);
                request.State = NotificationState.Cancelled;
                request.CompletedTokenSource.Cancel();
                return;
            }

            Logger.LogTrace("重新加入提醒队列, {}", request);
            RunOnUiThread(() => EnqueueNotification(request, true));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "重新加入提醒队列时发生错误");
        }
        finally
        {
            TryPopRequestsToConsumers();
        }
    }
    
    #region PropertyChanged
    
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
