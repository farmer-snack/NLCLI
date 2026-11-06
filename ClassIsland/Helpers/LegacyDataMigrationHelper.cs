using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ClassIsland.Core;
using ClassIsland.Services;
using Microsoft.Win32;

namespace ClassIsland.Helpers;

/// <summary>
/// 从其它 ClassIsland 安装目录迁移插件与备份包。
/// </summary>
/// <remarks>
/// 旧目录通过开机自启项定位：启动文件夹里的 .lnk 快捷方式，以及注册表 Run 键。
/// 只做复制，不移动、不覆盖已有文件，避免影响仍在使用的旧版本。
/// </remarks>
public static class LegacyDataMigrationHelper
{
    /// <summary>用来判断一个目录是不是 ClassIsland 的数据目录。</summary>
    private static readonly string[] DataMarkers = ["Plugins", "Backups", "Config", "Settings.json"];

    /// <summary>上一次扫描的结果，供启动完成后写日志。</summary>
    public static IReadOnlyList<MigrationResult> Results { get; private set; } = [];

    public sealed record MigrationResult(
        string LegacyRoot,
        int CopiedPlugins,
        int SkippedPlugins,
        int CopiedBackups,
        int FailedPlugins);

    /// <summary>
    /// 扫描开机自启项并迁移旧版本数据。必须在 <see cref="CommonDirectories.AppRootFolderPath"/>
    /// 确定之后、插件扫描之前调用。
    /// </summary>
    public static void Run()
    {
#if Platforms_Windows
        var results = new List<MigrationResult>();
        try
        {
            var currentRoot = NormalizePath(CommonDirectories.AppRootFolderPath);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var migrated = LoadMigratedRoots();
            foreach (var legacyRoot in EnumerateLegacyDataRoots())
            {
                var normalized = NormalizePath(legacyRoot);
                // 指向自己的启动项不算旧版本，同一个目录也只处理一次。
                if (string.Equals(normalized, currentRoot, StringComparison.OrdinalIgnoreCase) ||
                    !visited.Add(normalized) || migrated.Contains(normalized))
                {
                    continue;
                }

                var result = Migrate(legacyRoot);
                if (result.FailedPlugins == 0)
                {
                    // 有失败项时不记账，下次启动重试。
                    migrated.Add(normalized);
                }
                if (result.CopiedPlugins > 0 || result.CopiedBackups > 0 ||
                    result.SkippedPlugins > 0 || result.FailedPlugins > 0)
                {
                    results.Add(result);
                }
            }

            SaveMigratedRoots(migrated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[旧版本迁移] 扫描失败：{ex}");
        }

        Results = results;
#endif
    }

    /// <summary>把旧目录下的插件和备份包复制到当前数据目录。</summary>
    internal static MigrationResult Migrate(string legacyRoot, string? currentRoot = null)
    {
        currentRoot ??= CommonDirectories.AppRootFolderPath;
        var copiedPlugins = 0;
        var skippedPlugins = 0;
        var failedPlugins = 0;
        var copiedBackups = 0;

        var sourcePlugins = Path.Combine(legacyRoot, "Plugins");
        if (Directory.Exists(sourcePlugins))
        {
            var targetPlugins = Path.Combine(currentRoot, "Plugins");
            Directory.CreateDirectory(targetPlugins);
            foreach (var source in Directory.EnumerateDirectories(sourcePlugins))
            {
                var name = Path.GetFileName(source);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var target = Path.Combine(targetPlugins, name);
                if (Directory.Exists(target))
                {
                    // 同名插件已存在时保留当前版本，不覆盖。
                    skippedPlugins++;
                    continue;
                }

                try
                {
                    CopyDirectory(source, target);
                    copiedPlugins++;
                }
                catch (Exception ex)
                {
                    failedPlugins++;
                    Console.WriteLine($"[旧版本迁移] 复制插件 {name} 失败：{ex.Message}");
                }
            }
        }

        var sourceBackups = Path.Combine(legacyRoot, "Backups");
        if (Directory.Exists(sourceBackups))
        {
            var targetBackups = Path.Combine(currentRoot, "Backups");
            Directory.CreateDirectory(targetBackups);
            foreach (var source in Directory.EnumerateFiles(sourceBackups, "*.zip"))
            {
                var target = Path.Combine(targetBackups, Path.GetFileName(source));
                if (File.Exists(target))
                {
                    continue;
                }

                try
                {
                    File.Copy(source, target, false);
                    copiedBackups++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[旧版本迁移] 复制备份 {Path.GetFileName(source)} 失败：{ex.Message}");
                }
            }
        }

        return new MigrationResult(legacyRoot, copiedPlugins, skippedPlugins, copiedBackups, failedPlugins);
    }

    /// <summary>收集所有可能的旧版本数据目录。</summary>
    private static IEnumerable<string> EnumerateLegacyDataRoots()
    {
        foreach (var target in EnumerateAutoStartTargets())
        {
            var root = ResolveLegacyDataRoot(target);
            if (root is not null)
            {
                yield return root;
            }
        }

        // 安装版的数据目录固定在 %AppData%\ClassIsland\Data。即使旧版本的启动项
        // 已经被清理，只要数据还在，也一并迁移（从安装版换到便携版的情况）。
        string installedData;
        try
        {
            installedData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassIsland", "Data");
        }
        catch
        {
            yield break;
        }

        if (HasDataMarkers(installedData))
        {
            yield return installedData;
        }
    }

    private static string MarkerFilePath =>
        Path.Combine(CommonDirectories.AppConfigPath, "legacy-migration.txt");

    private static HashSet<string> LoadMigratedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(MarkerFilePath))
            {
                return roots;
            }

            foreach (var line in File.ReadAllLines(MarkerFilePath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    roots.Add(NormalizePath(line));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[旧版本迁移] 读取迁移记录失败：{ex.Message}");
        }

        return roots;
    }

    private static void SaveMigratedRoots(HashSet<string> roots)
    {
        try
        {
            Directory.CreateDirectory(CommonDirectories.AppConfigPath);
            File.WriteAllLines(MarkerFilePath, roots);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[旧版本迁移] 写入迁移记录失败：{ex.Message}");
        }
    }

    /// <summary>枚举开机自启项指向的可执行文件路径。</summary>
    private static IEnumerable<string> EnumerateAutoStartTargets()
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (Directory.Exists(startupFolder))
        {
            foreach (var shortcut in Directory.EnumerateFiles(startupFolder, "*.lnk"))
            {
                string? target = null;
                try
                {
                    target = ResolveShortcutTarget(shortcut);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[旧版本迁移] 无法解析快捷方式 {shortcut}：{ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(target))
                {
                    yield return target;
                }
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var exe in EnumerateRunKeyTargets(hive))
            {
                yield return exe;
            }
        }
    }

    private static IEnumerable<string> EnumerateRunKeyTargets(RegistryHive hive)
    {
        var targets = new List<string>();
        RegistryKey? key = null;
        try
        {
            key = RegistryKey.OpenBaseKey(hive, RegistryView.Default)
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null)
            {
                return targets;
            }

            foreach (var name in key.GetValueNames())
            {
                var exe = ExtractExecutablePath(key.GetValue(name) as string);
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    targets.Add(exe);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[旧版本迁移] 读取 {hive} 启动项失败：{ex.Message}");
        }
        finally
        {
            key?.Dispose();
        }

        return targets;
    }

    /// <summary>从快捷方式里取出目标路径。</summary>
    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            var target = (string)dynamicShortcut.TargetPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                // 少数快捷方式只在参数里带路径。
                target = ExtractExecutablePath((string)dynamicShortcut.Arguments) ?? "";
            }

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    /// <summary>
    /// 从可执行文件路径推导出旧版本的数据目录。便携版数据在 <c>data</c> 子目录，
    /// 安装版数据在全局目录（和当前版本共用），后者不迁移。
    /// </summary>
    internal static string? ResolveLegacyDataRoot(string executablePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        if (!fileName.StartsWith("ClassIsland", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var portableData = Path.Combine(directory, "data");
        if (HasDataMarkers(portableData))
        {
            return portableData;
        }

        return HasDataMarkers(directory) ? directory : null;
    }

    private static bool HasDataMarkers(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var marker in DataMarkers)
        {
            if (Directory.Exists(Path.Combine(directory, marker)) ||
                File.Exists(Path.Combine(directory, marker)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析命令行，取出可执行文件路径，处理带引号的情况。</summary>
    internal static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        commandLine = commandLine.Trim();
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            return end > 1 ? commandLine[1..end] : null;
        }

        var exeIndex = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            return commandLine[..(exeIndex + 4)];
        }

        var spaceIndex = commandLine.IndexOf(' ');
        return spaceIndex > 0 ? commandLine[..spaceIndex] : commandLine;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Copy(file, destination, false);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
