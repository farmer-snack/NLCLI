namespace ClassIsland.Shared.Models.Notification;

/// <summary>
/// 代表提醒队列的优先级顺序。
/// </summary>
public class NotificationPriority : IComparable
{
    /// <summary>
    /// 代表提醒队列的优先级顺序。
    /// </summary>
    internal NotificationPriority(int priority, int index, bool isPriorityOverride, bool isNotificationPlayed)
    {
        Priority = priority;
        Index = index;
        IsPriorityOverride = isPriorityOverride;
        IsNotificationPlayed = isNotificationPlayed;
    }

    /// <summary>
    /// 提醒提供方优先级。
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// 提醒插入队列的顺序。
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// 提醒是否插队。
    /// </summary>
    public bool IsPriorityOverride { get; }
    
    /// <summary>
    /// 提醒是否已经播放
    /// </summary>
    public bool IsNotificationPlayed { get; }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (obj is not NotificationPriority priority)
        {
            return -1;
        }

        // PriorityQueue 是最小堆，Dequeue 先出「比较结果更小」的那个，
        // 所以插队项必须返回 -1 才能排到最前面。原来无条件 return 1 等于把紧急提醒
        // 排到了所有普通提醒之后 —— 队列里积压 N 条普通提醒时，紧急通知要等它们全播完。
        // 同时两个插队项互相比较也必须给出确定结果，否则违反反对称性，堆序不可靠。
        if (IsPriorityOverride != priority.IsPriorityOverride)
        {
            return IsPriorityOverride ? -1 : 1;
        }

        // if (IsNotificationPlayed && !priority.IsNotificationPlayed)
        // {
        //     return 1;
        // }
        if (Priority != priority.Priority)
        {
            return Priority - priority.Priority;
        }

        return Index - priority.Index;
    }
}