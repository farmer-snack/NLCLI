using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
public sealed class DevelopmentUpdateService(ILogger<DevelopmentUpdateService> logger,
    SettingsService settingsService)
{
    private const int SubmoduleRetryCount = 5;
    private const int NetworkRetryCount = 3;

    /// <summary>
    /// 直连拉取的尝试次数。用完仍失败就改用镜像源。
    /// </summary>
    private const int DirectFetchAttemptCount = 4;

    /// <summary>
    /// 源码仓库地址，写死在这里。
    /// 不能拿本地已有的 remote 来拉：remote 可能指向官方仓库或别的副仓库，
    /// 拉下来的是别人的代码，更新完编译出来的东西根本不是这个分支。
    /// </summary>
    private const string SourceRepositoryUrl = "https://github.com/farmer-snack/NLCLI.git";

    private const string SourceBranch = "develop/v2/misha-alpha";

    /// <summary>
    /// 专用于本功能的远端引用名。fetch 时直接写引用而不建立 remote，
    /// 避免和本地已有的 origin / 官方仓库 remote 混在一起。
    /// </summary>
    private const string UpdateRemoteName = "classisland-auto-update";

    /// <summary>
    /// GitHub 加速镜像。直连多次失败后按顺序尝试。
    /// </summary>
    private static readonly string[] SourceRepositoryMirrors =
    [
        "https://ghproxy.net/https://github.com/farmer-snack/NLCLI.git",
        "https://gh-proxy.com/https://github.com/farmer-snack/NLCLI.git",
        "https://ghfast.top/https://github.com/farmer-snack/NLCLI.git",
        "https://github.moeyy.xyz/https://github.com/farmer-snack/NLCLI.git",
        "https://hub.gitmirror.com/https://github.com/farmer-snack/NLCLI.git"
    ];

    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    private int _isPublishing;

    /// <summary>
    /// 当前正在运行的是“打包编译产物”（否则为“拉取更新并编译”）。
    /// 页面切换后 ViewModel 重建时用它恢复正确的按钮状态。
    /// 两个操作共用 <see cref="_isRunning"/> 闸门，不会同时进行。
    /// </summary>
    public bool IsPublishing => Volatile.Read(ref _isPublishing) == 1;

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
            var git = await EnsureGitAsync(progress, cancellationToken);
            var dotnet = await EnsureDotnetAsync(progress, cancellationToken);

            // 不再要求当前目录就是 Git 仓库：找不到就自己拉一份源码到独立目录，
            // 编译仍然在仓库里进行，新版本沿用当前工作目录启动，配置不会漂移。
            var branch = GetSelectedBranch();
            Report(progress, DevelopmentUpdateStage.Preparing, $"目标分支：{branch}");
            var repositoryRoot = await EnsureRepositoryAsync(git, branch, progress, cancellationToken);
            await SyncRepositoryAsync(git, repositoryRoot, branch, progress, cancellationToken);
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

    /// <summary>
    /// 调试页的“打包编译产物”功能。环境准备（补齐 Git / .NET SDK、定位并拉取仓库）
    /// 与“拉取更新并编译”完全一致；区别是把编译这一步换成直接调用 dotnet publish
    /// （参数照搬 build-publish.bat，自包含可选）：打包前清理旧的 out 目录、打包后删除
    /// 其中的 .pdb 文件，最后把 out 压成 out.zip 并打开。产物并非当前运行版本，不重启应用。
    /// </summary>
    public async Task RunPublishAsync(IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            throw new InvalidOperationException("开发构建正在进行中。");
        }
        Interlocked.Exchange(ref _isPublishing, 1);

        try
        {
            var git = await EnsureGitAsync(progress, cancellationToken);
            var dotnet = await EnsureDotnetAsync(progress, cancellationToken);

            var branch = GetSelectedBranch();
            Report(progress, DevelopmentUpdateStage.Preparing, $"目标分支：{branch}");
            var repositoryRoot = await EnsureRepositoryAsync(git, branch, progress, cancellationToken);
            await SyncRepositoryAsync(git, repositoryRoot, branch, progress, cancellationToken);
            await UpdateSubmodulesAsync(git, repositoryRoot, progress, cancellationToken);

            // 打包前必须先删掉旧的 out，避免上一次的残留文件混进本次产物。
            // 删除失败要立刻提示用户，不能带着旧文件继续打包。
            var outDirectory = Path.Combine(repositoryRoot, "out");
            await DeleteOutDirectoryAsync(outDirectory, progress, cancellationToken);

            // 打包这一步不执行仓库的 build-publish.bat，而是在应用内直接调用 dotnet publish，
            // 参数照搬该脚本里的那一套；是否自包含由用户在页面上选择（默认自包含）。
            var selfContained = settingsService.Settings.DevelopmentPublishSelfContained;
            await RunDotnetPublishAsync(dotnet, repositoryRoot, selfContained, progress, cancellationToken);

            // 打包完成后清理 out 中的调试符号文件。
            var removedPdb = DeletePdbFiles(outDirectory);
            var pdbNote = removedPdb > 0 ? $"，已清理 {removedPdb} 个 .pdb 文件" : "";

            // 把 out 目录压成 out.zip，并打开这个压缩包。
            var zipPath = await CreateZipArchiveAsync(outDirectory, repositoryRoot, progress, cancellationToken);
            Report(progress, DevelopmentUpdateStage.Completed, $"打包完成{pdbNote}，已生成 out.zip。", zipPath);
            OpenFile(zipPath);
        }
        catch (OperationCanceledException)
        {
            Report(progress, DevelopmentUpdateStage.Failed, "操作已取消。");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "打包编译产物失败。");
            Report(progress, DevelopmentUpdateStage.Failed, $"打包失败：{ex.Message}", ex.ToString());
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isPublishing, 0);
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    /// <summary>
    /// 打包前删除旧的 out 目录。与 clone 时的静默清理不同，这里删除失败必须中断并提示用户。
    /// </summary>
    private async Task DeleteOutDirectoryAsync(string outDirectory,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(outDirectory))
        {
            return;
        }

        Report(progress, DevelopmentUpdateStage.Preparing, "正在删除旧的 out 目录…", outDirectory);
        try
        {
            await Task.Run(() => Directory.Delete(outDirectory, true), cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法删除旧的 out 目录：{outDirectory}\n" +
                "该目录可能正被占用（例如文件管理器或正在运行的旧版本）。请关闭占用它的程序后重试。\n" +
                ex.Message, ex);
        }
    }

    /// <summary>
    /// 在应用内直接调用 dotnet publish 打包。参数照搬仓库 build-publish.bat：
    /// 自包含用 --self-contained true + ClassIsland_SelfContained=true，输出到 out；
    /// 非自包含则关掉这两项、保留 win-x64 目标与 PlatformTarget=x64（与自包含无关的平台常量）。
    /// </summary>
    private async Task RunDotnetPublishAsync(string dotnet, string repositoryRoot, bool selfContained,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken,
        bool allowAiFix = true)
    {
        var project = Path.Combine(repositoryRoot, "ClassIsland.Desktop", "ClassIsland.Desktop.csproj");
        if (!File.Exists(project))
        {
            throw new InvalidOperationException("未找到 ClassIsland.Desktop/ClassIsland.Desktop.csproj。");
        }

        var selfContainedText = selfContained ? "true" : "false";
        string[] arguments =
        [
            "publish", project,
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", selfContainedText,
            $"-p:ClassIsland_SelfContained={selfContainedText}",
            "-p:ClassIsland_PlatformTarget=x64",
            "-o", "out"
        ];

        var mode = selfContained ? "自包含" : "框架依赖（非自包含）";
        Report(progress, DevelopmentUpdateStage.Building, $"正在打包（{mode}）…", string.Join(' ', arguments));
        var result = await RunWithRetryAsync(dotnet, arguments, repositoryRoot, progress, "打包",
            NetworkRetryCount, cancellationToken, IsNetworkFailure, DevelopmentUpdateStage.Building);
        if (result.ExitCode != 0)
        {
            var error = result.Error + result.Output;
            // 程序自身能力已用尽（含网络重试），尝试交给 AI 修复一次；只重试一次，避免死循环。
            if (allowAiFix && await TryFixBuildWithAiAsync(Tail(error, 60), repositoryRoot, progress, cancellationToken))
            {
                Report(progress, DevelopmentUpdateStage.Building, "AI 已修复代码，正在重新打包…");
                await RunDotnetPublishAsync(dotnet, repositoryRoot, selfContained, progress, cancellationToken, false);
                return;
            }

            throw new InvalidOperationException("dotnet publish 打包失败。\n" + Tail(error, 40));
        }
    }

    /// <summary>
    /// 把 out 目录压成 out.zip（先删旧压缩包），返回压缩包路径。
    /// </summary>
    private async Task<string> CreateZipArchiveAsync(string outDirectory, string repositoryRoot,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(outDirectory))
        {
            throw new InvalidOperationException("out 目录不存在，无法生成压缩包。");
        }

        var zipPath = Path.Combine(repositoryRoot, "out.zip");
        Report(progress, DevelopmentUpdateStage.Building, "正在生成 out.zip…", zipPath);
        await Task.Run(() =>
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            ZipFile.CreateFromDirectory(outDirectory, zipPath, CompressionLevel.Optimal, false);
        }, cancellationToken);
        return zipPath;
    }

    /// <summary>
    /// 用系统默认程序打开一个文件（这里用于打开 out.zip 压缩包）。
    /// </summary>
    private static void OpenFile(string path)
    {
        try
        {
            var (fileName, argument) = OperatingSystem.IsWindows()
                ? ("explorer.exe", path)
                : OperatingSystem.IsMacOS()
                    ? ("open", path)
                    : ("xdg-open", path);
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
        }
        catch
        {
            // 打开压缩包失败不影响打包结果，路径已在状态消息里给出。
        }
    }

    /// <summary>
    /// 递归删除 out 目录中的 .pdb 调试符号文件，返回成功删除的数量。
    /// 单个文件删除失败（例如被占用）不影响整体打包，只记录到日志。
    /// </summary>
    private int DeletePdbFiles(string outDirectory)
    {
        if (!Directory.Exists(outDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(outDirectory, "*.pdb", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "无法删除调试符号文件 {File}。", file);
            }
        }
        return removed;
    }

    /// <summary>
    /// 编译失败且自身重试也无法解决时，尝试借助命令行 AI 自动修复：
    /// 安装 BUN → 通过 BUN 安装 OpenCode CLI → 探测免费模型（DeepSeek &gt; Mimo &gt; 混元4 Flash）
    /// → 把编译错误交给 AI，让它在仓库里直接改代码。
    /// <para>返回 true 表示 AI 已实际执行修复，调用方应重新编译一次；任何环节失败都返回 false，
    /// 由调用方抛出原始编译错误，不掩盖真正的问题。</para>
    /// </summary>
    private async Task<bool> TryFixBuildWithAiAsync(string buildError, string repositoryRoot,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            Report(progress, DevelopmentUpdateStage.Building,
                "编译失败，尝试用 AI 自动修复…", Tail(buildError, 8));
            var bun = await EnsureBunAsync(progress, cancellationToken);
            var opencode = await EnsureOpenCodeAsync(bun, progress, cancellationToken);
            var model = await SelectFreeModelAsync(opencode, bun, repositoryRoot, progress, cancellationToken);
            return await RunAiFixAsync(opencode, bun, model, buildError, repositoryRoot, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI 自动修复 compilation 失败。");
            Report(progress, DevelopmentUpdateStage.Building, $"AI 自动修复未成功：{ex.Message}", Tail(ex.ToString()));
            return false;
        }
    }

    /// <summary>
    /// 确保本机有 BUN：PATH 或常见位置找到就复用，否则按平台安装并安装到 ~/.bun/bin。
    /// </summary>
    private async Task<string> EnsureBunAsync(IProgress<DevelopmentUpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        var bun = await FindToolAsync("bun", "--version", cancellationToken) ?? FindBunInCommonPaths();
        if (bun != null)
        {
            Report(progress, DevelopmentUpdateStage.Building, "已检测到 BUN。", bun);
            return bun;
        }

        Report(progress, DevelopmentUpdateStage.Building, "未检测到 BUN，正在安装…");
        if (OperatingSystem.IsWindows())
        {
            var installed = false;
            if (await CommandExistsAsync("winget", cancellationToken))
            {
                var result = await RunProcessAsync("winget",
                    ["install", "--id", "Oven.Bun", "-e", "--source", "winget",
                        "--accept-package-agreements", "--accept-source-agreements", "--silent"],
                    Environment.CurrentDirectory, progress, null, cancellationToken, false);
                installed = result.ExitCode == 0;
            }

            if (!installed)
            {
                await RunCheckedAsync("powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-Command", "irm bun.sh/install.ps1 | iex"],
                    Environment.CurrentDirectory, progress, cancellationToken, DevelopmentUpdateStage.Building);
            }
        }
        else
        {
            const string script = """
                set -e
                if command -v curl >/dev/null 2>&1; then
                    curl -fsSL https://bun.sh/install | bash
                elif command -v wget >/dev/null 2>&1; then
                    wget -qO- https://bun.sh/install | bash
                else
                    echo "缺少 curl 或 wget，无法安装 BUN" >&2
                    exit 1
                fi
                """;
            await RunCheckedAsync("/bin/bash", ["-c", script], Environment.CurrentDirectory, progress,
                cancellationToken, DevelopmentUpdateStage.Building);
        }

        bun = FindBunInCommonPaths() ?? await FindToolAsync("bun", "--version", cancellationToken);
        if (bun == null)
        {
            throw new InvalidOperationException("BUN 安装完成，但仍找不到 bun 命令。请重新启动应用后重试。");
        }

        Report(progress, DevelopmentUpdateStage.Building, "BUN 已就绪。", bun);
        return bun;
    }

    /// <summary>
    /// 确保安装了命令行版 OpenCode：优先复用，否则用 BUN 全局安装 opencode-ai。
    /// </summary>
    private async Task<string> EnsureOpenCodeAsync(string bun, IProgress<DevelopmentUpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        var opencode = FindOpenCodeInCommonPaths();
        if (opencode != null && await IsToolRunnableAsync(opencode, "--version", bun, cancellationToken))
        {
            Report(progress, DevelopmentUpdateStage.Building, "已检测到命令行版 OpenCode。", opencode);
            return opencode;
        }

        Report(progress, DevelopmentUpdateStage.Building, "未检测到 OpenCode，正在通过 BUN 安装命令行版…");
        var env = BuildBunEnvironment(bun);
        var result = await RunProcessAsync(bun, ["install", "-g", "opencode-ai"],
            Environment.CurrentDirectory, progress, env, cancellationToken, false, DevelopmentUpdateStage.Building);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("通过 BUN 安装 OpenCode 失败。\n" + Tail(result.Error + result.Output));
        }

        opencode = FindOpenCodeInCommonPaths();
        if (opencode == null || !await IsToolRunnableAsync(opencode, "--version", bun, cancellationToken))
        {
            throw new InvalidOperationException("OpenCode 安装完成，但仍找不到可运行的 opencode 命令。请重新启动应用后重试。");
        }

        Report(progress, DevelopmentUpdateStage.Building, "OpenCode 已就绪。", opencode);
        return opencode;
    }

    /// <summary>
    /// 探测 OpenCode 自带的免费模型，按 DeepSeek &gt; Mimo &gt; 混元4 Flash 的优先级挑选一个。
    /// </summary>
    private async Task<string> SelectFreeModelAsync(string opencode, string bun, string repositoryRoot,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        var env = BuildBunEnvironment(bun, opencode);
        var result = await RunProcessAsync(opencode, ["models"], repositoryRoot, progress, env,
            cancellationToken, false, DevelopmentUpdateStage.Building);
        var models = ParseModelIds(result.Output);

        // 首次为空可能是模型缓存未加载，刷新后再取一次。
        if (models.Count == 0)
        {
            await RunProcessAsync(opencode, ["models", "--refresh"], repositoryRoot, progress, env,
                cancellationToken, false, DevelopmentUpdateStage.Building);
            result = await RunProcessAsync(opencode, ["models"], repositoryRoot, progress, env,
                cancellationToken, false, DevelopmentUpdateStage.Building);
            models = ParseModelIds(result.Output);
        }

        // 免费模型是内置 opencode provider 下、id 带 free 的条目。
        var freeModels = models
            .Where(x => x.StartsWith("opencode/", StringComparison.OrdinalIgnoreCase) &&
                        x.Contains("free", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (freeModels.Count == 0)
        {
            // 兼容把免费模型挂在别的 provider 下的情况。
            freeModels = models.Where(x => x.Contains("free", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var chosen = freeModels.FirstOrDefault(x => x.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                     ?? freeModels.FirstOrDefault(x => x.Contains("mimo", StringComparison.OrdinalIgnoreCase))
                     ?? freeModels.FirstOrDefault(x => x.Contains("hy4", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Contains("hunyuan", StringComparison.OrdinalIgnoreCase));
        if (chosen == null)
        {
            throw new InvalidOperationException(
                "未在 OpenCode 免费模型中找到 DeepSeek / Mimo / 混元4 Flash。\n" +
                "可用免费模型：" + (freeModels.Count > 0 ? string.Join(", ", freeModels) : "（无）"));
        }

        Report(progress, DevelopmentUpdateStage.Building, $"选用免费模型：{chosen}", chosen);
        return chosen;
    }

    /// <summary>
    /// 把编译错误作为提示词交给 OpenCode，在仓库目录里以非交互方式自动修复代码。
    /// </summary>
    private async Task<bool> RunAiFixAsync(string opencode, string bun, string model, string buildError,
        string repositoryRoot, IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        var env = BuildBunEnvironment(bun, opencode);
        // 非交互 run 模式下权限请求默认被拒绝，这里内联放开编辑/写入/命令执行权限，让 AI 能真正改代码。
        env["OPENCODE_PERMISSION"] = """{"edit":"allow","write":"allow","bash":"allow","patch":"allow"}""";

        var errorText = Tail(buildError, 60);
        const string instruction = """
            你在协助修复一个 .NET（Avalonia）桌面项目的编译错误。下面是 Release / win-x64 编译失败的完整错误输出。
            请直接在当前工作目录中定位并修改源码，修复全部编译错误，使 dotnet build 能通过。要求：
            1. 只修改必要的源码或项目文件，不要改动无关文件。
            2. 针对缺失命名空间、类型错误、API 变更、空引用等问题给出正确的修复代码。
            3. 不要通过删除功能、注释掉代码或屏蔽错误的方式绕过问题。
            4. 修复完成后，简要列出改动的文件。
            编译错误输出：
            ----
            """;
        var prompt = instruction + "\n" + errorText + "\n----";

        // 单条命令带超时保护，避免 AI 长时间无响应卡住整个更新流程。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        Report(progress, DevelopmentUpdateStage.Building, $"正在交由 AI（{model}）修复编译错误…");
        ProcessResult result;
        try
        {
            result = await RunProcessAsync(opencode,
                ["run", "-m", model, "--dir", repositoryRoot, prompt],
                repositoryRoot, progress, env, timeout.Token, false, DevelopmentUpdateStage.Building);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 这是 10 分钟超时触发的取消，不是用户主动取消。
            // 必须在这里转成“未修复”，否则会一路冒泡到最外层被误报成“操作已取消”，
            // 掩盖真正的编译错误。用户主动取消仍原样抛出。
            Report(progress, DevelopmentUpdateStage.Building, "AI 修复超时（超过 10 分钟），已跳过自动修复。");
            return false;
        }
        if (result.ExitCode != 0)
        {
            Report(progress, DevelopmentUpdateStage.Building, "AI 修复命令执行失败。",
                Tail(result.Error + result.Output, 20));
            return false;
        }

        Report(progress, DevelopmentUpdateStage.Building, "AI 已完成修复，准备重新编译。",
            Tail(result.Output, 12));
        return true;
    }

    /// <summary>
    /// 构造子进程环境：把 bun / opencode 所在目录加到 PATH 前面，保证其子进程能找到工具。
    /// </summary>
    private static Dictionary<string, string> BuildBunEnvironment(string bun, string? opencode = null)
    {
        var dirs = new List<string>();
        if (Path.IsPathRooted(bun) && Path.GetDirectoryName(bun) is { } bunDir)
        {
            dirs.Add(bunDir);
        }
        if (opencode != null && Path.IsPathRooted(opencode) && Path.GetDirectoryName(opencode) is { } ocDir)
        {
            dirs.Add(ocDir);
        }
        var env = new Dictionary<string, string>();
        if (dirs.Count > 0)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            env["PATH"] = string.Join(Path.PathSeparator, dirs) + Path.PathSeparator + currentPath;
        }
        return env;
    }

    private async Task<bool> IsToolRunnableAsync(string path, string argument, string bun,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(path, [argument], Environment.CurrentDirectory, null,
            BuildBunEnvironment(bun), cancellationToken, false);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// 从 opencode models 的输出里解析 provider/model 形式的模型 id。
    /// 输出可能带表格/描述，逐行取第一个含 “/” 的 token。
    /// </summary>
    private static List<string> ParseModelIds(string output)
    {
        var ids = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Contains('/') && !token.Contains(' '))
                {
                    ids.Add(token);
                    break;
                }
            }
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Report(IProgress<DevelopmentUpdateProgress> progress, DevelopmentUpdateStage stage,
        string message, string? detail = null)
    {
        LastMessage = message;
        progress.Report(new DevelopmentUpdateProgress(stage, message, detail));
    }

    /// <summary>
    /// 构造候选地址列表（含是否为镜像的标记，避免靠索引判断导致顺序反转后计数错乱）。
    /// 默认：官方直连重试 DirectFetchAttemptCount 次，再依次尝试镜像。
    /// 加速模式（DevelopmentUpdatePreferMirror）：跳过直连，先依次尝试所有镜像；镜像全部失败
    /// 仍回退直连，避免镜像内容过期时彻底无法更新。
    /// </summary>
    private IReadOnlyList<(string Url, bool IsMirror)> BuildSourceCandidates()
    {
        var preferMirror = settingsService.Settings.DevelopmentUpdatePreferMirror;
        var direct = Enumerable.Range(0, DirectFetchAttemptCount)
            .Select(_ => (Url: SourceRepositoryUrl, IsMirror: false));
        var mirrors = SourceRepositoryMirrors.Select(url => (Url: url, IsMirror: true));

        List<(string Url, bool IsMirror)> candidates = preferMirror
            ? mirrors.Concat(direct).ToList()
            : direct.Concat(mirrors).ToList();
        return candidates;
    }

    private string GetSelectedBranch()
    {
        var branch = settingsService.Settings.DevelopmentUpdateBranch;
        return string.IsNullOrWhiteSpace(branch) ? SourceBranch : branch.Trim();
    }

    /// <summary>
    /// 探测远程有哪些分支。只按写死的仓库地址查询，不碰本地配置的任何 remote。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRemoteBranchesAsync(CancellationToken cancellationToken)
    {
        var git = await FindToolAsync("git", "--version", cancellationToken) ?? FindGitInCommonPaths();
        if (git == null)
        {
            return [];
        }

        foreach (var candidate in BuildSourceCandidates())
        {
            var result = await RunProcessAsync(git, ["ls-remote", "--heads", candidate.Url],
                Environment.CurrentDirectory, null, null, cancellationToken, false);
            if (result.ExitCode != 0)
            {
                continue;
            }

            var branches = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                .Select(parts => parts[1]["refs/heads/".Length..])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name == SourceBranch ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (branches.Count > 0)
            {
                return branches;
            }
        }

        return [];
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

    /// <summary>
    /// 获取源码工作目录。优先复用当前/父目录里已有的本仓库；
    /// 找不到时就用固定目录，并由调用方按写死的地址 clone 一份。
    /// </summary>
    private static string GetSourceDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(local, "ClassIsland", "dev-update-source");
    }

    private static bool IsClassIslandRepository(string root)
    {
        return File.Exists(Path.Combine(root, "ClassIsland.Desktop", "ClassIsland.Desktop.csproj"));
    }

    /// <summary>
    /// 确保有一份可用的源码仓库。当前目录树里找到本仓库就直接用；
    /// 否则在固定目录里按写死的地址 clone（直连失败自动切镜像）。
    /// </summary>
    private async Task<string> EnsureRepositoryAsync(string git, string branch,
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken)
    {
        var existing = FindRepositoryRoot();
        if (existing != null && IsClassIslandRepository(existing))
        {
            Report(progress, DevelopmentUpdateStage.Preparing, "已定位本地源码仓库", existing);
            return existing;
        }

        var directory = GetSourceDirectory();
        if (Directory.Exists(Path.Combine(directory, ".git")) && IsClassIslandRepository(directory))
        {
            Report(progress, DevelopmentUpdateStage.Preparing, "复用已下载的源码仓库", directory);
            return directory;
        }

        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);
        var candidates = BuildSourceCandidates();
        var mirrorAttempt = 0;
        var directAttempt = 0;
        ProcessResult last = new(-1, "", "");
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var label = candidate.IsMirror
                ? $"尝试镜像 {++mirrorAttempt}/{SourceRepositoryMirrors.Length} 下载源码…"
                : $"正在下载源码（官方直连第 {++directAttempt}/{DirectFetchAttemptCount} 次）…";
            Report(progress, DevelopmentUpdateStage.Preparing, label, candidate.Url);

            // clone 目标目录必须为空，失败后清掉残留再试下一个地址。
            TryDeleteDirectory(directory);
            last = await RunProcessAsync(git,
                ["clone", "--branch", branch, "--single-branch", "--no-tags", candidate.Url, directory],
                parent, progress, null, cancellationToken, false);
            if (last.ExitCode == 0 && IsClassIslandRepository(directory))
            {
                Report(progress, DevelopmentUpdateStage.Preparing, "源码已下载", directory);
                return directory;
            }

            if (i + 1 < candidates.Count)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(i, 5))));
                Report(progress, DevelopmentUpdateStage.Preparing,
                    $"下载失败，{delay.TotalSeconds:0} 秒后重试…", Tail(last.Error + last.Output, 8));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("源码下载失败，直连和所有镜像都未成功。\n" +
                                            Tail(last.Error + last.Output));
    }

    /// <summary>
    /// 从镜像地址里取出「前缀 + https://github.com/」，用于 git 的 insteadOf 改写。
    /// 例如 https://ghproxy.net/https://github.com/xxx.git → https://ghproxy.net/https://github.com/
    /// </summary>
    private static string GetMirrorPrefix(string mirrorUrl)
    {
        const string marker = "https://github.com/";
        var index = mirrorUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? mirrorUrl[..(index + marker.Length)] : mirrorUrl;
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
            // 删不掉时 clone 自己会报更明确的错，这里不掩盖主流程。
        }
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

    private async Task SyncRepositoryAsync(string git, string repositoryRoot, string branch,
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

        // 只按写死的仓库地址拉取目标分支，不枚举、不使用本地已有的任何 remote。
        // remote 可能指向官方仓库或别的副仓库，用它会把错误的代码编译进去。
        var candidates = BuildSourceCandidates();
        var mirrorAttempt = 0;
        var directAttempt = 0;
        ProcessResult fetch = new(-1, "", "");
        var fetched = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var label = candidate.IsMirror
                ? $"尝试镜像 {++mirrorAttempt}/{SourceRepositoryMirrors.Length} 拉取…"
                : $"拉取 {branch}（官方直连第 {++directAttempt}/{DirectFetchAttemptCount} 次）…";
            Report(progress, DevelopmentUpdateStage.Pulling, label, candidate.Url);
            fetch = await RunProcessAsync(git,
                ["fetch", "--no-tags", "--prune", candidate.Url, $"+refs/heads/{branch}:refs/remotes/{UpdateRemoteName}/{branch}"],
                repositoryRoot, progress, null, cancellationToken, false);
            if (fetch.ExitCode == 0)
            {
                fetched = true;
                break;
            }

            if (i + 1 < candidates.Count)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(i, 5))));
                Report(progress, DevelopmentUpdateStage.Pulling,
                    $"拉取失败，{delay.TotalSeconds:0} 秒后重试…", Tail(fetch.Error + fetch.Output, 8));
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (!fetched)
        {
            throw new InvalidOperationException($"拉取分支 {branch} 失败，直连和所有镜像都未成功。\n" +
                                                Tail(fetch.Error + fetch.Output));
        }

        var remoteRef = $"{UpdateRemoteName}/{branch}";
        // 切到目标分支：本地没有就基于远端引用创建，有就切过去再对齐。
        var current = await RunProcessAsync(git, ["rev-parse", "--abbrev-ref", "HEAD"], repositoryRoot,
            progress, null, cancellationToken);
        if (string.Equals(current.Output.Trim(), branch, StringComparison.Ordinal))
        {
            var merge = await RunProcessAsync(git, ["merge", "--ff-only", remoteRef], repositoryRoot, progress, null,
                cancellationToken);
            if (merge.ExitCode != 0)
            {
                // 本地与远端分叉时 --ff-only 必然失败。先尝试变基；变基失败就中止并回滚，
                // 不能让仓库停在一半的变基状态里。
                Report(progress, DevelopmentUpdateStage.Pulling, "快进失败，尝试变基到远端…",
                    Tail(merge.Error + merge.Output, 10));
                var rebase = await RunProcessAsync(git, ["rebase", remoteRef], repositoryRoot, progress, null,
                    cancellationToken);
                if (rebase.ExitCode != 0)
                {
                    await RunProcessAsync(git, ["rebase", "--abort"], repositoryRoot, null, null,
                        cancellationToken, false);
                    throw new InvalidOperationException(
                        "更新失败：本地分支与远端存在冲突，已中止变基。请手动处理后重试。\n" +
                        Tail(rebase.Error + rebase.Output));
                }
            }
        }
        else
        {
            var checkout = await RunProcessAsync(git, ["checkout", "-B", branch, remoteRef], repositoryRoot,
                progress, null, cancellationToken);
            if (checkout.ExitCode != 0)
            {
                throw new InvalidOperationException($"切换到分支 {branch} 失败。\n" +
                                                    Tail(checkout.Error + checkout.Output));
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
            // 第 2 次起把 SSH 改写成 HTTPS（照顾没配 SSH key 的机器）；
            // 更靠后的重试叠加上镜像前缀，绕开直连不通的网络环境。
            Dictionary<string, string>? environment = null;
            var mirrorIndex = attempt - 3; // attempt 4 -> 镜像 0
            if (attempt >= 2 || mirrorIndex >= 0)
            {
                environment = new Dictionary<string, string>();
                if (mirrorIndex >= 0 && mirrorIndex < SourceRepositoryMirrors.Length)
                {
                    var prefix = GetMirrorPrefix(SourceRepositoryMirrors[mirrorIndex]);
                    environment["GIT_CONFIG_COUNT"] = "2";
                    environment["GIT_CONFIG_KEY_0"] = $"url.{prefix}.insteadOf";
                    environment["GIT_CONFIG_VALUE_0"] = "https://github.com/";
                    environment["GIT_CONFIG_KEY_1"] = $"url.{prefix}.insteadOf";
                    environment["GIT_CONFIG_VALUE_1"] = "git@github.com:";
                }
                else
                {
                    environment["GIT_CONFIG_COUNT"] = "1";
                    environment["GIT_CONFIG_KEY_0"] = "url.https://github.com/.insteadOf";
                    environment["GIT_CONFIG_VALUE_0"] = "git@github.com:";
                }
            }
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
        IProgress<DevelopmentUpdateProgress> progress, CancellationToken cancellationToken,
        bool allowAiFix = true)
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
            var error = result.Error + result.Output;
            // 程序自身能力已用尽（含网络重试），尝试交给 AI 修复一次；只重试一次，避免死循环。
            if (allowAiFix && await TryFixBuildWithAiAsync(Tail(error, 60), repositoryRoot, progress, cancellationToken))
            {
                Report(progress, DevelopmentUpdateStage.Building, "AI 已修复代码，正在重新编译…");
                return await BuildAsync(dotnet, repositoryRoot, progress, cancellationToken, false);
            }

            throw new InvalidOperationException("编译失败。\n" + Tail(error, 40));
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

    /// <summary>
    /// BUN 的默认安装位置：Windows 走 %USERPROFILE%\.bun\bin，Unix 走 ~/.bun/bin。
    /// </summary>
    private static string? FindBunInCommonPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".bun", "bin", OperatingSystem.IsWindows() ? "bun.exe" : "bun"),
            Path.Combine(home, ".bun", "bin", OperatingSystem.IsWindows() ? "bun.cmd" : "bun")
        };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "bun", "bun.exe"));
        }
        else
        {
            candidates.AddRange(["/usr/local/bin/bun", "/usr/bin/bun"]);
        }
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>
    /// OpenCode CLI 的常见位置：BUN 全局 bin（~/.bun/bin）优先，其次是 npm 全局目录。
    /// Windows 上 bun 的全局命令是 .cmd / .exe 垫片。
    /// </summary>
    private static string? FindOpenCodeInCommonPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var bunBin = Path.Combine(home, ".bun", "bin");
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange([
                Path.Combine(bunBin, "opencode.cmd"),
                Path.Combine(bunBin, "opencode.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "opencode.cmd")
            ]);
        }
        else
        {
            candidates.AddRange([
                Path.Combine(bunBin, "opencode"),
                Path.Combine(home, ".npm", "global", "bin", "opencode"),
                "/usr/local/bin/opencode", "/usr/bin/opencode"
            ]);
        }
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private static string EscapeShell(string value) => value.Replace("'", "'\\''");

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed record BuildOutput(string Executable, string Directory);
}
