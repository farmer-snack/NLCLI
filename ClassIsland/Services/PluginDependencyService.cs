using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using ClassIsland.Core;

namespace ClassIsland.Services;

/// <summary>
/// 插件可选依赖的按需下载与缓存。
/// </summary>
/// <remarks>
/// 有一部分库只有个别插件会用到（截图、摄像头、人脸识别等），主程序自身完全不引用。
/// 把它们打进安装包会让每个人多下载一百多 MB，所以改为插件第一次真正用到时才下载，
/// 下载结果缓存在 <see cref="CacheDirectory"/>，后续启动直接复用，不再联网。
/// </remarks>
public static class PluginDependencyService
{
    /// <summary>
    /// 依赖缓存目录。与插件目录分开，卸载或更新插件都不会误删已经下好的依赖。
    /// </summary>
    public static string CacheDirectory { get; } =
        Path.Combine(CommonDirectories.AppRootFolderPath, "PluginDependencies");

    private static readonly object SyncRoot = new();

    private static readonly HttpClient Client = CreateHttpClient();

    /// <summary>
    /// 记录每个依赖最近一次下载失败的时间，避免每次解析都卡在同一个下载上。
    /// </summary>
    private static readonly Dictionary<string, DateTime> FailedAt = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 一个 NuGet 包，以及需要从包里抽取出来的文件。
    /// </summary>
    private sealed record DependencyPackage(string PackageId, string Version, string[] Entries);

    /// <summary>
    /// 可选依赖清单。键是插件请求的程序集名，值是包含该程序集的 NuGet 包。
    /// </summary>
    /// <remarks>
    /// 需要新增可选依赖时，在下面加一条即可，加载逻辑不用改。
    /// 抽取出来的文件会平铺到 <see cref="CacheDirectory"/>，原生库与托管库放在同一层。
    /// </remarks>
    private static readonly Dictionary<string, DependencyPackage[]> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenCvSharp"] =
        [
            new DependencyPackage("OpenCvSharp4", "4.13.0.20260627", ["lib/net8.0/OpenCvSharp.dll"]),
            new DependencyPackage("OpenCvSharp4.runtime.win", "4.13.0.20260627",
            [
                "runtimes/win-x64/native/OpenCvSharpExtern.dll",
                "runtimes/win-x64/native/opencv_videoio_ffmpeg4130_64.dll"
            ])
        ],
        ["DlibDotNet"] =
        [
            new DependencyPackage("DlibDotNet", "19.21.0.20220724",
            [
                "lib/netstandard2.0/DlibDotNet.dll",
                "runtimes/win-x64/native/DlibDotNetNative.dll",
                "runtimes/win-x64/native/DlibDotNetNativeDnn.dll"
            ])
        ]
    };

    /// <summary>
    /// 包下载地址，按顺序尝试。
    /// </summary>
    private static readonly string[] PackageSources =
    [
        "https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg",
        "https://www.nuget.org/api/v2/package/{0}/{1}"
    ];

    /// <summary>
    /// 判断某个程序集是否属于可选依赖。
    /// </summary>
    public static bool IsKnown(string? assemblyName)
    {
        return !string.IsNullOrEmpty(assemblyName) && Catalog.ContainsKey(assemblyName);
    }

    /// <summary>
    /// 获取已缓存的可选依赖程序集路径。未缓存返回 <see langword="null"/>。
    /// </summary>
    public static string? TryGetCachedAssemblyPath(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return null;
        }

        var path = Path.Combine(CacheDirectory, assemblyName + ".dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 获取已缓存的可选依赖原生库路径。未缓存返回 <see langword="null"/>。
    /// </summary>
    public static string? TryGetCachedNativePath(string? unmanagedDllName)
    {
        if (string.IsNullOrEmpty(unmanagedDllName))
        {
            return null;
        }

        var fileName = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? unmanagedDllName
            : unmanagedDllName + ".dll";
        var path = Path.Combine(CacheDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 确保可选依赖已经缓存到本地，没有则当场下载。
    /// </summary>
    /// <param name="assemblyName">插件请求的程序集名。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>依赖可用时返回 <see langword="true"/>。</returns>
    public static bool Ensure(string assemblyName, out string? error)
    {
        error = null;
        if (!Catalog.TryGetValue(assemblyName, out var packages))
        {
            error = "不在可选依赖清单中";
            return false;
        }

        if (TryGetCachedAssemblyPath(assemblyName) != null)
        {
            return true;
        }

        lock (SyncRoot)
        {
            // 等锁期间别的线程可能已经下完了。
            if (TryGetCachedAssemblyPath(assemblyName) != null)
            {
                return true;
            }

            if (FailedAt.TryGetValue(assemblyName, out var failedAt))
            {
                var remaining = RetryCooldown - (DateTime.UtcNow - failedAt);
                if (remaining > TimeSpan.Zero)
                {
                    error = $"上次下载失败，{(int)Math.Ceiling(remaining.TotalMinutes)} 分钟后重试";
                    return false;
                }
            }

            WriteLog($"首次使用可选依赖 {assemblyName}，开始下载 " +
                     string.Join("、", packages.Select(package => $"{package.PackageId} {package.Version}")));

            if (!Download(packages, out var downloadError))
            {
                FailedAt[assemblyName] = DateTime.UtcNow;
                error = downloadError;
                WriteLog($"可选依赖 {assemblyName} 下载失败：{downloadError}");
                return false;
            }

            FailedAt.Remove(assemblyName);
            WriteLog($"可选依赖 {assemblyName} 已缓存到 {CacheDirectory}");
            return true;
        }
    }

    /// <summary>
    /// 后台预取插件实际引用到的可选依赖。
    /// </summary>
    /// <remarks>
    /// 放在插件加载时触发，等用户点到摄像头、截图这些功能时依赖通常已经下好，
    /// 不至于在 UI 线程上干等下载。
    /// </remarks>
    public static void Prefetch(IEnumerable<string> assemblyPaths)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in assemblyPaths)
                {
                    foreach (var reference in ReadAssemblyReferences(path))
                    {
                        if (IsKnown(reference) && TryGetCachedAssemblyPath(reference) is null)
                        {
                            pending.Add(reference);
                        }
                    }
                }

                foreach (var name in pending)
                {
                    Ensure(name, out _);
                }
            }
            catch (Exception ex)
            {
                WriteLog($"预取可选依赖失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 下载全部包并抽取需要的文件到缓存目录。
    /// </summary>
    private static bool Download(IReadOnlyList<DependencyPackage> packages, out string? error)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ClassIsland", "PluginDependencies",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            foreach (var package in packages)
            {
                var nupkgPath = Path.Combine(tempDirectory, package.PackageId + ".nupkg");
                if (!DownloadPackage(package, nupkgPath, out var downloadError))
                {
                    error = downloadError;
                    return false;
                }

                if (!Extract(package, nupkgPath, tempDirectory, out var extractError))
                {
                    error = extractError;
                    return false;
                }
            }

            Directory.CreateDirectory(CacheDirectory);
            foreach (var file in Directory.GetFiles(tempDirectory))
            {
                if (file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(file, Path.Combine(CacheDirectory, Path.GetFileName(file)), true);
            }

            error = null;
            return true;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// 依次尝试各个下载源，成功后返回 <see langword="true"/>。
    /// </summary>
    private static bool DownloadPackage(DependencyPackage package, string destination, out string? error)
    {
        Exception? lastError = null;
        foreach (var template in PackageSources)
        {
            var url = string.Format(template, package.PackageId.ToLowerInvariant(),
                package.Version.ToLowerInvariant());
            try
            {
                using var response = Client.Send(new HttpRequestMessage(HttpMethod.Get, url));
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    continue;
                }

                using var source = response.Content.ReadAsStream();
                using var target = File.Create(destination);
                source.CopyTo(target);

                if (target.Length == 0)
                {
                    lastError = new IOException("下载内容为空");
                    continue;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        error = lastError?.Message ?? "下载失败";
        return false;
    }

    /// <summary>
    /// 从 nupkg 中抽取清单列出的文件，平铺到临时目录。
    /// </summary>
    private static bool Extract(DependencyPackage package, string nupkgPath, string destination,
        out string? error)
    {
        try
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            foreach (var entry in package.Entries)
            {
                var item = archive.GetEntry(entry);
                if (item is null)
                {
                    error = $"包 {package.PackageId} 中找不到 {entry}";
                    return false;
                }

                var target = Path.Combine(destination, Path.GetFileName(entry));
                using var input = item.Open();
                using var output = File.Create(target);
                input.CopyTo(output);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取程序集引用的程序集名，用于判断插件到底需要哪些可选依赖。
    /// </summary>
    private static IEnumerable<string> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        if (!reader.HasMetadata)
        {
            yield break;
        }

        var metadata = reader.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            yield return metadata.GetString(metadata.GetAssemblyReference(handle).Name);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassIsland");
        return client;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响功能。
        }
    }

    private static void WriteLog(string message)
    {
        Console.WriteLine($"[插件依赖] {message}");
        Debug.WriteLine($"[插件依赖] {message}");
    }
}
