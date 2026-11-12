namespace ClassIsland.Core.Enums;

/// <summary>
/// 表示插件与当前宿主 Avalonia 版本的二进制兼容状态。
/// </summary>
public enum PluginCompatibilityMode
{
    /// <summary>
    /// 尚未检测。通常表示插件只存在于插件市场，本地没有可分析的程序集。
    /// </summary>
    Unknown,

    /// <summary>
    /// 插件按当前（或更新的）Avalonia 版本编译，可以原生加载，不需要兼容层介入。
    /// </summary>
    Native,

    /// <summary>
    /// 插件按旧版 Avalonia 编译，加载时由兼容层改写了类型引用或方法调用。
    /// </summary>
    CompatibilityLayer,
}
