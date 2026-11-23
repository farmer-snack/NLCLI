using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

/// <summary>
/// 原生依赖缺失的类型，决定采用哪种修复方式。
/// </summary>
public enum NativeDependencyKind
{
    /// <summary>
    /// Microsoft Visual C++ 运行库（VCRUNTIME140、MSVCP140、UCRT 等）。可以从微软官方下载安装。
    /// </summary>
    VisualCppRuntime,

    /// <summary>
    /// .NET 运行时组件缺失。正常情况下不会出现，因为应用本身就跑在 .NET 上。
    /// </summary>
    DotNetRuntime,

    /// <summary>
    /// Windows 系统组件缺失。通常说明系统本身有问题，无法由应用自动修复。
    /// </summary>
    SystemComponent,

    /// <summary>
    /// 随 NuGet 包分发的原生库（例如 miniaudio.dll）缺失。可以从 nuget.org 还原。
    /// </summary>
    ApplicationNative,

    /// <summary>
    /// 无法归类的依赖。
    /// </summary>
    Unknown
}

/// <summary>
/// 一条原生依赖问题。
/// </summary>
/// <param name="Kind">问题类型，决定修复方式。</param>
/// <param name="FilePath">相对应用根目录的文件路径。缺失原生库本身时，这是期望的文件位置。</param>
/// <param name="MissingModule">缺失的模块名；如果是文件本身缺失，则为文件名。</param>
/// <param name="Description">用于界面展示的说明。</param>
/// <param name="PackageId">来源 NuGet 包 ID，仅 <see cref="NativeDependencyKind.ApplicationNative"/> 有值。</param>
/// <param name="PackageVersion">来源 NuGet 包版本。</param>
/// <param name="AssetPath">包内资源相对路径（runtimes/&lt;rid&gt;/native/xxx.dll）。</param>
public sealed record NativeDependencyIssue(
    NativeDependencyKind Kind,
    string FilePath,
    string? MissingModule,
    string Description,
    string? PackageId = null,
    string? PackageVersion = null,
    string? AssetPath = null);

/// <summary>
/// 一次原生依赖扫描的结果。
/// </summary>
/// <param name="Issues">发现的问题。</param>
/// <param name="ScannedFileCount">实际检查过的原生库数量。</param>
/// <param name="BaseDirectory">扫描的应用根目录。</param>
public sealed record NativeDependencyReport(
    IReadOnlyList<NativeDependencyIssue> Issues,
    int ScannedFileCount,
    string BaseDirectory)
{
    public bool IsHealthy => Issues.Count == 0;
}

/// <summary>
/// 一次自动修复的结果。
/// </summary>
/// <param name="RestoredFileCount">从 NuGet 还原的文件数量。</param>
/// <param name="VisualCppRuntimeInstalled">是否执行了 VC++ 运行库安装。</param>
/// <param name="RemainingIssues">修复后仍然存在的问题。</param>
/// <param name="Failure">修复过程中出现的第一个错误，没有错误时为 null。</param>
public sealed record NativeDependencyRepairResult(
    int RestoredFileCount,
    bool VisualCppRuntimeInstalled,
    IReadOnlyList<NativeDependencyIssue> RemainingIssues,
    string? Failure)
{
    public bool Succeeded => Failure == null && RemainingIssues.Count == 0;
}

/// <summary>
/// 极简 PE 导入表读取器。只读取导入模块名，不加载目标 DLL，避免触发 DllMain 副作用。
/// </summary>
internal static class PeImportReader
{
    private const ushort DosSignature = 0x5A4D;
    private const uint PeSignature = 0x00004550;
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    public static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 0x40)
            {
                return false;
            }

            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            return reader.ReadUInt16() == DosSignature;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一次性读取文件的关键信息：是否为托管程序集，以及原生导入的模块列表。
    /// 每个文件只打开一次，避免在启动路径上反复读取同一份头部。
    /// </summary>
    public static bool TryInspect(string path, out bool isManaged, out IReadOnlyList<string> imports)
    {
        isManaged = false;
        imports = Array.Empty<string>();
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            if (!TryReadLayout(reader, stream, out var dataDirectoryOffset, out var sections))
            {
                return false;
            }

            isManaged = HasCliHeader(reader, stream, dataDirectoryOffset);
            imports = ReadImports(reader, stream, dataDirectoryOffset, sections);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断文件是不是托管程序集。托管 DLL 也有导入表（通常只有 mscoree.dll），
    /// 但它们的依赖由 .NET 运行时负责解析，不应该参与原生依赖检查。
    /// </summary>
    public static bool IsManagedAssembly(string path) =>
        TryInspect(path, out var isManaged, out _) && isManaged;

    public static IReadOnlyList<string> ReadImportedModules(string path) =>
        TryInspect(path, out _, out var imports) ? imports : Array.Empty<string>();

    private static bool HasCliHeader(BinaryReader reader, Stream stream, long dataDirectoryOffset)
    {
        // 第 15 项（索引 14）是 CLR 运行时头，非零即托管程序集。
        var cliDirectoryOffset = dataDirectoryOffset + 14 * 8;
        if (cliDirectoryOffset + 8 > stream.Length)
        {
            return false;
        }

        stream.Position = cliDirectoryOffset;
        var cliHeaderRva = reader.ReadUInt32();
        var cliHeaderSize = reader.ReadUInt32();
        return cliHeaderRva != 0 && cliHeaderSize != 0;
    }

    private static IReadOnlyList<string> ReadImports(BinaryReader reader, Stream stream,
        long dataDirectoryOffset, List<Section> sections)
    {
        stream.Position = dataDirectoryOffset + 8; // 第 2 项是导入表
        var importDirectoryRva = reader.ReadUInt32();
        var importDirectorySize = reader.ReadUInt32();
        if (importDirectoryRva == 0 || importDirectorySize == 0)
        {
            return Array.Empty<string>();
        }

        var descriptorOffset = RvaToOffset(importDirectoryRva, sections, stream.Length);
        if (descriptorOffset == null)
        {
            return Array.Empty<string>();
        }

        var modules = new List<string>();
        stream.Position = descriptorOffset.Value;
        while (stream.Position + 20 <= stream.Length)
        {
            var originalFirstThunk = reader.ReadUInt32();
            reader.ReadUInt32(); // TimeDateStamp
            reader.ReadUInt32(); // ForwarderChain
            var nameRva = reader.ReadUInt32();
            reader.ReadUInt32(); // FirstThunk

            // 最后一个描述符全为 0；Name 为 0 也说明没有更多有效项了。
            if ((originalFirstThunk == 0 && nameRva == 0) || nameRva == 0)
            {
                break;
            }

            var nameOffset = RvaToOffset(nameRva, sections, stream.Length);
            if (nameOffset == null)
            {
                continue;
            }

            var resumePosition = stream.Position;
            stream.Position = nameOffset.Value;
            var builder = new StringBuilder(64);
            while (stream.Position < stream.Length && builder.Length < 260)
            {
                var value = reader.ReadByte();
                if (value == 0)
                {
                    break;
                }

                builder.Append((char)value);
            }

            stream.Position = resumePosition;

            var moduleName = builder.ToString();
            if (moduleName.Length > 0 &&
                !modules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
            {
                modules.Add(moduleName);
            }
        }

        return modules;
    }

    /// <summary>
    /// 读取 PE 头部布局：数据目录位置与节表。不是有效 PE 时返回 false。
    /// </summary>
    private static bool TryReadLayout(BinaryReader reader, Stream stream, out long dataDirectoryOffset,
        out List<Section> sections)
    {
        dataDirectoryOffset = 0;
        sections = [];

        if (stream.Length < 0x40)
        {
            return false;
        }

        stream.Position = 0;
        if (reader.ReadUInt16() != DosSignature)
        {
            return false;
        }

        stream.Position = 0x3C;
        var peHeaderOffset = reader.ReadInt32();
        if (peHeaderOffset <= 0 || peHeaderOffset + 24L > stream.Length)
        {
            return false;
        }

        stream.Position = peHeaderOffset;
        if (reader.ReadUInt32() != PeSignature)
        {
            return false;
        }

        reader.ReadUInt16(); // Machine
        var sectionCount = reader.ReadUInt16();
        reader.ReadUInt32(); // TimeDateStamp
        reader.ReadUInt32(); // PointerToSymbolTable
        reader.ReadUInt32(); // NumberOfSymbols
        var optionalHeaderSize = reader.ReadUInt16();
        reader.ReadUInt16(); // Characteristics

        var optionalHeaderStart = stream.Position;
        if (optionalHeaderStart + optionalHeaderSize > stream.Length)
        {
            return false;
        }

        var magic = reader.ReadUInt16();
        bool isPe32Plus;
        switch (magic)
        {
            case Pe32Magic:
                isPe32Plus = false;
                break;
            case Pe32PlusMagic:
                isPe32Plus = true;
                break;
            default:
                return false;
        }

        // 数据目录紧跟在可选头的固定字段之后：PE32 是 96 字节，PE32+ 是 112 字节。
        dataDirectoryOffset = optionalHeaderStart + (isPe32Plus ? 112 : 96);
        if (dataDirectoryOffset + 16 > stream.Length)
        {
            return false;
        }

        var sectionHeadersOffset = optionalHeaderStart + optionalHeaderSize;
        if (sectionHeadersOffset + sectionCount * 40L > stream.Length)
        {
            return false;
        }

        sections = new List<Section>(sectionCount);
        stream.Position = sectionHeadersOffset;
        for (var i = 0; i < sectionCount; i++)
        {
            reader.ReadBytes(8); // Name
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32();
            var rawPointer = reader.ReadUInt32();
            reader.ReadBytes(16); // 重定位、行号与节属性
            sections.Add(new Section(virtualAddress, virtualSize, rawPointer, rawSize));
        }

        return true;
    }

    private static long? RvaToOffset(uint rva, List<Section> sections, long fileLength)
    {
        foreach (var section in sections)
        {
            var size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + size)
            {
                continue;
            }

            var offset = section.RawPointer + (rva - section.VirtualAddress);
            return offset < fileLength ? offset : null;
        }

        return rva < fileLength ? rva : null;
    }
}

/// <summary>
/// 原生依赖扫描器。检查 runtimes 目录下原生库是否齐全，并解析它们的导入表，
/// 确认依赖的系统运行库在目标机器上确实存在。
/// </summary>
public static class NativeDependencyProbe
{
    private const uint LoadLibraryAsDataFile = 0x00000002;

    private static readonly HashSet<string> SystemModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32.dll", "kernelbase.dll", "ntdll.dll", "user32.dll", "gdi32.dll", "gdi32full.dll",
        "advapi32.dll", "shell32.dll", "shlwapi.dll", "ole32.dll", "oleaut32.dll", "combase.dll",
        "comctl32.dll", "comdlg32.dll", "ws2_32.dll", "winmm.dll", "dbghelp.dll", "psapi.dll",
        "version.dll", "setupapi.dll", "imm32.dll", "uxtheme.dll", "dwmapi.dll", "d3d11.dll",
        "dxgi.dll", "opengl32.dll", "winhttp.dll", "wininet.dll", "crypt32.dll", "bcrypt.dll",
        "secur32.dll", "userenv.dll", "netapi32.dll", "iphlpapi.dll", "dnsapi.dll", "mswsock.dll",
        "rpcrt4.dll", "shcore.dll", "powrprof.dll", "propsys.dll", "wtsapi32.dll", "avrt.dll",
        "mfplat.dll", "mf.dll", "mfuuid.dll", "strmiids.dll", "wldap32.dll", "normaliz.dll",
        "xinput1_4.dll", "xinput9_1_0.dll", "winspool.drv", "dsound.dll", "dinput8.dll",
        "msacm32.dll", "hid.dll", "cfgmgr32.dll", "winsta.dll", "dwrite.dll", "d2d1.dll",
        "msvfw32.dll", "oleacc.dll", "sechost.dll", "ncrypt.dll", "urlmon.dll", "slc.dll",
        "pdh.dll", "wevtapi.dll", "wintrust.dll", "imagehlp.dll", "authz.dll", "fltlib.dll",
        "msvcrt.dll", "fontsub.dll", "usp10.dll", "d3dcompiler_47.dll", "d3d9.dll", "d3d12.dll",
        "dcomp.dll", "dxva2.dll", "mfreadwrite.dll", "windowscodecs.dll", "uiautomationcore.dll"
    };

    /// <summary>
    /// 扫描原生库依赖。同步执行，调用方应放到后台线程。
    /// </summary>
    public static NativeDependencyReport Probe(string? baseDirectory = null)
    {
        var root = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var issues = new List<NativeDependencyIssue>();
        var rid = GetCurrentRuntimeIdentifier();
        var nativeAssetMap = LoadRuntimeAssetMap(root, rid);

        var scanTargets = new List<(string FullPath, string RelativePath, string? PackageId)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. deps.json 里声明的原生资产：先确认文件本身还在不在。
        foreach (var (assetPath, package) in nativeAssetMap)
        {
            var relativePath = assetPath.Replace('\\', '/');
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                var fileName = Path.GetFileName(relativePath);
                issues.Add(new NativeDependencyIssue(
                    NativeDependencyKind.ApplicationNative,
                    relativePath,
                    fileName,
                    $"缺少原生库 {fileName}（来自 NuGet 包 {package.Id} {package.Version}）。",
                    package.Id,
                    package.Version,
                    relativePath));
                continue;
            }

            if (seen.Add(fullPath))
            {
                scanTargets.Add((fullPath, relativePath, package.Id));
            }
        }

        // 2. 应用根目录下的原生库（有些包会直接平铺到输出目录）。
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (seen.Add(file))
                {
                    scanTargets.Add((file, Path.GetFileName(file), null));
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读时只保留 deps.json 能定位到的目标。
        }

        var localDirectories = new List<string> { root };
        var nativeDirectory = Path.Combine(root, "runtimes", rid, "native");
        if (Directory.Exists(nativeDirectory))
        {
            localDirectories.Add(nativeDirectory);
        }

        foreach (var target in scanTargets)
        {
            var directory = Path.GetDirectoryName(target.FullPath);
            if (!string.IsNullOrEmpty(directory) &&
                !localDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                localDirectories.Add(directory);
            }
        }

        // 3. 解析导入表，逐个确认依赖模块能不能在目标机器上找到。
        // 只统计真正参与导入表解析的原生库。scanTargets 里混入了大量托管程序集
        // （根目录下几百个 *.dll），它们会在下面被 isManaged 跳过，不应该计入
        // "已检查的原生库数量"，否则会出现"实际 179 个却显示检查了 392 个"的虚报。
        var scannedNativeCount = 0;
        foreach (var target in scanTargets)
        {
            // 托管程序集也会带导入表（mscoree.dll），但它们的依赖由 .NET 运行时解析，跳过。
            if (!PeImportReader.TryInspect(target.FullPath, out var isManaged, out var imports) || isManaged)
            {
                continue;
            }

            scannedNativeCount++;

            foreach (var module in imports)
            {
                if (string.Equals(module, Path.GetFileName(target.FullPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsModuleAvailable(module, localDirectories))
                {
                    continue;
                }

                var kind = Classify(module);
                issues.Add(new NativeDependencyIssue(
                    kind,
                    target.RelativePath,
                    module,
                    $"{target.RelativePath} 依赖的 {module} 在当前系统中不存在。"));
            }
        }

        // 同一个缺失模块经常被多个原生库同时依赖（例如 VCRUNTIME140.dll），
        // 按「问题类型 + 缺失模块」去重，避免界面上出现一长串重复项。
        var uniqueIssues = new List<NativeDependencyIssue>(issues.Count);
        var seenIssues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in issues)
        {
            var key = $"{issue.Kind}|{issue.MissingModule ?? issue.FilePath}";
            if (seenIssues.Add(key))
            {
                uniqueIssues.Add(issue);
            }
        }

        return new NativeDependencyReport(uniqueIssues, scannedNativeCount, root);
    }

    /// <summary>
    /// 推断当前进程对应的运行时标识符，用于定位 runtimes 下的原生库目录。
    /// </summary>
    public static string GetCurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win-{architecture}";
        }

        return OperatingSystem.IsMacOS() ? $"osx-{architecture}" : $"linux-{architecture}";
    }

    /// <summary>
    /// 判断依赖模块在「冷启动后」是否真的可用。
    /// </summary>
    /// <remarks>
    /// 关键陷阱：本检查是在主程序正在运行时执行的，此时几乎所有原生库都已经被加载进
    /// 进程内存。若直接调用 <c>LoadLibraryExW(module)</c> 按名字判断，Windows 会命中
    /// 这些「内存残影」——即使磁盘上的文件已经被删除，也照样返回成功。这正是
    /// 「检查显示通过、关掉主程序再重启就立刻停止工作」的根因：运行时查的是内存，
    /// 重启后内存没了、文件也没了，才真正崩溃。
    ///
    /// 因此这里一律以「磁盘上是否真实存在该文件」为准，让检查结论反映冷启动后的
    /// 真实状态。只有 API Set（api-ms-win-*/ext-ms-win-*）这类磁盘上没有实体、必须由
    /// 系统解析的模块，才回退到 LoadLibrary 判断。
    /// </remarks>
    private static bool IsModuleAvailable(string module, IReadOnlyList<string> localDirectories)
    {
        // 1. 应用目录、原生库目录及其子目录：磁盘上真实存在才算可用。
        foreach (var directory in localDirectories)
        {
            if (File.Exists(Path.Combine(directory, module)))
            {
                return true;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 平台：NativeLibrary 会按标准路径解析系统库；应用自带的库
            // 已在上面按磁盘文件判断过。
            return CanLoadNativeLibrary(module);
        }

        // 2. Windows 系统目录：只查实体文件，不触发任何加载（避免命中内存残影）。
        foreach (var systemDirectory in GetWindowsSystemDirectories())
        {
            if (File.Exists(Path.Combine(systemDirectory, module)))
            {
                return true;
            }
        }

        // 3. API Set 在磁盘上没有实体文件，只能由系统解析。这类属于操作系统组件，
        //    不会因为删除应用原生库而缺失，用 LoadLibrary 判断不会造成假阳性。
        var name = module.ToLowerInvariant();
        if (name.StartsWith("api-ms-win-", StringComparison.Ordinal) ||
            name.StartsWith("ext-ms-win-", StringComparison.Ordinal))
        {
            return CanLoadLibraryByName(module);
        }

        return false;
    }

    private static IReadOnlyList<string> GetWindowsSystemDirectories()
    {
        var directories = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) &&
                !directories.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(path);
            }
        }

        Add(Environment.SystemDirectory); // System32
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add(windowsDirectory);
        if (!string.IsNullOrEmpty(windowsDirectory))
        {
            Add(Path.Combine(windowsDirectory, "SysWOW64"));
        }

        return directories;
    }

    private static bool CanLoadLibraryByName(string module)
    {
        try
        {
            // 先按数据文件映射（不会执行 DllMain），失败再按普通库加载。
            var handle = LoadLibraryExW(module, IntPtr.Zero, LoadLibraryAsDataFile);
            if (handle != IntPtr.Zero)
            {
                FreeLibrary(handle);
                return true;
            }

            handle = LoadLibraryExW(module, IntPtr.Zero, 0);
            if (handle != IntPtr.Zero)
            {
                FreeLibrary(handle);
                return true;
            }
        }
        catch
        {
            // 加载失败按缺失处理。
        }

        return false;
    }

    private static bool CanLoadNativeLibrary(string module)
    {
        try
        {
            if (NativeLibrary.TryLoad(module, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // 加载失败按缺失处理。
        }

        return false;
    }

    private static NativeDependencyKind Classify(string module)
    {
        var name = module.ToLowerInvariant();

        if (name.StartsWith("vcruntime140", StringComparison.Ordinal) ||
            name.StartsWith("msvcp140", StringComparison.Ordinal) ||
            name.StartsWith("concrt140", StringComparison.Ordinal) ||
            name.StartsWith("vccorlib140", StringComparison.Ordinal) ||
            name.StartsWith("vcomp140", StringComparison.Ordinal) ||
            name.StartsWith("api-ms-win-crt-", StringComparison.Ordinal) ||
            name == "ucrtbase.dll")
        {
            return NativeDependencyKind.VisualCppRuntime;
        }

        if (name.StartsWith("api-ms-win-", StringComparison.Ordinal) ||
            name.StartsWith("ext-ms-win-", StringComparison.Ordinal) ||
            SystemModules.Contains(name))
        {
            return NativeDependencyKind.SystemComponent;
        }

        if (name is "coreclr.dll" or "hostfxr.dll" or "hostpolicy.dll" or "clrjit.dll"
            or "libcoreclr.so" or "libhostfxr.so" ||
            name.StartsWith("system.", StringComparison.Ordinal))
        {
            return NativeDependencyKind.DotNetRuntime;
        }

        return NativeDependencyKind.ApplicationNative;
    }

    private sealed record NuGetPackageReference(string Id, string Version);

    private static Dictionary<string, NuGetPackageReference> LoadRuntimeAssetMap(string root, string rid)
    {
        var map = new Dictionary<string, NuGetPackageReference>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> depsFiles;
        try
        {
            depsFiles = Directory.EnumerateFiles(root, "*.deps.json", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception)
        {
            return map;
        }

        foreach (var depsFile in depsFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(depsFile));
                if (!document.RootElement.TryGetProperty("targets", out var targets) ||
                    targets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var package in target.Value.EnumerateObject())
                    {
                        var separator = package.Name.LastIndexOf('/');
                        if (separator <= 0 || separator == package.Name.Length - 1 ||
                            package.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var id = package.Name[..separator];
                        var version = package.Name[(separator + 1)..];
                        if (!package.Value.TryGetProperty("runtimeTargets", out var runtimeTargets) ||
                            runtimeTargets.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var asset in runtimeTargets.EnumerateObject())
                        {
                            if (asset.Value.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var assetType = asset.Value.TryGetProperty("assetType", out var typeElement)
                                ? typeElement.GetString()
                                : null;
                            if (!string.Equals(assetType, "native", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // 只关心当前平台的原生库，其它 RID 的文件本来就不会被拷到输出目录。
                            if (!asset.Name.StartsWith($"runtimes/{rid}/", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // 符号文件不是依赖，缺了也不影响运行，不参与检查。
                            var extension = Path.GetExtension(asset.Name);
                            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                                !extension.Equals(".so", StringComparison.OrdinalIgnoreCase) &&
                                !extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            map[asset.Name] = new NuGetPackageReference(id, version);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 单个 deps.json 解析失败不影响其它文件。
            }
        }

        return map;
    }

    // 名字本身已经带 W 后缀：必须 ExactSpelling，否则默认的 A/W 名字改写会先去找
    // LoadLibraryExWW。虽然 CLR 找不到时会回退到精确名字，但不值得赌运行时版本行为。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}

/// <summary>
/// 原生依赖检查与修复服务。启动时会先跑一遍检查，缺失的系统运行库自动从微软下载安装，
/// 缺失的 NuGet 原生库自动从 nuget.org 还原。
/// </summary>
public sealed class NativeDependencyService(ILogger<NativeDependencyService> logger)
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 最近一次扫描结果。设置页会用它恢复上次的状态。
    /// </summary>
    public NativeDependencyReport? LastReport { get; private set; }

    /// <summary>
    /// 最近一次修复结果。
    /// </summary>
    public NativeDependencyRepairResult? LastRepairResult { get; private set; }

    public Task<NativeDependencyReport> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var report = NativeDependencyProbe.Probe();
            LastReport = report;
            return report;
        }, cancellationToken);

    /// <summary>
    /// 扫描并修复全部可自动处理的问题。扫描本身很快，只有确实缺失时才会联网。
    /// </summary>
    public async Task<NativeDependencyRepairResult?> EnsureAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = await ProbeAsync(cancellationToken);
        if (report.IsHealthy)
        {
            logger.LogInformation("原生库依赖检查通过，共检查 {Count} 个文件。", report.ScannedFileCount);
            return null;
        }

        logger.LogWarning("检测到 {Count} 项原生依赖问题：{Issues}", report.Issues.Count,
            string.Join("；", report.Issues.Select(x => x.Description)));
        return await RepairAsync(report, progress, cancellationToken);
    }

    public async Task<NativeDependencyRepairResult> RepairAsync(NativeDependencyReport report,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var restoredFiles = 0;
            var visualCppRuntimeInstalled = false;
            string? failure = null;

            var cppIssues = report.Issues
                .Where(x => x.Kind == NativeDependencyKind.VisualCppRuntime)
                .ToList();
            if (cppIssues.Count > 0)
            {
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        visualCppRuntimeInstalled =
                            await InstallVisualCppRuntimeAsync(progress, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failure = $"安装 Microsoft Visual C++ 运行库失败：{ex.Message}";
                        logger.LogError(ex, "安装 Microsoft Visual C++ 运行库失败。");
                    }
                }
                else
                {
                    failure = "当前系统缺少 C 运行库，请通过系统包管理器安装。";
                }
            }

            foreach (var issue in report.Issues.Where(x =>
                         x.Kind == NativeDependencyKind.ApplicationNative &&
                         !string.IsNullOrEmpty(x.PackageId) &&
                         !string.IsNullOrEmpty(x.AssetPath)))
            {
                try
                {
                    await RestoreNativeAssetAsync(issue, report.BaseDirectory, progress, cancellationToken);
                    restoredFiles++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure ??= $"还原 {issue.AssetPath} 失败：{ex.Message}";
                    logger.LogError(ex, "从 NuGet 还原原生库 {Asset} 失败。", issue.AssetPath);
                }
            }

            var after = await ProbeAsync(cancellationToken);
            var result = new NativeDependencyRepairResult(restoredFiles, visualCppRuntimeInstalled,
                after.Issues, failure);
            LastRepairResult = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> InstallVisualCppRuntimeAsync(IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var url = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "https://aka.ms/vs/17/release/vc_redist.x86.exe",
            Architecture.Arm64 => "https://aka.ms/vs/17/release/vc_redist.arm64.exe",
            _ => "https://aka.ms/vs/17/release/vc_redist.x64.exe"
        };

        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        var directory = Path.Combine(Path.GetTempPath(), "ClassIsland", "NativeDependencies");
        Directory.CreateDirectory(directory);
        var installerPath = Path.Combine(directory, fileName);

        progress?.Report($"正在下载 Microsoft Visual C++ 运行库（{fileName}）…");
        await DownloadFileAsync(url, installerPath, cancellationToken);

        progress?.Report("正在安装 Microsoft Visual C++ 运行库，请在系统提示中允许管理员权限…");
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/install /passive /norestart",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动运行库安装程序。");
        await process.WaitForExitAsync(cancellationToken);

        // 1638：系统中已经装了更新版本；3010：安装成功但需要重启。两者都说明运行库可用。
        if (process.ExitCode is 0 or 1638 or 3010)
        {
            logger.LogInformation("Microsoft Visual C++ 运行库安装完成，退出码 {ExitCode}。", process.ExitCode);
            return true;
        }

        throw new InvalidOperationException($"安装程序返回错误码 {process.ExitCode}。");
    }

    private async Task RestoreNativeAssetAsync(NativeDependencyIssue issue, string baseDirectory,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var id = issue.PackageId!;
        var version = issue.PackageVersion!;
        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

        var cacheDirectory = Path.Combine(Path.GetTempPath(), "ClassIsland", "NativeDependencies", "nuget");
        Directory.CreateDirectory(cacheDirectory);
        var packagePath = Path.Combine(cacheDirectory, $"{lowerId}.{lowerVersion}.nupkg");

        progress?.Report($"正在还原 {Path.GetFileName(issue.AssetPath!)}（{id} {version}）…");
        if (!File.Exists(packagePath))
        {
            await DownloadFileAsync(url, packagePath, cancellationToken);
        }

        var normalizedAsset = issue.AssetPath!.Replace('\\', '/');
        var destination = Path.Combine(baseDirectory,
            normalizedAsset.Replace('/', Path.DirectorySeparatorChar));
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.Entries.FirstOrDefault(x =>
                        string.Equals(x.FullName.Replace('\\', '/'), normalizedAsset,
                            StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(x =>
                        x.FullName.Contains("/native/", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetFileName(x.FullName), Path.GetFileName(normalizedAsset),
                            StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            throw new FileNotFoundException($"包 {id} {version} 中未找到 {normalizedAsset}。");
        }

        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
        logger.LogInformation("已从 NuGet 包 {Package} {Version} 还原 {Asset}。", id, version, normalizedAsset);
    }

    private static async Task DownloadFileAsync(string url, string destinationPath,
        CancellationToken cancellationToken)
    {
        // 先写临时文件再改名：下载中断时不会留下看起来完整、实际残缺的包，
        // 否则下次会直接复用坏文件，导致还原一直失败。
        var temporaryPath = destinationPath + ".tmp";
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            response.EnsureSuccessStatusCode();
            await using (var target = File.Create(temporaryPath))
            {
                await response.Content.CopyToAsync(target, timeoutSource.Token);
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // 临时文件删不掉不影响后续流程。
                }
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            // 只限制到响应头为止；下载大文件时内容读取不受这个超时限制，
            // 但网络不通时能在半分钟内失败，不会让启动画面卡上几分钟。
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassIsland-NativeDependencyService/1.0");
        return client;
    }
}
