using Avalonia.Controls.Primitives;

namespace ClassIsland.Core.Controls;

/// <summary>
/// 开发构建装饰层。
/// </summary>
/// <remarks>
/// 兼容层：本地分支已经移除了开发构建水印的显示逻辑，此类型仅为引用它的插件保留。
/// 它仍然可以正常实例化并加入视觉树，但不会绘制任何内容。
/// </remarks>
[Obsolete("本地分支已移除开发构建水印，此类型仅为兼容旧插件保留，不会绘制任何内容。")]
public class DevelopmentBuildAdorner : TemplatedControl
{
    /// <summary>
    /// 是否是开发构建
    /// </summary>
    public bool IsDevelopmentBuild { get; }

    /// <summary>
    /// 是否显示开源水印
    /// </summary>
    public bool ShowOssWatermark { get; }

    /// <summary>
    /// 创建开发构建装饰层。
    /// </summary>
    /// <param name="isDevelopmentBuild">是否是开发构建。</param>
    /// <param name="showOssWatermark">是否显示开源水印。</param>
    public DevelopmentBuildAdorner(bool isDevelopmentBuild, bool showOssWatermark)
    {
        IsDevelopmentBuild = isDevelopmentBuild;
        ShowOssWatermark = showOssWatermark;
        ClipToBounds = false;
    }
}
