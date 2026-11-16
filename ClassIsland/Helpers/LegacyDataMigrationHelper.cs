using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClassIsland.Core;
using ClassIsland.Services;
using Microsoft.Win32;

namespace ClassIsland.Helpers;

/// <summary>
/// 从其它 ClassIsland 安装目录迁移插件与备份包。
/// </summary>
/// <remarks>
/// 兼容三种部署形态：
/// <list type="bullet">
/// <item>Debug：PackagingType 为默认值，数据目录就是 exe 所在目录。</item>
/// <item>Release / 便携版：PackagingType=folder，数据目录是安装根下的 data 子目录。</item>
/// <item>安装版 / 商店版：数据目录为 %AppData%\ClassIsland\Data。</item>
/// </list>
/// 旧目录通过开机自启项、常见安装位置和当前 exe 的相邻目录综合定位。
/// 只做复制，不移动、不覆盖已有文件，避免影响仍在使用的旧版本。
/// </remarks>
public static class LegacyDataMigrationHelper
{
    /// <summary>迁移记录的格式版本。旧格式（无版本头）会被丢弃并重新扫描。</summary>
    private const string MarkerVersion = "#v2";

    private const string LogFileName = "legacy-migration.log";

    /// <summary>用来判断一个目录是不是 ClassIsland 的数据目录。</summary>
    private static readonly string[] DataMarkers = ["Plugins", "Backups", "Config", "Settings.json"];

    /// <summary>上一次扫描的结果，供启动完成后写日志。</summary>
    public static IReadOnlyList<MigrationResult> Results { get; private set; } = [];

    public sealed record MigrationResult(
        string LegacyRoot,
        int CopiedPlugins,
        int SkippedPlugins,
        int CopiedBackups,
        int FailedPlugins,
        int FailedBackups,
        bool PluginsComplete,
        bool BackupsComplete)
    {
        /// <summary>插件与备份都已处理完毕（没有失败项），可以记账。</summary>
        public bool Complete => PluginsComplete && BackupsComplete;

        /// <summary>本次是否真的产生了动作，用于决定要不要提示用户。</summary>
        public bool HasChanges =>
            CopiedPlugins > 0 || CopiedBackups > 0 || SkippedPlugins > 0 ||
            FailedPlugins > 0 || FailedBackups > 0;
    }

    /// <summary>
    /// 扫描旧版本目录并迁移数据。必须在 <see cref="CommonDirectories.AppRootFolderPath"/>
    /// 确定之后、插件扫描之前调用。
    /// </summary>
    public static void Run()
    {
        // 注意：这里不能用 #if Platforms_Windows。ClassIsland 项目本身不定义该常量
        // （只有 ClassIsland.Desktop 定义），用条件编译会导致方法体被整段裁掉。
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var results = new List<MigrationResult>();
        var log = new StringBuilder();
        try
        {
            var currentRoot = NormalizePath(CommonDirectories.AppRootFolderPath);
            Log(log, $"当前数据目录：{currentRoot}");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var migrated = LoadMigratedRoots(log);
            foreach (var legacyRoot in EnumerateLegacyDataRoots(log))
            {
                string normalized;
                try
                {
                    normalized = NormalizePath(legacyRoot);
                }
                catch (Exception ex)
                {
                    Log(log, $"跳过无法解析的路径 {legacyRoot}：{ex.Message}");
                    continue;
                }

                // 只跳过「相同」和「旧目录位于当前目录内部」两种情况：
                // 前者是把数据复制给自己，后者会把当前数据目录的子目录当成旧版数据、
                // 在复制时又把数据搬回自己身上。
                // 反过来「当前目录位于旧目录内部」是真实存在的升级布局：
                // 旧版把数据放在 %AppData%\ClassIsland，新版放在 %AppData%\ClassIsland\Data，
                // 这种旧目录必须允许迁移，否则旧版本里的插件永远不会被带过来。
                if (string.Equals(normalized, currentRoot, StringComparison.OrdinalIgnoreCase) ||
                    IsAncestorOf(currentRoot, normalized))
                {
                    Log(log, $"跳过 {normalized}：与当前数据目录相同，或位于当前数据目录内部");
                    continue;
                }

                if (!visited.Add(normalized))
                {
                    continue;
                }

                if (migrated.Contains(normalized))
                {
                    Log(log, $"跳过 {normalized}：之前已完成迁移");
                    continue;
                }

                // 每个旧目录单独隔离，一个目录出错不影响其它目录。
                try
                {
                    var result = Migrate(legacyRoot, currentRoot);
                    Log(log,
                        $"处理 {normalized}：插件 复制 {result.CopiedPlugins} / 跳过 {result.SkippedPlugins} / 失败 {result.FailedPlugins}，" +
                        $"备份 复制 {result.CopiedBackups} / 失败 {result.FailedBackups}");
                    if (result.Complete)
                    {
                        // 只有插件和备份都处理完才记账；有失败项时下次启动继续重试。
                        migrated.Add(normalized);
                    }

                    if (result.HasChanges)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    Log(log, $"处理 {normalized} 失败：{ex}");
                }
            }

            SaveMigratedRoots(migrated, log);
        }
        catch (Exception ex)
        {
            Log(log, $"扫描失败：{ex}");
        }

        Results = results;
        WriteLog(log);
    }

    /// <summary>把旧目录下的插件和备份包复制到当前数据目录。</summary>
    internal static MigrationResult Migrate(string legacyRoot, string? currentRoot = null)
    {
        currentRoot = NormalizePath(currentRoot ?? CommonDirectories.AppRootFolderPath);
        var copiedPlugins = 0;
        var skippedPlugins = 0;
        var failedPlugins = 0;
        var copiedBackups = 0;
        var failedBackups = 0;

        var pluginsComplete = true;
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
                    // 复制到一半失败时，目标目录里会留下残缺的插件。不清理的话，
                    // 下次启动会把它当成「同名插件已存在」直接跳过，残缺插件就永远修不回来了。
                    TryDeleteDirectory(target);
                    failedPlugins++;
                    pluginsComplete = false;
                    Console.WriteLine($"[旧版本迁移] 复制插件 {name} 失败：{ex.Message}");
                }
            }

            // 插件包有时直接放在 Plugins 根目录（.cipx、.dll），一并复制。
            foreach (var source in Directory.EnumerateFiles(sourcePlugins))
            {
                var name = Path.GetFileName(source);
                var target = Path.Combine(targetPlugins, name);
                if (File.Exists(target))
                {
                    continue;
                }

                try
                {
                    File.Copy(source, target, false);
                    copiedPlugins++;
                }
                catch (Exception ex)
                {
                    failedPlugins++;
                    pluginsComplete = false;
                    Console.WriteLine($"[旧版本迁移] 复制插件文件 {name} 失败：{ex.Message}");
                }
            }
        }

        var backupsComplete = true;
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
                    failedBackups++;
                    backupsComplete = false;
                    Console.WriteLine($"[旧版本迁移] 复制备份 {Path.GetFileName(source)} 失败：{ex.Message}");
                }
            }

            // 恢复页面同时支持目录形式的备份，这里也要带上。
            foreach (var source in Directory.EnumerateDirectories(sourceBackups))
            {
                var name = Path.GetFileName(source);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var target = Path.Combine(targetBackups, name);
                if (Directory.Exists(target))
                {
                    continue;
                }

                try
                {
                    CopyDirectory(source, target);
                    copiedBackups++;
                }
                catch (Exception ex)
                {
                    TryDeleteDirectory(target);
                    failedBackups++;
                    backupsComplete = false;
                    Console.WriteLine($"[旧版本迁移] 复制备份目录 {name} 失败：{ex.Message}");
                }
            }
        }

        return new MigrationResult(
            legacyRoot,
            copiedPlugins,
            skippedPlugins,
            copiedBackups,
            failedPlugins,
            failedBackups,
            pluginsComplete,
            backupsComplete);
    }

    /// <summary>收集所有可能的旧版本数据目录。</summary>
    private static IEnumerable<string> EnumerateLegacyDataRoots(StringBuilder log)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in EnumerateAutoStartTargets())
        {
            string? root;
            try
            {
                root = ResolveLegacyDataRoot(target);
            }
            catch (Exception ex)
            {
                Log(log, $"解析自启目标 {target} 失败：{ex.Message}");
                continue;
            }

            if (root is null)
            {
                Log(log, $"自启目标 {target} 未找到数据目录");
                continue;
            }

            if (seen.Add(NormalizePath(root)))
            {
                Log(log, $"自启项定位到旧目录：{root}");
                yield return root;
            }
        }

        // 便携版换目录、安装版换便携版等场景下，自启项可能已被清理，
        // 这里再把常见位置扫一遍。
        foreach (var candidate in EnumerateWellKnownCandidates())
        {
            string normalized;
            try
            {
                normalized = NormalizePath(candidate);
            }
            catch
            {
                continue;
            }

            if (!HasDataMarkers(candidate) || !seen.Add(normalized))
            {
                continue;
            }

            Log(log, $"常见位置定位到旧目录：{candidate}");
            yield return candidate;
        }
    }

    /// <summary>
    /// 枚举常见的数据目录候选。覆盖安装版（Roaming/Local）、
    /// Release 便携版与 Debug（exe 相邻的 data 与目录自身）。
    /// </summary>
    private static IEnumerable<string> EnumerateWellKnownCandidates()
    {
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData
                 })
        {
            string baseDir;
            try
            {
                baseDir = Environment.GetFolderPath(special);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            yield return Path.Combine(baseDir, "ClassIsland", "Data");
            yield return Path.Combine(baseDir, "ClassIsland");
        }

        // 从当前 exe 所在目录向上找两级，覆盖：
        //   便携版：<root>/data
        //   Release：<root>/data（exe 可能在 <root>/bin 下）
        //   Debug：<bin>/Debug/net10.0-windows.../ 自身（由 HasDataMarkers 判定）
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        if (string.IsNullOrEmpty(exeDir))
        {
            yield break;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(exeDir);
        }
        catch
        {
            yield break;
        }

        // 当前目录自身必须无条件试一次。不能放进下面的循环：循环条件要求 Parent 非空，
        // exe 直接放在盘符根（U 盘/绿色版常见）时 Parent 为 null，循环一次都不跑，
        // 这条兜底就等于没写。
        yield return current.FullName;
        yield return Path.Combine(current.FullName, "data");

        for (var i = 0; i < 2 && current?.Parent is not null; i++)
        {
            var parent = current.Parent;
            yield return Path.Combine(parent.FullName, "data");
            // 盘符根 / 网络共享根不能整体当候选：装在 D:\Tools\ClassIsland 时上一级是 D:\Tools，
            // 而再上一级就是 D:\，把整盘当数据目录会把无关内容扫进来。
            if (parent.Parent is not null)
            {
                yield return parent.FullName;
            }
            current = parent;
        }
    }

    private static string MarkerFilePath =>
        Path.Combine(CommonDirectories.AppConfigPath, "legacy-migration.txt");

    private static HashSet<string> LoadMigratedRoots(StringBuilder log)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(MarkerFilePath))
            {
                return roots;
            }

            var lines = File.ReadAllLines(MarkerFilePath);
            if (lines.Length == 0 || !string.Equals(lines[0].Trim(), MarkerVersion, StringComparison.Ordinal))
            {
                // 旧版本记账不区分成功与否，可能把失败的目录也记上了。直接丢弃重扫。
                Log(log, "迁移记录为旧格式，已忽略并重新扫描");
                return roots;
            }

            foreach (var line in lines.Skip(1))
            {
                var value = line.Trim();
                if (value.Length > 0)
                {
                    roots.Add(NormalizePath(value));
                }
            }

            Log(log, $"已有迁移记录 {roots.Count} 条");
        }
        catch (Exception ex)
        {
            Log(log, $"读取迁移记录失败：{ex.Message}");
        }

        return roots;
    }

    private static void SaveMigratedRoots(HashSet<string> roots, StringBuilder log)
    {
        try
        {
            Directory.CreateDirectory(CommonDirectories.AppConfigPath);
            var lines = new List<string> { MarkerVersion };
            lines.AddRange(roots);
            File.WriteAllLines(MarkerFilePath, lines);
        }
        catch (Exception ex)
        {
            Log(log, $"写入迁移记录失败：{ex.Message}");
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
        // 64 位系统上 Run 键可能写在 32 位视图（WOW6432Node），两个视图都查一遍。
        foreach (var view in new[] { RegistryView.Default, RegistryView.Registry32 })
        {
            RegistryKey? key = null;
            try
            {
                key = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    var exe = ExtractExecutablePath(key.GetValue(name) as string);
                    if (!string.IsNullOrWhiteSpace(exe) && !targets.Contains(exe, StringComparer.OrdinalIgnoreCase))
                    {
                        targets.Add(exe);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[旧版本迁移] 读取 {hive}/{view} 启动项失败：{ex.Message}");
            }
            finally
            {
                key?.Dispose();
            }
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
    /// Debug/Release 数据就在 exe 目录，安装版数据在全局目录（由调用方兜底）。
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

        // 便携版：exe 同级（或上级）的 data 子目录。
        var portableData = Path.Combine(directory, "data");
        if (HasDataMarkers(portableData))
        {
            return portableData;
        }

        // Debug / Release 绿色版：数据直接躺在 exe 旁边。
        if (HasDataMarkers(directory))
        {
            return directory;
        }

        // exe 放在 <root>/bin 下的布局，数据在 <root>/data 或 <root>。
        var parent = Path.GetDirectoryName(directory);
        if (!string.IsNullOrEmpty(parent))
        {
            var parentData = Path.Combine(parent, "data");
            if (HasDataMarkers(parentData))
            {
                return parentData;
            }

            if (HasDataMarkers(parent))
            {
                return parent;
            }
        }

        // 安装版：数据在全局目录，返回 null 交给常见位置扫描。
        return null;
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

    /// <summary>复制目录的最大递归深度。正常情况下插件目录远达不到，这里只用于兜底。</summary>
    private const int MaxCopyDepth = 64;

    private static void CopyDirectory(string source, string target, int depth = 0)
    {
        // 目录联接/符号链接可以指回上级，递归复制会无限展开直到栈溢出。
        // StackOverflowException 无法捕获，表现为「应用启动即闪退」且每次启动都复现，
        // 用户只能手工去旧目录里删掉那个链接。所以必须在进入前就拦住。
        if (depth > MaxCopyDepth)
        {
            Console.WriteLine($"[旧版本迁移] 目录层级超过 {MaxCopyDepth} 层，已跳过：{source}");
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // 指向别处的符号链接跳过：复制它会把链接目标的内容整份搬进数据目录。
            if (IsReparsePoint(file))
            {
                continue;
            }

            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Copy(file, destination, false);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (IsReparsePoint(directory))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), depth + 1);
        }
    }

    /// <summary>判断路径是否为符号链接/目录联接等重解析点。取不到属性时按「不是」处理。</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 删除复制失败留下的半成品目录。删不掉时保持原样（下次会被当成同名目录跳过），
    /// 但至少不会在本次迁移里留下一个「看起来成功」的假象。
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[旧版本迁移] 清理半成品目录 {path} 失败：{ex.Message}");
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>两个路径相同，或存在祖先/后代包含关系。</summary>
    private static bool IsSameOrNested(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsAncestorOf(a, b) || IsAncestorOf(b, a);
    }

    private static bool IsAncestorOf(string parent, string child)
    {
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(StringBuilder log, string message)
    {
        log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        Console.WriteLine($"[旧版本迁移] {message}");
    }

    private static void WriteLog(StringBuilder log)
    {
        if (log.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(CommonDirectories.AppLogFolderPath);
            var path = Path.Combine(CommonDirectories.AppLogFolderPath, LogFileName);
            File.AppendAllText(path,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 旧版本数据迁移 ====={Environment.NewLine}{log}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不应影响启动流程。
        }
    }
}
