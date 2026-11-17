using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

/// <summary>
/// 在托盘图标被隐藏时监听全局鼠标，识别用户画出的爱心手势并恢复托盘图标。
/// 目前仅在 Windows 上生效（依赖 WH_MOUSE_LL 全局钩子）。
/// </summary>
public sealed class HeartGestureService : IHostedService, IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_QUIT = 0x0012;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint PM_REMOVE = 0x0001;
    private const uint HookMessageWaitMilliseconds = 100;

    private const double MinGestureSize = 90;
    private const double MinGestureHeight = 80;
    private const double CloseDistanceRatio = 0.35;
    private const double MaxChamferDistance = 0.22;

    private static readonly IReadOnlyList<PointD> HeartTemplate = BuildHeartTemplate();

    private readonly ILogger<HeartGestureService> _logger;
    private readonly SettingsService _settingsService;
    private readonly TaskBarIconService _taskBarIconService;
    private readonly object _syncRoot = new();
    private readonly List<PointD> _points = new();

    private Settings? _subscribedSettings;
    private LowLevelMouseProc? _hookProc;
    private Thread? _hookThread;
    private volatile uint _hookThreadId;
    private IntPtr _hookHandle = IntPtr.Zero;
    private volatile bool _running;
    private bool _tracking;
    private DateTime _strokeStartedAt;
    private PointD _lastPoint;
    private int _recognizing;

    public HeartGestureService(ILogger<HeartGestureService> logger, SettingsService settingsService,
        TaskBarIconService taskBarIconService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _taskBarIconService = taskBarIconService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settingsService.PropertyChanged += SettingsServiceOnPropertyChanged;
        SubscribeSettings(_settingsService.Settings);
        ApplyState();
        AppBase.Current.AppStopping += CurrentOnAppStopping;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopHook();
        _settingsService.PropertyChanged -= SettingsServiceOnPropertyChanged;
        if (_subscribedSettings != null)
        {
            _subscribedSettings.PropertyChanged -= SettingsOnPropertyChanged;
            _subscribedSettings = null;
        }

        AppBase.Current.AppStopping -= CurrentOnAppStopping;
        return Task.CompletedTask;
    }

    private void SettingsServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsService.Settings))
        {
            return;
        }

        SubscribeSettings(_settingsService.Settings);
        ApplyState();
    }

    private void SubscribeSettings(Settings settings)
    {
        if (ReferenceEquals(_subscribedSettings, settings))
        {
            return;
        }

        if (_subscribedSettings != null)
        {
            _subscribedSettings.PropertyChanged -= SettingsOnPropertyChanged;
        }

        _subscribedSettings = settings;
        _subscribedSettings.PropertyChanged += SettingsOnPropertyChanged;
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.IsTaskBarIconVisible))
        {
            ApplyState();
        }
    }

    private void ApplyState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_settingsService.Settings.IsTaskBarIconVisible)
        {
            StopHook();
        }
        else
        {
            StartHook();
        }
    }

    private void CurrentOnAppStopping(object? sender, EventArgs e)
    {
        StopHook();
    }

    private void StartHook()
    {
        lock (_syncRoot)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _hookProc = HookCallback;
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "ClassIsland Heart Gesture Hook"
            };
            _hookThread.Start();
            _logger.LogInformation("托盘图标已隐藏，开始监听爱心手势。");
        }
    }

    private void StopHook()
    {
        Thread? thread;
        uint threadId;
        lock (_syncRoot)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            thread = _hookThread;
            threadId = _hookThreadId;
            _hookThread = null;
        }

        if (threadId != 0)
        {
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        thread?.Join(1500);
        lock (_syncRoot)
        {
            _tracking = false;
            _points.Clear();
        }

        // _hookHandle 由钩子线程自己卸载并清零。这里不再代它清零，
        // 否则线程若因超时尚未退出，会看到句柄为空而跳过 UnhookWindowsHookEx。
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc!, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            _logger.LogError("无法安装全局鼠标钩子，错误码：{Code}", Marshal.GetLastWin32Error());
            _running = false;
            return;
        }

        // 不能直接阻塞在 GetMessage 上：StopHook 依赖向本线程投递 WM_QUIT 来唤醒，
        // 而线程 ID 是在线程启动后才写入的，存在“停止请求早于线程就绪”的时间窗。
        // 一旦错过，线程会永久阻塞，钩子无法卸载，再次隐藏托盘时还会装上第二个钩子。
        // 这里改为带超时的消息等待，每 100 毫秒重新检查一次运行标志。
        while (_running)
        {
            var waitResult = MsgWaitForMultipleObjects(0, IntPtr.Zero, false, HookMessageWaitMilliseconds, QS_ALLINPUT);
            if (!_running)
            {
                break;
            }

            if (waitResult != WAIT_OBJECT_0)
            {
                continue;
            }

            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (message.Message == WM_QUIT)
                {
                    _running = false;
                    break;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _hookThreadId = 0;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _running)
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var point = new PointD(data.Point.X, data.Point.Y);
                switch (wParam.ToInt32())
                {
                    case WM_LBUTTONDOWN:
                        BeginStroke(point);
                        break;
                    case WM_MOUSEMOVE:
                        AddPoint(point);
                        break;
                    case WM_LBUTTONUP:
                        EndStroke(point);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理鼠标钩子事件时发生异常。");
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void BeginStroke(PointD point)
    {
        _points.Clear();
        _points.Add(point);
        _lastPoint = point;
        _strokeStartedAt = DateTime.UtcNow;
        _tracking = true;
    }

    private void AddPoint(PointD point)
    {
        if (!_tracking)
        {
            return;
        }

        if (Distance(_lastPoint, point) < 2)
        {
            return;
        }

        _points.Add(point);
        _lastPoint = point;
    }

    private void EndStroke(PointD point)
    {
        if (!_tracking)
        {
            return;
        }

        _tracking = false;
        AddPoint(point);

        var duration = DateTime.UtcNow - _strokeStartedAt;
        if (_points.Count < 16 || duration < TimeSpan.FromMilliseconds(350) || duration > TimeSpan.FromSeconds(20))
        {
            _points.Clear();
            return;
        }

        if (Interlocked.Exchange(ref _recognizing, 1) == 1)
        {
            return;
        }

        var points = _points.ToArray();
        _points.Clear();
        Task.Run(() =>
        {
            try
            {
                if (IsHeart(points))
                {
                    _logger.LogInformation("检测到爱心手势，正在临时显示托盘图标。");
                    // 只临时显示，不把「显示托盘图标」设置改回开启：
                    // 用户用完后图标会自动收起，下次仍可再画爱心唤出。
                    Dispatcher.UIThread.Post(_taskBarIconService.ShowTemporarily);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "识别爱心手势时发生异常。");
            }
            finally
            {
                Interlocked.Exchange(ref _recognizing, 0);
            }
        });
    }

    private static bool IsHeart(IReadOnlyList<PointD> rawPoints)
    {
        var points = Resample(rawPoints, 128);
        if (points.Count < 32)
        {
            return false;
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width < MinGestureSize || height < MinGestureHeight)
        {
            return false;
        }

        var ratio = width / height;
        if (ratio is < 0.55 or > 1.9)
        {
            return false;
        }

        var diagonal = Math.Sqrt(width * width + height * height);
        var pathLength = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            pathLength += Distance(points[i - 1], points[i]);
        }

        if (pathLength < diagonal * 1.8 || pathLength > diagonal * 6.5)
        {
            return false;
        }

        if (Distance(points[0], points[^1]) > diagonal * CloseDistanceRatio)
        {
            return false;
        }

        var centerX = (minX + maxX) / 2;
        var bottom = points[0];
        foreach (var point in points)
        {
            if (point.Y > bottom.Y)
            {
                bottom = point;
            }
        }

        if (Math.Abs(bottom.X - centerX) > width * 0.24)
        {
            return false;
        }

        var centerPoints = points.Where(p => Math.Abs(p.X - centerX) < width * 0.10).ToList();
        var leftPoints = points.Where(p => p.X < centerX - width * 0.10).ToList();
        var rightPoints = points.Where(p => p.X > centerX + width * 0.10).ToList();
        if (centerPoints.Count == 0 || leftPoints.Count == 0 || rightPoints.Count == 0)
        {
            return false;
        }

        var centerTop = centerPoints.Min(p => p.Y);
        var leftTop = leftPoints.Min(p => p.Y);
        var rightTop = rightPoints.Min(p => p.Y);

        // 心形上方有两个凸起（y 更小），中间有一个下凹。
        if (leftTop > centerTop - height * 0.06 || rightTop > centerTop - height * 0.06)
        {
            return false;
        }

        var normalized = Normalize(points, minX, maxX, minY, maxY);
        var toTemplate = ChamferDistance(normalized, HeartTemplate);
        var fromTemplate = ChamferDistance(HeartTemplate, normalized);
        return Math.Max(toTemplate, fromTemplate) < MaxChamferDistance;
    }

    private static List<PointD> Resample(IReadOnlyList<PointD> points, int targetCount)
    {
        var result = new List<PointD>(targetCount);
        if (points.Count < 2)
        {
            return result;
        }

        var cumulative = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            cumulative[i] = cumulative[i - 1] + Distance(points[i - 1], points[i]);
        }

        var total = cumulative[^1];
        if (total <= 0)
        {
            return result;
        }

        var step = total / (targetCount - 1);
        var index = 0;
        for (var i = 0; i < targetCount; i++)
        {
            var target = Math.Min(step * i, total);
            while (index < cumulative.Length - 2 && cumulative[index + 1] < target)
            {
                index++;
            }

            var segmentLength = cumulative[index + 1] - cumulative[index];
            var t = segmentLength <= 0 ? 0 : (target - cumulative[index]) / segmentLength;
            var x = points[index].X + (points[index + 1].X - points[index].X) * t;
            var y = points[index].Y + (points[index + 1].Y - points[index].Y) * t;
            result.Add(new PointD(x, y));
        }

        return result;
    }

    private static List<PointD> Normalize(IReadOnlyList<PointD> points, double minX, double maxX, double minY, double maxY)
    {
        var width = Math.Max(maxX - minX, 1e-6);
        var height = Math.Max(maxY - minY, 1e-6);
        return points.Select(p => new PointD((p.X - minX) / width, (p.Y - minY) / height)).ToList();
    }

    private static IReadOnlyList<PointD> BuildHeartTemplate()
    {
        var points = new List<PointD>(256);
        for (var i = 0; i < 256; i++)
        {
            var t = i * Math.PI * 2 / 256;
            var x = 16 * Math.Pow(Math.Sin(t), 3);
            var y = -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t));
            points.Add(new PointD(x, y));
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return Normalize(points, minX, maxX, minY, maxY);
    }

    private static double ChamferDistance(IReadOnlyList<PointD> from, IReadOnlyList<PointD> to)
    {
        var sum = 0.0;
        foreach (var point in from)
        {
            var nearest = double.MaxValue;
            foreach (var candidate in to)
            {
                var distance = Distance(point, candidate);
                if (distance < nearest)
                {
                    nearest = distance;
                }
            }

            sum += nearest;
        }

        return sum / Math.Max(from.Count, 1);
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void Dispose()
    {
        StopHook();
    }

    private readonly record struct PointD(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr pHandles, bool bWaitAll,
        uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
