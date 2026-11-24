using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Core.Enums.Notification;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Shared.Abstraction.Models;
using ClassIsland.Shared.Interfaces;
using ClassIsland.Shared.Models.Notification;
using Microsoft.Extensions.Logging;
/*
 * このまま未完成なままでいいの
 * 就这样有始无终便可以
 * エンドロールはいらないから
 * 因为我们并不需要片尾的演职员表
 * 壊れた時計は魔法をかけてももう動かないわ
 * 支离破碎的时针即便施加魔法也难以使其再次运转
 * 君との甘すぎる夢の中で泳いでいる事も忘れて
 * 遨游在与你如痴如醉的梦境之中使我流连忘返
 * 誰も知らないこの物語は二人だけのランデヴー
 * 无人知晓的这篇物语便是独属于二人的桑间之约
 *             —— 未完成ランデヴー - Lezel
 */
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;


namespace ClassIsland.Services;

public class NotificationWorkerService : INotificationWorkerService
{
    
    private ISpeechService SpeechService { get; }
    private IAudioService AudioService { get; }
    private SettingsService SettingsService { get; }
    private IExactTimeService ExactTimeService { get; }
    private ILessonsService LessonsService { get; }
    private ILogger<NotificationWorkerService> Logger { get; }
    public ITopmostEffectPlayer TopmostEffectPlayer { get; }

    private List<(NotificationRequest request, bool overlay)> PlayingRequests { get; } = [];

    /// <summary>
    /// <see cref="PlayingRequests"/> 的同步锁。
    /// </summary>
    /// <remarks>
    /// 这个列表在 UI 线程的主计时器里被遍历（刷新进度），而增删发生在提醒处理器里。
    /// 处理器虽然多数时候由 UI 线程的消费者驱动，但票据取消、链式取消都可能从后台线程
    /// 恢复续体，裸 <see cref="List{T}"/> 被并发修改会让遍历抛「集合已修改」。
    /// </remarks>
    private readonly object _playingRequestsLock = new();

    /// <summary>
    /// 票据取消后等待 processor 收尾的兜底时间。
    /// </summary>
    private static readonly TimeSpan CancellationSettledFallbackDelay = TimeSpan.FromSeconds(1.5);
    
    public NotificationWorkerService(ISpeechService speechService,
        IAudioService audioService, 
        SettingsService settingsService,
        IExactTimeService exactTimeService,
        ILessonsService lessonsService,
        ILogger<NotificationWorkerService> logger,
        ITopmostEffectPlayer topmostEffectPlayer)
    {
        SpeechService = speechService;
        AudioService = audioService;
        SettingsService = settingsService;
        ExactTimeService = exactTimeService;
        LessonsService = lessonsService;
        Logger = logger;
        TopmostEffectPlayer = topmostEffectPlayer;

        LessonsService.PostMainTimerTicked += LessonsServiceOnPostMainTimerTicked;
    }

    private void LessonsServiceOnPostMainTimerTicked(object? sender, EventArgs e)
    {
        var now = ExactTimeService.GetCurrentLocalDateTime();
        (NotificationRequest request, bool overlay)[] snapshot;
        lock (_playingRequestsLock)
        {
            snapshot = PlayingRequests.ToArray();
        }
        foreach (var (request, overlay) in snapshot)
        {
            if (!overlay || request.OverlayContent is not {} content)
            {
                continue;
            }

            var session = request.OverlaySession;
            request.LeftProgress = session.IsExplicitEndTime
                ? 1 - (now - session.SessionStartTime) / content.Duration
                : 1 - (session.SessionPlayedTime + session.TimingStopwatch.Elapsed) / content.Duration;
        }
    }
    
    private TimeSpan SetupNotificationSessionTiming(Guid sid, NotificationContent content, NotificationPlayingSessionInfo session)
    {
        var now = ExactTimeService.GetCurrentLocalDateTime();
        var explicitEndTime = content.EndTime != null;
        if (!content.IsTimingInit && explicitEndTime)  // 如果目标结束时间不为空，那么就计算持续时间
        {
            var rawTime = content.EndTime!.Value - now;
            content.Duration = rawTime > TimeSpan.Zero ? rawTime : TimeSpan.Zero;
        }

        if (session.SessionStartTime == DateTime.MinValue)
        {
            session.SessionStartTime = now;
        }

        session.IsExplicitEndTime = explicitEndTime;
        session.CurrentTicketStartTime = now;
        session.TimingStopwatch.Restart();
        content.IsTimingInit = true;

        var duration = explicitEndTime
            ? content.EndTime!.Value - now
            : content.Duration - session.SessionPlayedTime;
        Logger.LogTrace("[{sid}] 计算当前票据会话持续时间，now={now}, playedTime={playedTime}, duration={duration}", sid, now, session.SessionPlayedTime, duration);
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    public NotificationPlayingTicket CreateTicket(NotificationRequest request)
    {
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        INotificationSettings settings = SettingsService.Settings;
        foreach (var i in new List<NotificationSettings?>([
                         request.ChannelSettings,
                         request.ProviderSettings,
                         request.RequestNotificationSettings
                     ]).OfType<NotificationSettings>()
                     .Where(i => i.IsSettingsEnabled))
        {
            settings = i;
            break;
        }
        
        var effectId = settings is not NotificationSettings ns || ns.OverridesNotificationEffectId 
            ? settings.NotificationEffectId
            : request.EffectId ?? SettingsService.Settings.NotificationEffectId;

        var cancellationCompletedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationTokenSource.Token.Register(() =>
        {
            if (request.State != NotificationState.Playing)
            {
                cancellationCompletedSource.TrySetResult();
                return;
            }

            // 票据取消时请求仍是 Playing，说明还有 processor 在跑，正常情况下它的 finally 会完成这个信号。
            // 但存在「遮罩正常结束、正文因为时长为 0 被直接跳过」这种没有任何 processor 收尾的路径，
            // 一旦没人完成信号，宿主侧的等待就会一直挂着，之后这条提醒再也派发不出去。
            // 这里挂一个延迟兜底：宁可让重排早一点，也不要让整条链路停摆。
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(CancellationSettledFallbackDelay).ConfigureAwait(false);
                    if (cancellationCompletedSource.TrySetResult())
                    {
                        Logger.LogDebug("票据取消后迟迟没有收尾，已强制放行：{}", request.GetHashCode());
                    }
                }
                catch (Exception ex)
                {
                    // 纯粹是兜底，失败也不该影响任何调用方。
                    Logger.LogDebug(ex, "提醒票据取消兜底等待失败");
                }
            }, CancellationToken.None);
        });
        // 提醒音效的取消令牌，与整个提醒请求的取消令牌关联，
        // 使音效在整个提醒（遮罩+正文）播放完毕或被打断时才取消，而非在遮罩阶段结束时就取消。
        var soundsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
        var ticket = new NotificationPlayingTicket()
        {
            ProcessMask = CreateMaskProcessor(request, cancellationTokenSource.Token, settings, cancellationCompletedSource, soundsCts),
            ProcessOverlay = CreateOverlayProcessor(request, cancellationTokenSource.Token, settings, cancellationCompletedSource, soundsCts),
            Request = request,
            Settings = settings,
            CancellationTokenSource = cancellationTokenSource,
            CancellationCompletedCompletionSource = cancellationCompletedSource,
            TryPlayNotificationEffect = context =>
            {
                if (!settings.IsNotificationEffectEnabled || !SettingsService.Settings.AllowNotificationEffect)
                {
                    return;
                }
                var effectFactory = INotificationWorkerService.RegisteredEffects.FirstOrDefault(x => x.Id == effectId)?
                    .Factory;
                if (effectFactory?.Invoke(context) is not {} effect)
                {
                    return;
                }
                TopmostEffectPlayer.PlayEffect(effect);
            }
        };
        return ticket;
    }

    private Func<Task> CreateMaskProcessor(NotificationRequest request, CancellationToken cancellationToken, INotificationSettings settings, TaskCompletionSource cancellationCompletedSource, CancellationTokenSource soundsCts) => async () =>
    {
        await ProcessNotificationSessionCore(request, request.MaskContent, request.MaskSession, true, cancellationToken, settings, cancellationCompletedSource, soundsCts);
    };
    
    private Func<Task> CreateOverlayProcessor(NotificationRequest request, CancellationToken cancellationToken, INotificationSettings settings, TaskCompletionSource cancellationCompletedSource, CancellationTokenSource soundsCts) => async () =>
    {
        if (request.OverlayContent == null)
        {
            return;
        }
        await ProcessNotificationSessionCore(request, request.OverlayContent, request.OverlaySession, false, cancellationToken, settings, cancellationCompletedSource, soundsCts);
    };

    /// <summary>
    /// 播放提醒音效。自定义音效文件缺失或损坏时回退到内置音效，避免提醒直接变成静音。
    /// </summary>
    private void PlayNotificationSound(string? soundPath, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(soundPath))
                {
                    if (File.Exists(soundPath))
                    {
                        await AudioService.PlayAudioAsync(soundPath,
                            (float)SettingsService.Settings.NotificationSoundVolume, cancellationToken);
                        return;
                    }

                    Logger.LogWarning("提醒音效文件不存在，已回退到内置音效：{}", soundPath);
                }

                using var stream = AssetLoader.Open(INotificationProvider.DefaultNotificationSoundUri);
                await AudioService.PlayAudioAsync(stream,
                    (float)SettingsService.Settings.NotificationSoundVolume, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                // 之前这里用丢弃的 Task 直接播放，异常被吞掉，音效失效时看不出任何原因。
                Logger.LogError(e, "无法播放提醒音效：{}", soundPath);
            }
        }, CancellationToken.None);
    }

    private async Task ProcessNotificationSessionCore(NotificationRequest request,
        NotificationContent content,
        NotificationPlayingSessionInfo session,
        bool isMask,
        CancellationToken cancellationToken, 
        INotificationSettings settings,
        TaskCompletionSource cancellationCompletedSource,
        CancellationTokenSource soundsCts)
    {
        var id = Guid.NewGuid();
        var duration = SetupNotificationSessionTiming(id, content, session);
        request.State = NotificationState.Playing;
        var tuple = (request, !isMask);
        lock (_playingRequestsLock)
        {
            PlayingRequests.Add(tuple);
        }
        Logger.LogTrace("[{id}] Start session, isMask={isMask}, duration={duration}", id, isMask, duration);
        try
        {
            var isSpeechEnabled = settings.IsSpeechEnabled && content.IsSpeechEnabled && SettingsService.Settings.AllowNotificationSpeech;
            if (!session.HasSoundsPlayed && isSpeechEnabled)
            {
                SpeechService.EnqueueSpeechQueue(content.SpeechContent);
            }
            
            if (!session.HasSoundsPlayed && isMask && settings.IsNotificationSoundEnabled && SettingsService.Settings.AllowNotificationSound)
            {
                Logger.LogInformation("即将播放提醒音效：{}", settings.NotificationSoundPath);
                PlayNotificationSound(settings.NotificationSoundPath, soundsCts.Token);
            }
            
            session.HasSoundsPlayed = true;
            await Task.Delay(duration, cancellationToken);
            if (request.OverlayContent == null || !isMask)
            {
                request.State = NotificationState.Completed;
                SpeechService.ClearSpeechQueue();
                await soundsCts.CancelAsync();
                await request.CompletedTokenSource.CancelAsync();
            }

            session.IsCompleted = true;
        }
        catch (TaskCanceledException)
        {
            Logger.LogInformation("提醒请求 {request} 取消播放", request.GetHashCode());
            request.State = request.CancellationToken.IsCancellationRequested
                ? NotificationState.Cancelled
                : NotificationState.Paused;
            throw;
        }
        catch
        {
            request.State = NotificationState.Paused;
            throw;
        }
        finally
        {
            try
            {
                var playedTime = session.TimingStopwatch.Elapsed;
                session.TimingStopwatch.Reset();
                session.SessionPlayedTime += playedTime;
                Logger.LogTrace("[{id}] END session, isMask={isMask}, playedTime={playedTime}", id, isMask, playedTime);
                lock (_playingRequestsLock)
                {
                    PlayingRequests.Remove(tuple);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // 取消提醒时语音队列也要一起清掉；否则音效停了，SpeechService
                        // 仍会把已经入队的语音继续念完（取消后还在朗读）。
                        SpeechService.ClearSpeechQueue();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "取消提醒语音时发生错误");
                    }
                    try
                    {
                        await soundsCts.CancelAsync();
                    }
                    catch (Exception ex)
                    {
                        // 音效播放的取消回调由音频服务提供，抛异常不能连累下面的完成信号。
                        Logger.LogWarning(ex, "取消提醒音效时发生错误");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "提醒会话收尾时发生错误");
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // 宿主靠这个信号把被打断的提醒重新入队。它必须在所有路径上完成：
                    // 上面任何一步抛异常都会跳过它，结果就是这条提醒在没有报错的情况下
                    // 既不再播放、也不通知等待方，等于凭空消失。
                    cancellationCompletedSource.TrySetResult();
                }
            }
        }
    }
    
}
