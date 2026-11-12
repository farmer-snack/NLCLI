using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Models.Marketplace;
using ClassIsland.Core.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.Core.Models.Plugin;

/// <summary>
/// 插件信息
/// </summary>
public class PluginInfo : ObservableRecipient, IMarketplaceItemInfo
{
    private DownloadProgress? _downloadProgress;
    private bool _isAvailableOnMarket = false;
    private PluginManifest _manifest = new();
    private bool _restartRequired = false;
    private bool _isUpdateAvailable = false;
    private long _downloadCount = 0;
    private long _starsCount = 0;
    private bool _isNotSupportCurrentOS = false;
    private string _realIconPath = "";
    private PluginCompatibilityMode _compatibilityMode = PluginCompatibilityMode.Unknown;

    /// <summary>
    /// 插件元数据
    /// </summary>
    public PluginManifest Manifest
    {
        get => _manifest;
        set
        {
            if (Equals(value, _manifest)) return;
            _manifest = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件是否存在于本地
    /// </summary>
    [JsonIgnore]
    public bool IsLocal { get; internal set; } = false;

    /// <summary>
    /// 插件是否已启用
    /// </summary>
    [JsonIgnore]
    public bool IsEnabled
    {
        get
        {
            if (!IsLocal)
                return false;
            return !File.Exists(Path.Combine(PluginFolderPath, ".disabled"));
        }
        set
        {
            if (!IsLocal)
                throw new InvalidOperationException("无法为不存在本地的插件设置启用状态。");
            var path = Path.Combine(PluginFolderPath, ".disabled");
            RestartRequired = true;
            if (value)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, "");
            }
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件是否将要卸载
    /// </summary>
    [JsonIgnore]
    public bool IsUninstalling
    {
        get
        {
            if (!IsLocal)
                return false;
            return File.Exists(Path.Combine(PluginFolderPath, ".uninstall"));
        }
        set
        {
            if (!IsLocal)
                throw new InvalidOperationException("无法为不存在本地的插件设置将要卸载状态。");
            var path = Path.Combine(PluginFolderPath, ".uninstall");
            if (value)
            {
                File.WriteAllText(path, "");
            }
            else
            {
                File.Delete(path);
            }   
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件文件路径。
    /// </summary>
    [JsonIgnore]
    public string PluginFolderPath { get; internal set; } = "";

    /// <summary>
    /// 图标真实路径
    /// </summary>
    public string RealIconPath
    {
        get => _realIconPath;
        set
        {
            if (value == _realIconPath) return;
            _realIconPath = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件加载时错误
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; internal set; }

    /// <summary>
    /// 兼容层在加载此插件时做出的调整记录（程序集重定向、依赖兜底等）。
    /// </summary>
    /// <remarks>
    /// 正常情况下这个列表是空的。只有插件包内自带了旧版 ClassIsland 核心程序集、
    /// 或缺少 <c>deps.json</c> 等情况才会有内容，用于向用户说明「为什么这个插件本来跑不起来，现在能跑了」。
    /// </remarks>
    [JsonIgnore]
    public ObservableCollection<string> CompatibilityNotes { get; } = [];

    /// <summary>
    /// 兼容层是否对该插件做过调整。
    /// </summary>
    [JsonIgnore]
    public bool HasCompatibilityNotes => CompatibilityNotes.Count > 0;

    /// <summary>
    /// 插件与当前宿主 Avalonia 版本的二进制兼容状态。
    /// </summary>
    /// <remarks>
    /// <see cref="PluginCompatibilityMode.Unknown"/> 表示还没检测过（常见于只存在于插件市场、
    /// 本地没有程序集的插件）。已安装插件在加载时由兼容层写入实际结果。
    /// </remarks>
    [JsonIgnore]
    public PluginCompatibilityMode CompatibilityMode
    {
        get => _compatibilityMode;
        internal set
        {
            if (value == _compatibilityMode) return;
            _compatibilityMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNativeCompatible));
            OnPropertyChanged(nameof(IsUsingCompatibilityLayer));
            OnPropertyChanged(nameof(IsCompatibilityUnknown));
        }
    }

    /// <summary>
    /// 插件是否原生兼容当前宿主，不需要兼容层介入。
    /// </summary>
    [JsonIgnore]
    public bool IsNativeCompatible => CompatibilityMode == PluginCompatibilityMode.Native;

    /// <summary>
    /// 插件是否需要经过兼容层转译才能在当前宿主上加载。
    /// </summary>
    [JsonIgnore]
    public bool IsUsingCompatibilityLayer => CompatibilityMode == PluginCompatibilityMode.CompatibilityLayer;

    /// <summary>
    /// 插件的兼容状态是否尚未确定。
    /// </summary>
    [JsonIgnore]
    public bool IsCompatibilityUnknown => CompatibilityMode == PluginCompatibilityMode.Unknown;

    /// <summary>
    /// 记录一条兼容层调整。重复的记录会被忽略。
    /// </summary>
    /// <remarks>
    /// 程序集是按需加载的，可能在后台线程触发，因此这里统一切回 UI 线程再更新集合，
    /// 避免绑定到界面的 <see cref="ObservableCollection{T}"/> 被跨线程修改导致崩溃。
    /// </remarks>
    public void AddCompatibilityNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        void Append()
        {
            if (CompatibilityNotes.Contains(note))
            {
                return;
            }

            // 兼容层可能产生较多条目，限制总量避免把插件详情页刷满。
            if (CompatibilityNotes.Count >= 40)
            {
                return;
            }

            CompatibilityNotes.Add(note);
            OnPropertyChanged(nameof(HasCompatibilityNotes));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Append();
        }
        else
        {
            Dispatcher.UIThread.Post(Append);
        }
    }

    /// <summary>
    /// 插件加载状态
    /// </summary>
    [JsonIgnore]
    public PluginLoadStatus LoadStatus { get; internal set; } = PluginLoadStatus.NotLoaded;

    /// <inheritdoc />
    [JsonIgnore]
    public IMarketplaceItemManifest ManifestReadonly => Manifest;

    /// <summary>
    /// 是否在插件市场上可用
    /// </summary>
    [JsonIgnore]
    public bool IsAvailableOnMarket
    {
        get => _isAvailableOnMarket;
        set
        {
            if (value == _isAvailableOnMarket) return;
            _isAvailableOnMarket = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 关联的下载进度
    /// </summary>
    [JsonIgnore]
    public DownloadProgress? DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (Equals(value, _downloadProgress)) return;
            _downloadProgress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 需要重启
    /// </summary>
    [JsonIgnore]
    public bool RestartRequired
    {
        get => _restartRequired;
        set
        {
            if (value == _restartRequired) return;
            _restartRequired = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件是否有更新可用。
    /// </summary>
    [JsonIgnore]
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (value == _isUpdateAvailable) return;
            _isUpdateAvailable = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件是否支持当前的操作系统。
    /// </summary>
    [JsonIgnore]
    public bool IsNotSupportCurrentOS
    {
        get => _isNotSupportCurrentOS;
        set
        {
            if (value == _isNotSupportCurrentOS) return;
            _isNotSupportCurrentOS = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件下载量
    /// </summary>
    public long DownloadCount
    {
        get => _downloadCount;
        set
        {
            if (value == _downloadCount) return;
            _downloadCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 插件 Stars 数量
    /// </summary>
    public long StarsCount
    {
        get => _starsCount;
        set
        {
            if (value == _starsCount) return;
            _starsCount = value;
            OnPropertyChanged();
        }
    }
}