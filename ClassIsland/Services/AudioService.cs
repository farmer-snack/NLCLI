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
    private readonly AudioEngine _audioEngine = Task.Run((() => new MiniAudioEngine())).Result;
    private ILogger<AudioService> Logger { get; } = logger;

    private RefCounted<AudioPlaybackDevice>? _sharedAudioPlaybackDevice;

    private object _audioPlaybackDeviceInitializeLock = new();

    public AudioEngine AudioEngine
    {
        get
        {
            if (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                throw new InvalidOperationException(
                    "出于线程安全考虑，禁止在非 MTA 线程上调用 AudioEngine。请在 MTA 线程上调用 AudioEngine。详细请见 https://github.com/ClassIsland/ClassIsland/issues/1333#issuecomment-3505591836");
            }
            return _audioEngine;
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
            var lease2 = _sharedAudioPlaybackDevice.Rent();
            _sharedAudioPlaybackDevice.Dispose();
            return lease2;
        }
    });

    private static bool IsSameDevice(DeviceInfo? cached, DeviceInfo current) =>
        cached is { } info && Equals(info.Id, current.Id);

    private AudioPlaybackDevice? TryInitializeDefaultPlaybackDeviceInternal()
    {
        try
        {
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
        cancellationToken.Value.Register(() =>
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
        AudioEngine.Dispose();
        GC.SuppressFinalize(this);
    }
}
