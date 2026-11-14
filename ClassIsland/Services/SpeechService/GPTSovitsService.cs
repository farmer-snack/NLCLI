using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Abstraction.Services;

using Microsoft.Extensions.Logging;

using PgpCore;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;

namespace ClassIsland.Services.SpeechService;

[SpeechProviderInfo("classisland.speech.gpt-sovits", "GPT-SoVITS")]
public class GptSoVitsService : ISpeechService
{
    public static readonly string GPTSoVITSCacheFolderPath = Path.Combine(CommonDirectories.AppCacheFolderPath, "GPTSoVITS");

    public IAudioService AudioService { get; }
    private ILogger<GptSoVitsService> Logger { get; }

    private SettingsService SettingsService { get; }

    private Queue<GptSoVitsPlayInfo> PlayingQueue { get; } = new();

    private bool IsPlaying { get; set; } = false;

    private GptSoVitsPlayInfo? _currentPlayInfo;

    /// <summary>
    /// 复用的 HttpClient。每朗读一句就 new 一个 HttpClient 会不断创建新的连接池，
    /// 被丢弃的实例要等 GC 才释放，短时间内连续播报就能把本机端口耗光（TIME_WAIT），
    /// 之后所有请求都会以「无法连接」失败。
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public GptSoVitsService(IAudioService audioService, ILogger<GptSoVitsService> logger, SettingsService settingsService)
    {
        AudioService = audioService;
        Logger = logger;
        SettingsService = settingsService;
        
        Logger.LogInformation("初始化了 GPTSoVITS 服务。");
    }

    private string GetCachePath(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var md5 = MD5.HashData(data);
        var md5String = md5.Aggregate("", (current, t) => current + t.ToString("x2"));
        var path = Path.Combine(GPTSoVITSCacheFolderPath, SettingsService.Settings.GptSoVitsSpeechSettings.GptSoVitsVoiceName, $"{md5String}.wav");
        var directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory) && directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        return path;
    }

    public void EnqueueSpeechQueue(string text)
    {
        Logger.LogInformation("以 {VoiceName} 朗读文本：{Text}", SettingsService.Settings.GptSoVitsSpeechSettings.GptSoVitsVoiceName, text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var cancellationTokenSource = new CancellationTokenSource();

        var cache = GetCachePath(text);
        Logger.LogDebug("语音缓存路径：{CachePath}", cache);

        Task<bool>? task = null;
        if (!File.Exists(cache))
        {
            task = GenerateSpeechAsync(text, cache, cancellationTokenSource.Token);
        }

        if (cancellationTokenSource.IsCancellationRequested)
        {
            cancellationTokenSource.Dispose();
            return;
        }

        PlayingQueue.Enqueue(new GptSoVitsPlayInfo(cache, cancellationTokenSource, task));
        _ = ProcessPlayerList();
    }

    public void ClearSpeechQueue()
    {
        try
        {
            _currentPlayInfo?.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 当前条目正好在 ProcessPlayerList 的 finally 里被释放，属于正常竞态。
        }

        while (PlayingQueue.Count > 0)
        {
            var playInfo = PlayingQueue.Dequeue();
            try
            {
                playInfo.CancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 同上。
            }

            playInfo.CancellationTokenSource.Dispose();
        }
    }

    private async Task<bool> GenerateSpeechAsync(string text, string filePath, CancellationToken cancellationToken)
    {
        var settings = SettingsService.Settings.GptSoVitsSpeechSettings;
        string? apiSignature = null;

        if (settings.IsInternal && GptSovitsSecrets.IsSecretsFilled)
        {
            var key = new EncryptionKeys(GptSovitsSecrets.PrivateKey, GptSovitsSecrets.PrivateKeyPassPhrase);
            var pgp = new PGP(key);
            var ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            var signData = new
            {
                ContentSHA256 = SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                Timestamp = Convert.ToInt64(ts.TotalMilliseconds)
            };
            var sign = await pgp.SignAsync(JsonSerializer.Serialize(signData));
            apiSignature = Convert.ToBase64String(Encoding.UTF8.GetBytes(sign));
        }
        var serverIp = settings.GptSoVitsServerIp;
        var port = settings.GptSoVitsPort;

        var queryParams = new Dictionary<string, string>
        {
            { "text", text },
            { "text_lang", settings.GptSoVitsTextLang },
            { "ref_audio_path", settings.GptSoVitsRefAudioPath },
            { "prompt_lang", settings.GptSoVitsPromptLang },
            { "prompt_text", settings.GptSoVitsPromptText },
            { "text_split_method", settings.GptSoVitsTextSplitMethod },
            { "batch_size", settings.GptSoVitsBatchSize.ToString() },
            { "media_type", "wav" },
            { "streaming_mode", "false" }
        };

        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
        var requestUri = $"http://{serverIp}:{port}/tts?{queryString}";

        try
        {
            Logger.LogDebug("发送 TTS 请求到：{RequestUri}", requestUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (apiSignature != null)
            {
                request.Headers.TryAddWithoutValidation("X-ClassIsland-ApiSignature", apiSignature);
            }

            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClassIsland", AppBase.AppVersion));
            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                // 先写临时文件再改名：直接写目标路径时，下载到一半被取消/断网，
                // 目标位置会留下一个能被 File.Exists 命中的半截 wav，
                // 之后每次朗读都播放这段损坏音频，而且永远不会重新合成。
                var tempPath = filePath + ".tmp";
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }

                File.Move(tempPath, filePath, true);
                Logger.LogDebug("语音生成并保存到：{FilePath}", filePath);
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Logger.LogError("TTS 请求失败，状态码：{StatusCode}, 内容：{ErrorContent}", response.StatusCode, errorContent);
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 队列被清空/提醒被切走导致的取消不是失败，不该刷错误日志。
            Logger.LogInformation("已取消 GPT-SoVITS 语音合成：{FilePath}", filePath);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "发送 TTS 请求时发生异常。");
            return false;
        }
    }

    private async Task ProcessPlayerList()
    {
        if (IsPlaying)
            return;
        IsPlaying = true;
        try
        {
            while (PlayingQueue.Count > 0)
            {
                var playInfo = _currentPlayInfo = PlayingQueue.Dequeue();
                try
                {
                    if (playInfo.CancellationTokenSource.IsCancellationRequested)
                        continue;
                    if (playInfo.DownloadTask != null)
                    {
                        Logger.LogDebug("等待语音生成完成...");
                        var result = await playInfo.DownloadTask;
                        if (!result)
                        {
                            Logger.LogError("语音 {} 生成失败。", playInfo.FilePath);
                            continue;
                        }
                        Logger.LogDebug("语音生成完成。");
                    }

                    if (!File.Exists(playInfo.FilePath))
                    {
                        Logger.LogError("语音文件不存在：{FilePath}", playInfo.FilePath);
                        continue;
                    }
                    
                    Logger.LogDebug("开始播放 {FilePath}", playInfo.FilePath);
                    await AudioService.PlayAudioAsync(playInfo.FilePath,
                        (float)SettingsService.Settings.SpeechVolume, playInfo.CancellationTokenSource.Token);
                    Logger.LogDebug("结束播放 {FilePath}", playInfo.FilePath);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "无法播放语音。");
                }
                finally
                {
                    _currentPlayInfo = null;
                    playInfo.CancellationTokenSource.Dispose();
                }
            }
        }
        finally
        {
            _currentPlayInfo = null;
            IsPlaying = false;
        }
    }
}
