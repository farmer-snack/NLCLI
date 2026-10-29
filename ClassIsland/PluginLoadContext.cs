using System.Reflection;
using System.Runtime.Loader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Services;

namespace ClassIsland;

/// <summary>
/// 为插件加载提供隔离的 <see cref="AssemblyLoadContext"/> 实现。<para/>
/// 根据运行平台选择不同的依赖解析器，并负责从插件目录解析托管与非托管依赖项。
/// <remarks>macOS 和 Android 平台的依赖解析器为 <see cref="MonoPluginAssemblyResolver"/>。</remarks>
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly bool _forceMonoPluginLoader;

    public PluginLoadContext(PluginInfo info, string fullPath, bool forceMonoPluginLoader) : base($"ClassIsland.PluginLoadContext[{info.Manifest.Id}]")
    {
        _forceMonoPluginLoader = forceMonoPluginLoader;
        Info = info;
        PluginDirectory = Path.GetDirectoryName(fullPath) ?? "";
        CoreResolver = UseMonoPluginLoadingBehavior ? null : new(fullPath);
        MonoResolver = UseMonoPluginLoadingBehavior ? new(fullPath) : null;
    }

    /// <summary>
    /// 插件信息与清单引用，用于根据声明的依赖项查找并委托到其它插件的加载上下文。
    /// </summary>
    public PluginInfo Info { get; }

    private bool UseMonoPluginLoadingBehavior =>
        _forceMonoPluginLoader || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid();

    private AssemblyDependencyResolver? CoreResolver { get; }
    
    private MonoPluginAssemblyResolver? MonoResolver { get; }

    /// <summary>
    /// 插件所在目录，用于在依赖解析器失效时按文件名兜底查找。
    /// </summary>
    private string PluginDirectory { get; }

    /// <summary>
    /// 兼容层：属于 ClassIsland 自身的程序集。这些程序集必须绑定到宿主已加载的实例，
    /// 不能使用插件目录里的同名副本。否则插件里的类型与宿主里的类型不是同一个类型，
    /// 注册组件、判断接口时会静默失效（典型症状：插件显示已启用但功能完全不出现）。
    /// </summary>
    private static readonly string[] HostAssemblyNames =
    [
        "ClassIsland",
        "ClassIsland.Desktop",
        "ClassIsland.Core",
        "ClassIsland.Shared",
        "ClassIsland.Shared.IPC",
        "ClassIsland.Platforms.Abstractions"
    ];

    /// <summary>
    /// 兼容层：必须以宿主版本为准的框架程序集前缀。
    /// </summary>
    /// <remarks>
    /// 插件作者通常直接 <c>dotnet publish</c> 后把整个输出目录打成插件包，里面会带上 Avalonia 等框架程序集。
    /// 一旦插件在自己的加载上下文里再加载一份 Avalonia，插件创建的控件与宿主控件就是两套完全不同的类型，
    /// 加入视觉树时会抛类型转换异常，或者更隐蔽地「什么也不显示」。
    /// </remarks>
    private static readonly string[] SharedFrameworkPrefixes =
    [
        "Avalonia",
        "CommunityToolkit.Mvvm",
        "Microsoft.Extensions."
    ];

    private static IReadOnlyList<string> WinRTDeps { get; } = [
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET"
    ];

    /// <summary>
    /// 判断某个程序集是否应该优先使用宿主已经加载的版本。
    /// </summary>
    private static bool ShouldBindToHost(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        if (HostAssemblyNames.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return SharedFrameworkPrefixes.Any(prefix =>
            assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断某个程序集是否「绝对不能」使用插件目录里的副本。
    /// </summary>
    /// <remarks>
    /// ClassIsland 自身的程序集和 Avalonia 一旦出现两份，插件和宿主之间的类型就会分裂，
    /// 表现为插件加载成功但完全不工作。剩下的框架库（如 Microsoft.Extensions.*）即使复制一份也不会造成类型分裂，
    /// 宿主没有加载时允许继续按插件自带的副本加载，避免反过来把本来能跑的插件弄坏。
    /// </remarks>
    private static bool MustUseHostCopy(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        if (HostAssemblyNames.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return assemblyName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在需要加载程序集时被调用。优先从已加载的插件依赖项上下文中解析，如果在插件目录中找到对应的程序集则从路径加载。
    /// 对 WinRT 相关依赖会使用宿主的实现。
    /// </summary>
    /// <remarks>
    /// 兼容策略（按顺序）：
    /// <list type="number">
    /// <item>ClassIsland 自身程序集与 Avalonia 等框架程序集一律复用宿主实例，忽略插件要求的版本号与插件目录里的副本。</item>
    /// <item>插件清单里声明的依赖插件，委托给对应插件的加载上下文。</item>
    /// <item>按插件自带的 <c>deps.json</c> 解析（原有逻辑）。</item>
    /// <item>解析失败时退化为「插件目录下同名 dll」，兼容没有 <c>deps.json</c> 的打包方式。</item>
    /// <item>仍然找不到、但宿主已经加载了同名程序集时，复用宿主实例，避免插件因缺依赖直接抛出 <see cref="FileNotFoundException"/>。</item>
    /// </list>
    /// </remarks>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (WinRTDeps.Contains(assemblyName.Name))
        {
            // 为了防止因引用 WinRT 依赖导致重复初始化 WinRT 相关运行时使应用代码无法正常调用 WinRT，
            // 这里将插件要的加载的 WinRT 相关程序集替换为应用自带的 WinRT 相关程序集。
            return null;
        }

        // ① 兼容层：宿主自身程序集与框架程序集，优先绑定到宿主版本。
        if (ShouldBindToHost(assemblyName.Name))
        {
            var hostAssembly = FindLoadedAssembly(assemblyName.Name);
            if (hostAssembly != null)
            {
                if (HasLocalCopy(assemblyName.Name))
                {
                    Info.AddCompatibilityNote(
                        $"程序集 {assemblyName.Name} {assemblyName.Version} 已重定向到宿主版本 {hostAssembly.GetName().Version}（插件包内自带了副本，已忽略）。");
                }

                return hostAssembly;
            }

            // 宿主尚未加载：
            // - ClassIsland.* / Avalonia*：绝不能使用插件目录里的副本，交给运行时按默认规则解析；
            // - 其余框架库：继续往下走，允许使用插件自带的副本。
            if (MustUseHostCopy(assemblyName.Name))
            {
                return null;
            }
        }

        // 尝试查找依赖
        foreach (var dep in Info.Manifest.Dependencies)
        {
            if (!PluginService.PluginLoadContexts.TryGetValue(dep.Id, out var context))
            {
                continue;
            }

            var assembly = context.Load(assemblyName);
            if (assembly != null)
            {
                return assembly;
            }
        }
        
        string? assemblyPath;
        assemblyPath = UseMonoPluginLoadingBehavior ? MonoResolver?.ResolveAssemblyToPath(assemblyName) : CoreResolver?.ResolveAssemblyToPath(assemblyName);
        
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        // ④ 兼容层：没有 deps.json 或解析器失效时，直接按文件名在插件目录里找。
        if (!string.IsNullOrEmpty(assemblyName.Name))
        {
            var fallbackPath = Path.Combine(PluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(fallbackPath))
            {
                Info.AddCompatibilityNote(
                    $"程序集 {assemblyName.Name} 未在 deps.json 中声明，已从插件目录直接加载。");
                return LoadFromAssemblyPath(fallbackPath);
            }
        }

        // ⑤ 兼容层：插件没有打包这个依赖，但宿主已经加载了同名程序集（多为框架类库），复用宿主实例。
        var loadedAssembly = FindLoadedAssembly(assemblyName.Name);
        if (loadedAssembly != null)
        {
            Info.AddCompatibilityNote(
                $"程序集 {assemblyName.Name} 插件包内未提供，已复用宿主版本 {loadedAssembly.GetName().Version}。");
        }

        return loadedAssembly;
    }

    /// <summary>
    /// 判断插件目录里是否带了同名程序集副本。
    /// </summary>
    private bool HasLocalCopy(string? assemblyName)
    {
        return !string.IsNullOrEmpty(assemblyName) &&
               File.Exists(Path.Combine(PluginDirectory, assemblyName + ".dll"));
    }

    /// <summary>
    /// 在宿主（默认加载上下文）中查找已加载的同名程序集。找不到返回 <see langword="null"/>。
    /// </summary>
    private static Assembly? FindLoadedAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return null;
        }

        return Default.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析并加载插件的非托管（本地）库，按平台和插件目录的约定搜索文件。
    /// 返回非托管库句柄，找不到则返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath;
        libraryPath = UseMonoPluginLoadingBehavior ? MonoResolver?.ResolveUnmanagedDllToPath(unmanagedDllName) : CoreResolver?.ResolveUnmanagedDllToPath(unmanagedDllName);

        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        // 兼容层：插件没有 deps.json 或解析器失效时，按约定目录逐个查找原生库。
        return LoadUnmanagedDllFromFallback(unmanagedDllName);
    }

    /// <summary>
    /// 兼容层：在插件目录与其 <c>runtimes</c> 子目录中查找非托管库。
    /// </summary>
    /// <remarks>
    /// 有些插件包是直接把 <c>dotnet publish</c> 的输出打进去的，缺少 <c>deps.json</c>，
    /// <see cref="AssemblyDependencyResolver"/> 无法解析它们的原生库，
    /// 会导致插件一调用到原生依赖就崩溃（典型症状：麦克风、TTF、播放器类插件直接闪退）。
    /// </remarks>
    private IntPtr LoadUnmanagedDllFromFallback(string unmanagedDllName)
    {
        var platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => null
        };

        var searchDirectories = new List<string> { PluginDirectory };
        var platformNativeDirectory = Path.Combine(PluginDirectory, "runtimes", platform, "native");
        searchDirectories.Add(platformNativeDirectory);
        if (architecture != null)
        {
            searchDirectories.Add(Path.Combine(PluginDirectory, "runtimes", $"{platform}-{architecture}", "native"));
        }

        var fileNames = new List<string> { unmanagedDllName };
        if (OperatingSystem.IsWindows())
        {
            if (!unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                fileNames.Add(unmanagedDllName + ".dll");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            fileNames.Add($"lib{unmanagedDllName}.dylib");
            fileNames.Add($"{unmanagedDllName}.dylib");
        }
        else
        {
            fileNames.Add($"lib{unmanagedDllName}.so");
            fileNames.Add($"{unmanagedDllName}.so");
        }

        foreach (var directory in searchDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    return LoadUnmanagedDllFromPath(path);
                }
            }
        }

        return IntPtr.Zero;
    }
}

/// <summary>
/// Mono 运行时专用的插件程序集与本地库解析器。根据 macOS 或 Android 的插件目录结构查找真实文件路径。
/// </summary>
public class MonoPluginAssemblyResolver(string componentAssemblyPath)
{
    private readonly string _pluginDirectory = Path.GetDirectoryName(componentAssemblyPath) ?? "";

    /// <summary>
    /// 将程序集名解析为插件目录下的 dll 文件路径（若存在），否则返回 null。
    /// </summary>
    public string? ResolveAssemblyToPath(AssemblyName assemblyName)
    {
        var dllPath = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (File.Exists(dllPath))
            return dllPath;
        return null;
    }

    /// <summary>
    /// 在插件目录及当前平台对应的 runtimes 子目录中查找非托管库文件，并返回第一个匹配的完整路径。
    /// 支持 macOS 和 Android 的平台通用目录与处理器架构目录。
    /// </summary>
    public string? ResolveUnmanagedDllToPath(string unmanagedDllName)
    {
        var searchPaths = new List<string>
        {
            _pluginDirectory
        };

        var platform = OperatingSystem.IsMacOS()
            ? "osx"
            : OperatingSystem.IsAndroid()
                ? "android"
                : null;

        var extension = OperatingSystem.IsMacOS()
            ? ".dylib"
            : OperatingSystem.IsAndroid()
                ? ".so"
                : null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 when OperatingSystem.IsAndroid() => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm when OperatingSystem.IsAndroid() => "arm",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        if (platform != null)
        {
            searchPaths.Add(Path.Combine(_pluginDirectory, "runtimes", platform, "native"));
            if (arch != null)
            {
                searchPaths.Add(Path.Combine(_pluginDirectory, "runtimes", $"{platform}-{arch}", "native"));
            }
        }

        foreach (var path in searchPaths)
        {
            if (!Directory.Exists(path)) continue;

            if (extension != null)
            {
                var prefixedPath = Path.Combine(path, $"lib{unmanagedDllName}{extension}");
                if (File.Exists(prefixedPath)) return prefixedPath;

                var platformPath = Path.Combine(path, $"{unmanagedDllName}{extension}");
                if (File.Exists(platformPath)) return platformPath;
            }

            var rawPath = Path.Combine(path, unmanagedDllName);
            if (File.Exists(rawPath)) return rawPath;
        }

        return null;
    }
}
