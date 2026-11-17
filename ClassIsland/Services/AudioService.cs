using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace ClassIsland.Services;

public class AudioService(ILogger<AudioService> logger) : IAudioService
{
    private readonly object _audioEngineLock = new();
    private AudioEngine? _audioEngine;
    private ILogger<AudioService> Logger { get; } = logger;

    private RefCounted<AudioPlaybackDevice>? _sharedAudioPlaybackDevice;

    private object _audioPlaybackDeviceInitializeLock = new();

    /// <summary>
    /// 上一次刷新音频设备列表的时间。设备列表是初始化时的快照，
    /// 插拔耳机/显示器音响后不刷新的话，永远只会看到启动时的那些设备。
    /// 刷新本身要枚举系统设备，比较耗时，因此限制最小间隔。
    /// </summary>
    private DateTime _lastDeviceRefreshTime = DateTime.MinValue;

    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(2);

    public AudioEngine AudioEngine
    {
        get
        {
            if (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                throw new InvalidOperationException(
                    "出于线程安全考虑，禁止在非 MTA 线程上调用 AudioEngine。请在 MTA 线程上调用 AudioEngine。详细请见 https://github.com/ClassIsland/ClassIsland/issues/1333#issuecomment-3505591836");
            }
            return GetOrCreateAudioEngine();
        }
    }

    /// <summary>
    /// 延迟初始化音频引擎。MiniAudioEngine 会加载 miniaudio.dll，而它依赖 VC++ 运行库：
    /// 在缺少 VCRUNTIME140.dll 的机器上，构造会直接抛异常。如果放在构造函数里，
    /// 整个应用会在启动阶段崩溃（0x8007007E），连自动修复的机会都没有。
    /// 这里把失败结果排除在缓存之外，补装运行库后再次调用仍有机会恢复。
    /// </summary>
    private AudioEngine GetOrCreateAudioEngine()
    {
        if (_audioEngine != null)
        {
            return _audioEngine;
        }

        lock (_audioEngineLock)
        {
            return _audioEngine ??= Task.Run(() => new MiniAudioEngine()).Result;
        }
    }

    public AudioPlaybackDevice? TryInitializeDefaultPlaybackDevice() =>
        TryInitializeDefaultPlaybackDeviceAsync().Result;

    public Task<AudioPlaybackDevice?> TryInitializeDefaultPlaybackDeviceAsync() => 
        Task.Run(TryInitializeDefaultPlaybackDeviceInternal);

    public Task<RefCounted<AudioPlaybackDevice>.Lease?> TryInitializeDefaultPlaybackDeviceSafeAsync() => Task.Run(() =>
    {
        lock (_audioPlaybackDeviceInitializeLock)
        {
            if (_sharedAudioPlaybackDevice?.IsValueDisposed == false)
            {
                // 先租再比：RefCounted 不暴露裸值，租约期间设备不会被释放。
                var cachedLease = _sharedAudioPlaybackDevice.Rent();
                var cachedDeviceInfo = cachedLease.Value.Info;
                var defaultDeviceInfo = AudioEngine.PlaybackDevices.FirstOrDefault(x => x.IsDefault);

                // 系统默认播放设备会被切换（插拔耳机、接上显示器音响等），但缓存的设备实例
                // 仍然绑定在旧设备上，会继续从旧设备出声，直到重启应用。
                // 这里在每次取用前核对一次默认设备，变化了就重新初始化。
                if (defaultDeviceInfo == default || IsSameDevice(cachedDeviceInfo, defaultDeviceInfo))
                {
                    Logger.LogDebug("使用了缓存的音频设备 {} (Id={})", cachedDeviceInfo?.Name, cachedDeviceInfo?.Id);
                    return cachedLease;
                }

                Logger.LogInformation("默认播放设备已切换为 {} (Id={})，重新初始化音频设备",
                    defaultDeviceInfo.Name, defaultDeviceInfo.Id);
                cachedLease.Dispose();
                _sharedAudioPlaybackDevice.Dispose();
                _sharedAudioPlaybackDevice = null;
            }

            if (TryInitializeDefaultPlaybackDeviceInternal() is not { } device)
            {
                return null;
            }
            _sharedAudioPlaybackDevice = new RefCounted<AudioPlaybackDevice>(device);
            // 这里不能再 Dispose 掉 RefCounted 自身持有的那 1 个计数：
            // 那样租约一归还计数就归零，设备每次播放结束都会被销毁，
            // 下一次播放又得重新初始化（表现为连续播报时每句之间都有明显延迟，
            // 某些声卡上还会因反复开关设备而爆音）。共享设备应当常驻，
            // 由 AudioService.Dispose() 统一释放。
            return _sharedAudioPlaybackDevice.Rent();
        }
    });

    private static bool IsSameDevice(DeviceInfo? cached, DeviceInfo current) =>
        cached is { } info && Equals(info.Id, current.Id);

    private void RefreshDevicesIfNeeded()
    {
        // 同步初始化路径与 Task.Run 初始化路径可能并发进来，
        // SoundFlow 的设备枚举不是线程安全的，必须串行化。
        lock (_audioPlaybackDeviceInitializeLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastDeviceRefreshTime < DeviceRefreshInterval)
            {
                return;
            }

            _lastDeviceRefreshTime = now;
            try
            {
                // 设备列表在引擎初始化时固化，不刷新的话，之后插入的耳机/显示器音响
                // 根本不会出现在 PlaybackDevices 里，切换默认设备也读不到新设备。
                AudioEngine.UpdateDevicesInfo();
            }
            catch (Exception ex)
            {
                // 刷新失败不能影响播放：继续用上一次的设备列表。
                Logger.LogDebug(ex, "刷新音频设备列表失败");
            }
        }
    }

    private AudioPlaybackDevice? TryInitializeDefaultPlaybackDeviceInternal()
    {
        try
        {
            RefreshDevicesIfNeeded();
            var deviceInfo = AudioEngine.PlaybackDevices.FirstOrDefault(x => x.IsDefault);
            if (deviceInfo == default)
            {
                Logger.LogDebug("找不到可用的音频设备");
                return null;
            }

            Logger.LogDebug("初始化音频设备 {} (Id={})", deviceInfo.Name, deviceInfo.Id);
            var device = AudioEngine.InitializePlaybackDevice(deviceInfo, IAudioService.DefaultAudioFormat);
            device.MasterMixer.Volume = 1.0f;
            device.Start();
            return device;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "初始化音频设备失败");
            return null;
        }
        
    }

    public Task PlayAudioAsync(Stream audio, float volume, CancellationToken? cancellationToken = null) => Task.Run(async () =>
    {
        using var audioStream = audio;
        cancellationToken ??= CancellationToken.None;
        using var lease = await TryInitializeDefaultPlaybackDeviceSafeAsync();
        if (lease == null)
        {
            return;
        }

        var device = lease.Value;
        using var player = new SoundPlayer(AudioEngine, IAudioService.DefaultAudioFormat,
            new StreamDataProvider(AudioEngine, IAudioService.DefaultAudioFormat, audio));
        player.Volume = volume;
        Logger.LogDebug("开始播放音频 {}", audio.GetHashCode());
        device.MasterMixer.AddComponent(player);
        var tcs = new TaskCompletionSource<bool>();

        player.PlaybackEnded += OnPlayerOnPlaybackEnded;
        // 注册必须 Dispose。CancellationToken 来自提醒请求的 CTS，生命周期远长于一次播放；
        // 不注销的话，每播放一段音频就在 token 上多挂一个回调（连同 tcs、player 一起被引用），
        // 连续播报几十条后这些已结束的播放器全都无法回收。
        using var cancellationRegistration = cancellationToken.Value.Register(() =>
        {
            Logger.LogDebug("取消播放音频 {}", audio.GetHashCode());
            tcs.TrySetResult(false);
        });
        player.Play();
        tcs.Task.Wait();  // 不要在此处 await，否则会导致设备停止过程阻塞，无法完成播放流程。
        Logger.LogDebug("结束播放音频 {}", audio.GetHashCode());
        player.PlaybackEnded -= OnPlayerOnPlaybackEnded;

        return;

        void OnPlayerOnPlaybackEnded(object? sender, EventArgs args)
        {
            tcs.TrySetResult(true);
        }
    });

    public async Task PlayAudioAsync(string filePath, float volume, CancellationToken? cancellationToken = null)
    {
        using var audio = File.OpenRead(filePath);
        await PlayAudioAsync(audio, volume, cancellationToken);
    }

    public void Dispose()
    {
        // 先归还共享设备的基础引用，让 RefCounted 有机会真正 Dispose 底层设备，
        // 再销毁引擎。顺序反了会在引擎已经释放后仍去 Stop 设备。
        try
        {
            _sharedAudioPlaybackDevice?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "释放共享音频设备失败");
        }
        finally
        {
            _sharedAudioPlaybackDevice = null;
        }

        try
        {
            _audioEngine?.Dispose();
        }
        catch (Exception ex)
        {
            // 退出流程里 Dispose 抛异常会直接打断 App 的关闭逻辑，
            // 后面的配置保存、单实例锁释放都执行不到。这里只记录，不向上抛。
            Logger.LogWarning(ex, "释放音频引擎失败");
        }

        GC.SuppressFinalize(this);
    }
}
