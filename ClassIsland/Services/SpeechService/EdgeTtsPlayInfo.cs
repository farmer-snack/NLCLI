using System.Threading;
using System.Threading.Tasks;

namespace ClassIsland.Services.SpeechService;

public class EdgeTtsPlayInfo(string text, string filePath, CancellationTokenSource cts, Task<bool>? downloadTask)
{
    /// <summary>
    /// 本条队列项要朗读的原始文本，用于 EdgeTTS 失败时回退到系统 TTS。
    /// </summary>
    public string Text { get; } = text;
    public string FilePath { get; } = filePath;
    public CancellationTokenSource CancellationTokenSource { get; } = cts;
    public Task<bool>? DownloadTask { get; } = downloadTask;
    public bool IsPlayingCompleted { get; set; } = false;
}
