using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

public enum DevelopmentUpdateStage
{
    Preparing,
    InstallingTools,
    Pulling,
    UpdatingSubmodules,
    Building,
    Completed,
    Failed
}

public sealed record DevelopmentUpdateProgress(
    DevelopmentUpdateStage Stage,
    string Message,
    string? Detail = null);

/// <summary>
/// 调试页的“拉取更新并编译运行”功能。仅在从源码目录运行时可用。
/// </summary>
public sealed class DevelopmentUpdateService(ILogger<DevelopmentUpdateService> logger)
{
    private const int SubmoduleRetryCount = 5;
    private const int NetworkRetryCount = 3;
    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    /// <summary>
    /// 最近一次报告的状态文本。设置窗口切到别的页面再切回来时，
    /// ViewModel 会被重建，靠这个值恢复界面显示，避免看起来像没在运行。
    /// </summary>
    public string LastMessage { get; private set; } = "尚未开始。";

    public async Task RunAsync(IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            throw new InvalidOperationException("开发构建正在进行中。");
        }

        try
        {
            var repositoryRoot = FindRepositoryRoot()
                                 ?? throw new InvalidOperationException(
                                     "当前运行目录不在 Git 仓库中。开发构建功能只能从源码目录运行。");
            Report(progress, DevelopmentUpdateStage.Preparing, "已定位仓库", repositoryRoot);

            var git = await EnsureGitAsync(progress, cancellationToken);
            var dotnet = await EnsureDotnetAsync(progress, cancellationToken);

            await PullAsync(git, repositoryRoot, progress, cancellationToken);
            await UpdateSubmodulesAsync(git, repositoryRoot, progress, cancellationToken);
            var buildOutput = await BuildAsync(dotnet, repositoryRoot, progress, cancellationToken);

            Report(progress, DevelopmentUpdateStage.Completed, "编译完成，正在打开输出目录…", buildOutput.Directory);
            OpenDirectory(buildOutput.Directory);

            Report(progress, DevelopmentUpdateStage.Completed, "编译完成，即将重启到新版本…", buildOutput.Executable);
            // 新进程的工作目录必须和当前保持一致：默认打包类型（folderClassic）下，
            // 配置、档案、日志目录都是相对工作目录解析的。如果把工作目录设成编译输出目录，
            // 新版本会拿一套空配置启动，用户看到的现象就是「更新完数据全丢了」。
            LaunchAfterCurrentExit(buildOutput.Executable, Environment.CurrentDirectory);
            // 这里不能再传取消令牌：新版本的进程已经在等待本进程退出，
            // 此时若因取消抛出异常，就会出现「旧进程不退、新进程也不启动」的僵局。
            await Task.Delay(1200);
            Dispatcher.UIThread.Post(() => AppBase.Current.Stop());
        }
        catch (OperationCanceledException)
        {
            Report(progress, DevelopmentUpdateStage.Failed, "操作已取消。");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "开发构建失败。");
            Report(progress, DevelopmentUpdateStage.Failed, $"失败：{ex.Message}", ex.ToString());
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private void Report(IProgress<DevelopmentUpdateProgress> progress, DevelopmentUpdateStage stage,
        string message, string? detail = null)
    {
        LastMessage = message;
        progress.Report(new DevelopmentUpdateProgress(stage, message, detail));
    }

    private static string? FindRepositoryRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? ""
        };

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            var directory = new DirectoryInfo(candidate);
            for (var i = 0; i < 12 && directory != null; i++)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private async Task<string> EnsureGitAsync(IProgress<DevelopmentUpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        var git = await FindToolAsync("git", "--version", cancellationToken) ?? FindGitInCommonPaths();
        if (git != null)
        {
            return git;
        }

        Report(progress, DevelopmentUpdateStage.InstallingTools, "未检测到 Git，正在自动安装…");
        if (OperatingSystem.IsWindows())
        {
            if (await CommandExistsAsync("winget", cancellationToken))
            {
                await RunCheckedAsync("winget",
                    ["install", "--id", "Git.Git", "-e", "--source", "winget",
                        "--accept-package-agreements", "--accept-source-agreements", "--silent"],
                    Environment.CurrentDirectory, progress, cancellationToken);
            }
            else if (await CommandExistsAsync("choco", cancellationToken))
            {
                await RunCheckedAsync("choco", ["install", "git", "-y"],
                    Environment.CurrentDirectory, progress, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException(
                    "未找到 winget 或 choco，无法自动安装 Git。请手动安装 Git 后重试。");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (!await CommandExistsAsync("brew", cancellationToken))
            {
                throw new InvalidOperationException("未找到 Homebrew，无法自动安装 Git。请手动安装 Git 后重试。");
            }

            await RunCheckedAsync("brew", ["install", "git"],
                Environment.CurrentDirectory, progress, cancellationToken);
        }
        else
        {
            await InstallLinuxPackageAsync("git", progress, cancellationToken);
        }

        git = FindGitInCommonPaths() ?? await FindToolAsync("git", "--version", cancellationToken);
        if (git == null)
        {
            throw new InvalidOperationException("Git 安装完成，但仍无法找到 git 命令。请重新启动应用后重试。");
        }

        Report(progress, DevelopmentUpdateStage.InstallingTools, "Git 已就绪。", git);
        return git;
    }

    private async Task<string> EnsureDotnetAsync(IProgress<DevelopmentUpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        var dotnet = await FindToolAsync("dotnet", "--version", cancellationToken) ?? FindDotnetInCommonPaths();
        if (dotnet != null && await HasDotnet10SdkAsync(dotnet, cancellationToken))
        {
            return dotnet;
        }

        Report(progress, DevelopmentUpdateStage.InstallingTools,
            dotnet == null ? "未检测到 .NET SDK，正在自动安装 .NET 10 SDK…" : "未检测到 .NET 10 SDK，正在自动安装…");

        if (OperatingSystem.IsWindows())
        {
            var installed = false;
            if (await CommandExistsAsync("winget", cancellationToken))
            {
                var result = await RunProcessAsync("winget",
                    ["install", "--id", "Microsoft.DotNet.SDK.10", "-e", "--source", "winget",
                        "--accept-package-agreements", "--accept-source-agreements", "--silent"],
                    Environment.CurrentDirectory, progress, null, cancellationToken, false);
                installed = result.ExitCode == 0;
            }

            if (!installed)
            {
                var installDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
                var scriptPath = Path.Combine(Path.GetTempPath(), "dotnet-install.ps1");
                var command = $"$ErrorActionPreference='Stop'; Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' " +
                              $"-OutFile '{EscapePowerShell(scriptPath)}'; & '{EscapePowerShell(scriptPath)}' " +
                              $"-Channel 10.0 -InstallDir '{EscapePowerShell(installDirectory)}'";
                await RunCheckedAsync("powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
                    Environment.CurrentDirectory, progress, cancellationToken);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (await CommandExistsAsync("brew", cancellationToken))
            {
                await RunCheckedAsync("brew", ["install", "--cask", "dotnet-sdk"],
                    Environment.CurrentDirectory, progress, cancellationToken);
            }
            else
            {
                await InstallDotnetWithScriptAsync(progress, cancellationToken);
            }
        }
        else
        {
            await InstallDotnetWithScriptAsync(progress, cancellationToken);
        }

        dotnet = FindDotnetInCommonPaths() ?? await FindToolAsync("dotnet", "--version", cancellationToken);
        if (dotnet == null || !await HasDotnet10SdkAsync(dotnet, cancellationToken))
        {
            throw new InvalidOperationException(".NET 10 SDK 安装完成，但仍无法找到可用的 dotnet。请重新启动应用后重试。");
        }

        Report(progress, DevelopmentUpdateStage.InstallingTools, ".NET SDK 已就绪。", dotnet);
        return dotnet;
    }

    private async Task<bool> HasDotnet10SdkAsync(string dotnet, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(dotnet, ["--list-sdks"], Environment.CurrentDirectory, null, null,
            cancellationToken, false);
        return result.ExitCode == 0 &&
               result.Output.Split('\n')
                   .Select(x => x.Trim())
                   .Any(x => x.StartsWith("10.", StringComparison.Ordinal));
    }

    private async Task InstallDotnetWithScriptAsync(IProgress<DevelopmentUpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        const string script = """
            set -e
            if command -v curl >/dev/null 2>&1; then
                curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
            elif command -v wget >/dev/null 2>&1; then
                wget -qO /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
            else
                echo "缺少 curl 或 wget，无法下载 dotnet-install.sh" >&2
                exit 1
            fi
            bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
            """;
        await RunCheckedAsync("/bin/bash", ["-c", script], Environment.CurrentDirectory, progress,
            cancellationToken);
    }

    private async Task InstallLinuxPackageAsync(string package,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        var managers = new (string File, string[] Arguments)[]
        {
            ("/usr/bin/apt-get", ["install", "-y", package]),
            ("/usr/bin/dnf", ["install", "-y", package]),
            ("/usr/bin/pacman", ["-S", "--noconfirm", package]),
            ("/usr/bin/zypper", ["--non-interactive", "install", package])
        };

        var manager = managers.FirstOrDefault(x => File.Exists(x.File));
        if (manager.File == null)
        {
            throw new InvalidOperationException($"未找到可用的包管理器，无法自动安装 {package}。");
        }

        var (fileName, arguments) = Elevate(manager.File, manager.Arguments);
        if (package == "git" && manager.File.EndsWith("apt-get", StringComparison.Ordinal))
        {
            var (updateFile, updateArguments) = Elevate("/usr/bin/apt-get", ["update"]);
            await RunProcessAsync(updateFile, updateArguments, Environment.CurrentDirectory, progress, null,
                cancellationToken, false);
        }

        await RunCheckedAsync(fileName, arguments, Environment.CurrentDirectory, progress, cancellationToken);
    }

    private static (string FileName, string[] Arguments) Elevate(string fileName, string[] arguments)
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            if (File.Exists("/usr/bin/pkexec"))
            {
                return ("/usr/bin/pkexec", [fileName, .. arguments]);
            }

            return ("/usr/bin/sudo", ["-n", fileName, .. arguments]);
        }

        return (fileName, arguments);
    }

    private async Task PullAsync(string git, string repositoryRoot,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        Report(progress, DevelopmentUpdateStage.Pulling, "检查本地改动…");
        var status = await RunProcessAsync(git, ["status", "--porcelain"], repositoryRoot, progress, null,
            cancellationToken);
        var stashed = false;
        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            Report(progress, DevelopmentUpdateStage.Pulling, "检测到本地改动，先暂存…");
            var stash = await RunProcessAsync(git,
                ["stash", "push", "--include-untracked", "-m",
                    $"ClassIsland 自动更新备份 {DateTime.Now:yyyyMMdd-HHmmss}"],
                repositoryRoot, progress, null, cancellationToken);
            if (stash.ExitCode != 0)
            {
                throw new InvalidOperationException("本地改动暂存失败，已停止更新。\n" +
                                                    Tail(stash.Error + stash.Output));
            }

            stashed = true;
        }

        Report(progress, DevelopmentUpdateStage.Pulling, "拉取远程提交…");
        var fetch = await RunWithRetryAsync(git, ["fetch", "--all", "--prune"], repositoryRoot,
            progress, "拉取远程提交", NetworkRetryCount, cancellationToken);
        if (fetch.ExitCode != 0)
        {
            throw new InvalidOperationException("拉取远程提交失败。\n" + Tail(fetch.Error + fetch.Output));
        }

        var branch = await RunProcessAsync(git, ["rev-parse", "--abbrev-ref", "HEAD"], repositoryRoot,
            progress, null, cancellationToken);
        var upstream = await RunProcessAsync(git,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], repositoryRoot, progress, null,
            cancellationToken, false);
        if (upstream.ExitCode != 0 || string.IsNullOrWhiteSpace(upstream.Output))
        {
            Report(progress, DevelopmentUpdateStage.Pulling,
                $"当前分支 {branch.Output.Trim()} 没有上游分支，跳过 git pull，直接编译。");
        }
        else
        {
            Report(progress, DevelopmentUpdateStage.Pulling,
                $"更新 {branch.Output.Trim()} ← {upstream.Output.Trim()}…");
            var pull = await RunProcessAsync(git, ["pull", "--ff-only"], repositoryRoot, progress, null,
                cancellationToken);
            if (pull.ExitCode != 0)
            {
                // 本地与远端分叉时 --ff-only 必然失败。先尝试变基；变基失败就中止，
                // 不能让仓库停在一半的变基状态里，否则用户下次打开仓库会一脸问号。
                Report(progress, DevelopmentUpdateStage.Pulling, "快进拉取失败，尝试变基到远端…",
                    Tail(pull.Error + pull.Output, 10));
                var rebase = await RunProcessAsync(git, ["pull", "--rebase"], repositoryRoot, progress, null,
                    cancellationToken);
                if (rebase.ExitCode != 0)
                {
                    await RunProcessAsync(git, ["rebase", "--abort"], repositoryRoot, null, null,
                        cancellationToken, false);
                    throw new InvalidOperationException(
                        "git pull 失败：本地分支与远端存在冲突，已中止变基。请手动处理后重试。\n" +
                        Tail(rebase.Error + rebase.Output));
                }
            }
        }

        if (stashed)
        {
            Report(progress, DevelopmentUpdateStage.Pulling, "恢复本地改动…");
            var pop = await RunProcessAsync(git, ["stash", "pop"], repositoryRoot, progress, null,
                cancellationToken);
            if (pop.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "本地改动恢复失败，改动仍保存在 git stash 中（ClassIsland 自动更新备份）。请手动执行 git stash pop 处理。\n" +
                    Tail(pop.Error + pop.Output));
            }
        }
    }

    private async Task UpdateSubmodulesAsync(string git, string repositoryRoot,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(repositoryRoot, ".gitmodules")))
        {
            return;
        }

        Report(progress, DevelopmentUpdateStage.UpdatingSubmodules, "同步子模块配置…");
        await RunProcessAsync(git, ["submodule", "sync", "--recursive"], repositoryRoot, progress, null,
            cancellationToken, false);

        for (var attempt = 1; attempt <= SubmoduleRetryCount; attempt++)
        {
            Report(progress, DevelopmentUpdateStage.UpdatingSubmodules,
                $"初始化子模块（第 {attempt}/{SubmoduleRetryCount} 次）…");
            var environment = attempt >= 2
                ? new Dictionary<string, string>
                {
                    ["GIT_CONFIG_COUNT"] = "1",
                    ["GIT_CONFIG_KEY_0"] = "url.https://github.com/.insteadOf",
                    ["GIT_CONFIG_VALUE_0"] = "git@github.com:"
                }
                : null;
            var result = await RunProcessAsync(git, ["submodule", "update", "--init", "--recursive"],
                repositoryRoot, progress, environment, cancellationToken, false);
            if (result.ExitCode == 0)
            {
                Report(progress, DevelopmentUpdateStage.UpdatingSubmodules, "子模块已就绪。");
                return;
            }

            if (attempt < SubmoduleRetryCount)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                Report(progress, DevelopmentUpdateStage.UpdatingSubmodules,
                    $"子模块拉取失败，{delay.TotalSeconds:0} 秒后重试…", Tail(result.Error + result.Output));
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("子模块初始化失败，已重试 " + SubmoduleRetryCount +
                                                    " 次。请检查网络后重试。\n" + Tail(result.Error + result.Output));
            }
        }
    }

    private async Task<BuildOutput> BuildAsync(string dotnet, string repositoryRoot,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        var project = Path.Combine(repositoryRoot, "ClassIsland.Desktop", "ClassIsland.Desktop.csproj");
        if (!File.Exists(project))
        {
            throw new InvalidOperationException("未找到 ClassIsland.Desktop/ClassIsland.Desktop.csproj。");
        }

        // 应用很可能正从 ClassIsland.Desktop/bin/Release 下运行，直接编译回默认输出目录会
        // 撞上正在被占用的 exe 和 dll（Windows 上是硬性文件锁，Linux 上会替换正在映射的文件）。
        // 用 --artifacts-path 把中间产物和输出全部放到仓库外的独立目录，运行中的文件不参与编译。
        var artifactsPath = Path.Combine(Path.GetTempPath(), "ClassIsland-dev-build");
        Report(progress, DevelopmentUpdateStage.Building, "开始编译（Release）…", artifactsPath);
        var result = await RunWithRetryAsync(dotnet,
            ["build", project, "-c", "Release", "--nologo", "--artifacts-path", artifactsPath],
            repositoryRoot, progress, "编译", NetworkRetryCount, cancellationToken, IsNetworkFailure,
            DevelopmentUpdateStage.Building);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("编译失败。\n" + Tail(result.Error + result.Output, 40));
        }

        var executable = FindBuildExecutable(artifactsPath)
                         ?? throw new InvalidOperationException("编译成功，但未找到输出可执行文件。");
        var directory = Path.GetDirectoryName(executable)!;
        Report(progress, DevelopmentUpdateStage.Building, "编译完成。", executable);
        return new BuildOutput(executable, directory);
    }

    private static string? FindBuildExecutable(string artifactsPath)
    {
        var binRoot = Path.Combine(artifactsPath, "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        var fileName = OperatingSystem.IsWindows() ? "ClassIsland.Desktop.exe" : "ClassIsland.Desktop";
        return Directory.GetFiles(binRoot, fileName, SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void OpenDirectory(string directory)
    {
        try
        {
            var (fileName, argument) = OperatingSystem.IsWindows()
                ? ("explorer.exe", directory)
                : OperatingSystem.IsMacOS()
                    ? ("open", directory)
                    : ("xdg-open", directory);
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
        }
        catch
        {
            // 打开目录失败不影响后续启动。
        }
    }

    private static void LaunchAfterCurrentExit(string executable, string workingDirectory)
    {
        var processId = Environment.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            var command = $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue; " +
                          $"Start-Process -FilePath '{EscapePowerShell(executable)}' " +
                          $"-WorkingDirectory '{EscapePowerShell(workingDirectory)}'";
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            Process.Start(startInfo);
        }
        else
        {
            var command = $"while kill -0 {processId} 2>/dev/null; do sleep 1; done; " +
                          $"cd '{EscapeShell(workingDirectory)}' && nohup '{EscapeShell(executable)}' " +
                          ">/dev/null 2>&1 &";
            // 用 ArgumentList 传参：把整条命令塞进 Arguments 字符串时，
            // .NET 在 Unix 上会再做一次引号解析，路径里的空格和单引号会被拆坏。
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
            Process.Start(startInfo);
        }
    }

    private async Task<ProcessResult> RunWithRetryAsync(string fileName, string[] arguments,
        string workingDirectory, IProgress<DevelopmentUpdateProgress> progress, string description,
        int retryCount, CancellationToken cancellationToken, Func<ProcessResult, bool>? shouldRetry = null,
        DevelopmentUpdateStage stage = DevelopmentUpdateStage.Pulling)
    {
        ProcessResult result = new(-1, "", "");
        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            result = await RunProcessAsync(fileName, arguments, workingDirectory, progress, null,
                cancellationToken, false, stage);
            if (result.ExitCode == 0)
            {
                return result;
            }

            if (attempt >= retryCount || (shouldRetry != null && !shouldRetry(result)))
            {
                return result;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            Report(progress, stage,
                $"{description}失败，{delay.TotalSeconds:0} 秒后重试（第 {attempt}/{retryCount} 次）…",
                Tail(result.Error + result.Output, 10));
            await Task.Delay(delay, cancellationToken);
        }

        return result;
    }

    private static bool IsNetworkFailure(ProcessResult result)
    {
        var text = result.Error + result.Output;
        return text.Contains("NU1301", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unable to load the service index", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("The HTTP request was forbidden", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SSL", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunCheckedAsync(string fileName, string[] arguments, string workingDirectory,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken,
        DevelopmentUpdateStage stage = DevelopmentUpdateStage.Building)
    {
        var result = await RunProcessAsync(fileName, arguments, workingDirectory, progress, null,
            cancellationToken, false, stage);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} 执行失败（退出码 {result.ExitCode}）。\n" +
                                                Tail(result.Error + result.Output, 20));
        }
    }

    private async Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken)
    {
        return await FindToolAsync(command, "--version", cancellationToken) != null;
    }

    private async Task<string?> FindToolAsync(string fileName, string argument, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(fileName, [argument], Environment.CurrentDirectory, null, null,
            cancellationToken, false);
        return result.ExitCode == 0 ? fileName : null;
    }

    private async Task<ProcessResult> RunProcessAsync(string fileName, string[] arguments,
        string workingDirectory, IProgress<DevelopmentUpdateProgress>? progress,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken,
        bool throwOnStartFailure = true, DevelopmentUpdateStage stage = DevelopmentUpdateStage.Building)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            output.AppendLine(e.Data);
            progress?.Report(new DevelopmentUpdateProgress(stage, "", e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            error.AppendLine(e.Data);
            progress?.Report(new DevelopmentUpdateProgress(stage, "", e.Data));
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动进程 {fileName}。");
            }
        }
        catch (Win32Exception ex)
        {
            if (throwOnStartFailure)
            {
                throw new InvalidOperationException($"无法启动 {fileName}：{ex.Message}", ex);
            }

            return new ProcessResult(-1, "", ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // ignored
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static string Tail(string text, int lines = 20)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.TakeLast(lines));
    }

    private static string? FindGitInCommonPaths()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Git", "cmd", "git.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Git", "cmd", "git.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Git", "cmd", "git.exe"));
        }
        else
        {
            candidates.AddRange(["/usr/bin/git", "/usr/local/bin/git", "/opt/homebrew/bin/git"]);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindDotnetInCommonPaths()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "dotnet.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "dotnet", "dotnet.exe"));
        }
        else
        {
            candidates.AddRange([
                "/usr/bin/dotnet", "/usr/local/bin/dotnet", "/opt/homebrew/bin/dotnet",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet")
            ]);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private static string EscapeShell(string value) => value.Replace("'", "'\\''");

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed record BuildOutput(string Executable, string Directory);
}
