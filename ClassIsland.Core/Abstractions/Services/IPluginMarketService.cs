using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Shared;
using ClassIsland.Shared.ComponentModels;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;

namespace ClassIsland.Core.Abstractions.Services;

/// <summary>
/// 插件市场服务。
/// </summary>
public interface IPluginMarketService : INotifyPropertyChanged
{
    /// <summary>
    /// 已将插件仓库与本地插件合并的全部插件
    /// </summary>
    public ObservableDictionary<string, PluginInfo> MergedPlugins { get; }

    /// <summary>
    /// 是否正在加载插件源
    /// </summary>
    public bool IsLoadingPluginSource { get; set; }

    /// <summary>
    /// 插件源加载进度
    /// </summary>
    public double PluginSourceDownloadProgress { get; set; }

    /// <summary>
    /// 插件源加载异常
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// 刷新插件源。
    /// </summary>
    public Task RefreshPluginSourceAsync();

    /// <summary>
    /// 请求下载插件
    /// </summary>
    /// <param name="id">要下载的插件id</param>
    public void RequestDownloadPlugin(string id);

    /// <summary>
    /// 解析商店插件信息
    /// </summary>
    /// <param name="id">插件 ID</param>
    /// <returns>解析的插件信息，如果未找到对应 ID 的插件，将返回 null</returns>
    public PluginIndexItem? ResolveMarketPlugin(string id);

    /// <summary>
    /// 请求重启事件
    /// </summary>
    public event EventHandler? RestartRequested;

    /// <summary>
    /// 重载本地插件源
    /// </summary>
    public void LoadPluginSource();

    /// <summary>
    /// 已加载的插件源
    /// </summary>
    public ObservableDictionary<string, PluginIndex> Indexes { get; }

    /// <summary>
    /// 获取索引信息
    /// </summary>
    /// <returns>获取到的索引信息</returns>
    public IEnumerable<PluginIndexInfo> GetIndexInfos();

    /// <summary>
    /// 更新全部插件。
    /// </summary>
    public void UpdateAllPlugins(bool discardDisabled=false);

    /// <summary>
    /// 请求探测单个市场插件的「原生支持 / 需要兼容层」状态。
    /// </summary>
    /// <remarks>
    /// 探测需要下载插件包以读取入口程序集，属于网络操作。由插件列表在容器准备时逐个触发，
    /// 只有用户实际滚到的插件才会被探测。
    /// </remarks>
    /// <param name="plugin">要探测的市场插件。</param>
    public void RequestCompatibilityProbe(PluginInfo plugin);
}