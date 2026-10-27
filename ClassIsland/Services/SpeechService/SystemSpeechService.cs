using System.Linq;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Abstraction.Services;

using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.SpeechService;

[SpeechProviderInfo("classisland.speech.system", "系统 TTS")]
[SupportedOSPlatform("windows")]
public class SystemSpeechService : ISpeechService
{
    private ILogger<SystemSpeechService> Logger { get; } = App.GetService<ILogger<SystemSpeechService>>();

    private SettingsService SettingsService { get; } = App.GetService<SettingsService>();

    private SpeechSynthesizer SpeechSynthesizer { get; } = new();

    public SystemSpeechService()
    {
        // 优先使用中文语音，避免系统默认音色用英文发音读中文。
        try
        {
            var chineseVoice = SpeechSynthesizer.GetInstalledVoices()
                .FirstOrDefault(x => x.Enabled &&
                                     x.VoiceInfo.Culture.Name.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase));
            if (chineseVoice != null)
            {
                SpeechSynthesizer.SelectVoice(chineseVoice.VoiceInfo.Name);
                Logger.LogInformation("系统 TTS 使用中文语音：{}", chineseVoice.VoiceInfo.Name);
            }
            else
            {
                Logger.LogWarning("系统 TTS 没有找到中文语音，将使用默认语音。");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning(ex, "选择系统 TTS 中文语音失败，将使用默认语音。");
        }

        SpeechSynthesizer.SetOutputToDefaultAudioDevice();
        Logger.LogInformation("初始化了系统TTS服务。");
    }

    public void EnqueueSpeechQueue(string text)
    {
        Logger.LogInformation("朗读文本：{}", text);
        SpeechSynthesizer.Volume = (int)(SettingsService.Settings.SpeechVolume * 100);
        SpeechSynthesizer.SpeakAsync(text);
    }

    public void ClearSpeechQueue()
    {
        SpeechSynthesizer.SpeakAsyncCancelAll();
    }
}