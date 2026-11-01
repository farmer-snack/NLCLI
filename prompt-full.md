# 任务：把 ClassIsland 官方分支的最新代码同步到我的仓库，并处理冲突

> **这是完整版（Windows / Linux / macOS 三平台通用）**，留作备用。
> 日常请用 `prompt-windows.md` 或 `prompt-linux.md`——平台相关部分已经写死，不用判断。

你是一名精通 Git 的自动化工程助手。请严格按下面的流程执行一次「上游同步」，全程不要跳步、不要自行简化。

## ★ 可修改参数（**用户只改这一块**）

```
# ↓↓↓ 需要调整就改这里，其余内容一个字都不用动 ↓↓↓
FAST            = 1
PUSH_MODE       = auto
UPSTREAM_BRANCH = develop/v2/misha-alpha
TARGET_PROJ     = ClassIsland.Desktop/ClassIsland.Desktop.csproj
LOCAL_REPO      = .
# 附加需求：留空 = 只同步；填了 = 同步全部做完后，再按这句话继续开发（也可以直接在对话里跟我说）
CUSTOM_TASK     =
# ↑↑↑ 改完保存，整份文件发给 AI ↑↑↑
```

| 参数 | 可选值 | 含义 |
|---|---|---|
| `FAST` | `1` / `0` | `1`=快速通道（跳过磁盘/LFS 检查、构建只跑一次增量）；`0`=完整检查（构建跑两遍、检查换行符） |
| `PUSH_MODE` | `auto` / `direct` / `patch` | `auto`=能推就推、没权限自动导补丁；`direct`=只推，失败就报错；`patch`=不推，直接导补丁 |
| `UPSTREAM_BRANCH` | 分支名 | 默认 `develop/v2/misha-alpha`；官方若删了这个分支，AI 会自动回退 `master` |
| `TARGET_PROJ` | 项目路径 | 构建验证的目标；默认桌面宿主，会连带编译 Core 和主项目 |
| `LOCAL_REPO` | 路径 | `.` = 在哪个目录启动就在哪个目录找仓库 |
| `CUSTOM_TASK` | 一句话需求 / 留空 | 留空 = 只同步。填了就在**同步提交并推送完成之后**再做定制开发（见 1.5 / 13.9）；也可以不填，直接在对话里说 |

**AI 必须遵守的三条**：

1. **第 0 步里这五项都要用上表的值**：`LOCAL_REPO`（`cd` 那行）、`UPSTREAM_BRANCH`（赋值那行）、
   `FAST` / `PUSH_MODE` / `TARGET_PROJ`（环境文件的 heredoc 里）。
   **不许照抄代码块里的默认值**——用户改过之后，代码块里的默认值就不作数了。
2. **如果用户在对话里另外指定了参数**（例如「这次 FAST=0」「用 patch 模式」），**以用户当场说的为准**，
   并且要在汇报的第一行注明「本次参数：FAST=0, PUSH_MODE=patch …」。
3. **`CUSTOM_TASK` 不是 shell 变量**：不要写进环境文件、不要 `export`、不要拿它做 `[ -n ... ]` 之外的花样。
   它和「对话里现场提的需求」等价——**两者只要有一个非空，就必须执行 13.9**；两个都空才跳过。
   **绝对不许因为"这轮只说了加功能、没说同步"就把整轮跳过**（见 1.5 的情形 C）。

## 0. 基本事实（已核实，直接使用，不要重新质疑）

- 官方仓库：https://github.com/ClassIsland/ClassIsland.git
- 我自己的仓库：https://github.com/farmer-snack/NLCLI.git
- 要同步的官方分支：develop/v2/misha-alpha
- 该分支 `.github/workflows/` 下存在以下 GitHub Actions 工作流文件：
  build_release.yml、close_stale_issues.yml、issues_similarity_analysis.yml、qodana_code_quality.yml
- 工程语言：C# / .NET（Avalonia 桌面应用），官方默认分支为 master

## 0.1 执行前提：每个命令块都是独立 shell

**这是整份提示词最容易翻车的地方，务必先读。**

AI 编程工具（Claude Code / Cursor / Codex）执行命令时，**每次调用通常是全新的 shell**：

- `cd` 不保留——上一条刚到仓库根目录，下一条又回到默认目录
- 变量不保留——上一条 `MY_REMOTE=origin`，下一条它就是空字符串

不管这件事，会出现这类事故：

| 你写的命令 | 变量丢失后实际执行 | 后果 |
|---|---|---|
| `git push "$MY_REMOTE" "HEAD:$MY_BRANCH"` | `git push "HEAD:"` | 报 `invalid refspec`，推送直接失败 |
| `git checkout "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH" -- f` | `git checkout "/" -- f` | 直接失败 |
| `git fetch "$UPSTREAM_REMOTE" --prune` | `git fetch --prune` | 拉错了 remote |
| `cd "$REPO_ROOT"` | `cd ""` | 停在当前目录，后续全错位 |

**解决办法：第 0 步把变量固化成环境文件，之后每个命令块第一行都 source 它。**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
```

四条规矩：

1. **所有实际执行的命令块**，第一行都写上面这句（示例、模板、示意代码除外）。
2. 中途新算出来的变量（`BASE`、`PRE_MERGE_HEAD`、`UPSTREAM_HEAD`、`LAST_SYNCED_SHA`、`BRANCH_FALLBACK`），
   算完立刻追加进环境文件：
   ```
   echo "export BASE=$BASE" >> /tmp/upstream-sync.env
   ```
3. **危险命令前必须断言变量非空**——`git push`、`git checkout`、`git rm` 前加：
   ```
   [ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空，先 source /tmp/upstream-sync.env"; exit 1; }
   ```
4. 某条命令报「参数为空 / 路径不存在」时，**第一反应是检查有没有 source**，不要先去猜业务逻辑。

**为什么模板里有 `: "${REPO_ROOT:?...}"` 这一行**——因为 `cd ""` 在 bash 里**返回 0、不报错**，
它只是原地不动。也就是说变量丢了的时候，`cd "$REPO_ROOT"` 会"成功"，然后后面的 `git checkout`、
`git reset` 全部在**错误的目录**里执行。这一行是唯一能在源头拦住它的地方，不要删。

## 0.2 平台适配（Windows / Linux / macOS 都要能跑，**不能只按 Linux 写**）

这套流程在三种机器上都要成立：**Linux、macOS、Windows**。它们不是"路径斜杠不同"这么简单，
下面每一条都是实测会让人卡住的地方。

### 0.2.0 Windows 上必须先解决的一件事：命令到底进了哪个 shell

**这是 Windows 上最容易整轮白跑的地方。**

Windows 上 AI 编程工具（Cursor、Claude Code、Codex）默认用 **PowerShell** 执行命令。
本文档所有命令都是 bash 语法，在 PowerShell 里第一条 `source` 就报错，后面全废。

**所以开工前先做这一步，二选一：**

**方式 A（推荐，最省事）**

1. 在仓库目录右键 → **Git Bash Here**（Git for Windows 自带，不需要 WSL）
2. **在这个 Git Bash 窗口里启动你的 AI 工具**（`claude`、`codex` 等 CLI），它会继承 bash 环境
3. Cursor / VS Code 这类 GUI：把默认终端改成 Git Bash
   （`Ctrl+Shift+P` → `Terminal: Select Default Profile` → 选 `Git Bash`）

**方式 B（只能用 PowerShell 时的兜底）**

让 AI 把每个命令块**整体交给 bash 执行**，不要逐行敲：

```powershell
$code = @'
<把文档里的整个命令块原样粘在这里>
'@
# 关键三点，缺一不可：
#   1) 换行统一成 LF——Set-Content 会按 CRLF 写，bash 会把 CR 当成命令的一部分（报 $'\r': command not found）
#   2) 存成不带 BOM 的 UTF-8——PS 5.1 的 -Encoding utf8 是带 BOM 的，BOM 会让首行报错
#   3) 路径别手打：用 $env:ProgramFiles 拼。PowerShell 双引号里反斜杠不是转义符，
#      但复制 / 渲染 / 中转的某一层会把 "\bin\bash.exe" 里的 \b 当成退格吃掉——
#      本文档第一版就踩过，显示成了 Gitinash.exe。写完先回头核对这一行路径。
$code = $code -replace "`r`n", "`n"
[IO.File]::WriteAllText("$env:TEMP\step.sh", $code, (New-Object Text.UTF8Encoding($false)))
$env:PATH = "$env:ProgramFiles\Git\mingw64\bin;$env:ProgramFiles\Git\usr\bin;$env:PATH"
& "$env:ProgramFiles\Git\bin\bash.exe" --noprofile --norc "$env:TEMP\step.sh"
```

**为什么不能写 `Set-Content -Encoding utf8`**：Windows PowerShell 5.1（Win10/11 自带那个）
的 `utf8` 就是**带 BOM** 的 UTF-8，而且它按系统换行（CRLF）写。这样生成的文件在 bash 里
第一行就会报 `$'\r': command not found`，或者 BOM 直接让整行变成非法命令。

**注意 `bash -l` 不会改变工作目录**：`bash 脚本.sh` 的工作目录继承调用者，`-l` 只是多读一个
profile。如果你在 PowerShell 里执行，当前目录很可能是 `C:\Windows\System32` 或工具自己的目录，
第 0 步的 `cd .` 就会失败。要么先 `Set-Location 你的仓库`，要么把脚本改成
`& "$env:ProgramFiles\Git\bin\bash.exe" -c "cd '/d/NLCLI' && bash '$env:TEMP/step.sh'"`。

**给 AI 的硬规则**：如果你（AI）是在 PowerShell 里执行命令，**不要试图把 bash 命令直接丢进 PowerShell**，
也不要自己"翻译"成 PowerShell 版本（`$env:X`、`Get-ChildItem` 那套会和文档逻辑对不上）。
按方式 B 整体交给 bash，或者停下来告诉我需要换 Git Bash。

### 0.2.1 判定当前 shell（第 0 步第一条命令）

**必须用 bash**：Linux/macOS 的终端、Windows 的 **Git Bash**（推荐）或 **WSL**。

**不支持**：PowerShell / CMD 原生。文档用了 `source`、`set -o pipefail`、`${PIPESTATUS[0]}`、
`$'\t'`、bash 数组，这些在 PowerShell 里一个都不成立。

```
if [ -n "$BASH_VERSION" ]; then
  echo "SHELL=OK bash $BASH_VERSION"
else
  echo "FAIL: 当前不是 bash（$0）。Windows 请改用 Git Bash：仓库目录右键 → Git Bash Here，在里面重跑"
  exit 1
fi
```

### 0.2.2 平台判定与差异表

```
case "$(uname -s)" in
  Linux*)               PLATFORM=linux ;;
  Darwin*)              PLATFORM=macos ;;
  MINGW*|MSYS*|CYGWIN*) PLATFORM=windows_gitbash ;;
  *)                    PLATFORM=unknown ;;
esac
echo "PLATFORM=$PLATFORM"
```

| 事项 | Linux | macOS | Windows（Git Bash） | 本文档怎么处理 |
|---|---|---|---|---|
| **Git 版本** | **必须 ≥ 2.20** | 系统自带通常够（用 `git --version` 确认） | 老机器上可能是 msysgit 1.8 | 本文档要求 **Git ≥ 2.20**：`:(literal)` 要 1.9+、`-c core.quotePath` 要 2.0+、`get-url --all` 要 2.7+、`--ignore-cr-at-eol` 要 2.10+。第 0 步会解析版本，低于 2.20 直接 FAIL，不带着旧 Git 硬跑 |
| AI 工具的默认 shell | bash | zsh | **PowerShell** | 见 0.2.0：Windows 必须在 Git Bash 里启动工具，或整段交给 `bash` 执行；macOS 见 0.2.6 |
| 临时目录 | `/tmp` | `/tmp` | `/tmp` 由 MSYS 映射到 `%TEMP%`，**可写** | 统一用字面量 `/tmp/upstream-sync`（三平台都可用），不要自己拼 TMPDIR |
| `.git` 换行 | LF | LF | 默认 `core.autocrlf=true`，会把整个仓库判成"全改" | 第 0 步对**本仓库**设 `core.autocrlf=false` |
| 路径长度 | 无限制 | 无限制 | 260 字符上限，深层目录会报 `Filename too long` | 第 0 步设 `core.longpaths=true`；**克隆/仓库放在浅目录**（如 `D:\NLCLI`），别放 `D:\Projects\2026\xxx\NLCLI`——这个仓库最长路径已经 100+ 字符 |
| 执行位 | 有效 | 有效 | `core.filemode=false`，`chmod +x` 基本无效 | 落回文件时**不要**指望 chmod 能修好权限 |
| 凭据弹窗 | 终端提示 | 终端提示 | Git Credential Manager 会**弹 GUI 窗口**，AI 的命令直接挂死 | 一律加 `GIT_TERMINAL_PROMPT=0` + `GCM_INTERACTIVE=never` |
| 文件名大小写 | 敏感 | 默认不敏感 | 不敏感 | `fixxed.txt` / `Fixxed.txt` 在 Windows 是同一个文件，探测时以实际文件名为准 |
| 非法文件名 | 几乎不限制 | `:` 受限 | `: * ? " < > \|` 都不允许 | 上游新增这类文件名时 checkout 会失败 → 停下问我 |
| 符号链接 | 支持 | 支持 | 需开发者模式/管理员，默认建不了 | 上游新增 symlink 时可能失败 → 停下问我 |
| 中文路径 | OK | OK | OK，但控制台默认 GBK 会乱码 | 读取 git 输出时不要靠"看起来正常"判断，要看退出码 |
| 文件占用 | 少见 | 少见 | IDE/杀软锁定 `bin/`、`obj/` → 构建报 `being used by another process` | 构建失败先问是不是开着 Visual Studio |

### 0.2.3 Windows 上必须做的四项设置（只改本仓库，不动全局）

**这里只是说明，不要单独执行这一节**——真正设置它们的是第 0 步那条完整命令块
（`git config core.autocrlf / core.eol / core.longpaths / core.filemode` 四行在里面）。
单独跑一个 `if [ "$PLATFORM" = "windows_gitbash" ]` 片段是没用的：每条命令都是新 shell，
`$PLATFORM` 到那一块早就空了，`if` 恒假、什么都不做，但屏幕上看不出任何异常。

四项的含义：

| 配置 | 为什么必须 | 不设会怎样 |
|---|---|---|
| `core.autocrlf=false` | 否则 CRLF 让 diff 里出现几百个"假改动" | 文件分类把整仓算成"本地定制" |
| `core.eol=lf` | `.gitattributes` 里 `* text=auto` 时，检出的换行由它决定 | 工作区永远脏，第 0 步过不去 |
| `core.longpaths=true` | 绕过 260 字符路径上限 | 深层目录 checkout 直接失败 |
| `core.filemode=false` | Windows 上执行位不可信 | 无意义的 mode 变更混进 diff |

**为什么 `core.eol` 必须一起设**：这个仓库的 `.gitattributes` 里有 `* text=auto`。
凡是 `text` 属性生效的文件，检出时的换行由 `core.eol` 决定（Windows 默认 `native` 就是 CRLF），
`core.autocrlf=false` 压不住它。只设 autocrlf 的话，`git checkout-index -a -f` 出来的还是 CRLF，
和索引里的 LF 永远对不上——工作区"永远脏"，第 0 步直接过不去。

**不要用 `--global`**：这些设置只该对这个仓库生效，别污染我其它项目的配置。

### 0.2.4 环境文件本身也要防 CRLF

Windows 上如果环境文件被写成 CRLF，`source` 会报 `$'\r': command not found`，
而且报错信息很不直观，容易误判成"变量没落盘"。所以**每次写完全局环境文件后，立刻转成 LF**：

```
# 临时文件必须放在 /tmp/upstream-sync/ 目录之外：
# 第 6 步的三方合并会 rm -rf /tmp/upstream-sync，放里面的话环境文件会被一起删掉
tr -d '\r' < /tmp/upstream-sync.env > /tmp/upstream-sync.env.tmp \
  && mv /tmp/upstream-sync.env.tmp /tmp/upstream-sync.env || {
    echo "FAIL: 环境文件转 LF 失败（/tmp 不可写？）"; exit 1; }
```

另外，**`source` 之前必须先验一遍有没有残留的 `\r`**——因为 `export FAST=1\r` 在 bash 里
**不报错**，变量的值会变成 `"1\r"`，后面 `[ "$FAST" = "1" ]` 静默走 else 分支。

同理，任何 `.sh` 脚本、`.git/hooks/*` 如果被你改动过，也要保证是 LF。

### 0.2.5 命令写法上的跨平台约束

| 不要用 | 原因 | 改用 |
|---|---|---|
| `date -Iseconds` | macOS 的 BSD date 不认 | `date +%Y-%m-%dT%H:%M:%S%z` |
| `stat -c %s` | macOS 要 `stat -f %z` | `wc -c < 文件` |
| `sed -i 's/x/y/'` | macOS 必须写 `sed -i ''` | 用 `tr`、`awk` 或临时文件+`mv` |
| `readlink -f` | macOS 没有 | `cd "$(dirname "$f")" && pwd` |
| `grep -P` | 部分平台不支持 | `grep -E` |
| `ls -l --time-style` | 不通用 | 只用 `ls -l` |
| 路径写死 `C:\...` 或 `/home/...` | 三平台不通用 | 一律 `git rev-parse --show-toplevel` 取 |

### 0.2.6 三个平台各怎么开始（这段也要转述给我）

| 平台 | 怎么做 | 坑 |
|---|---|---|
| **Linux** | 普通终端，`bash` 即可 | 几乎没有 |
| **macOS** | 用 `bash -l` 启动 AI 工具；或让 AI 用 `bash -c "…"` 包住每个命令块 | 默认是 zsh，`BASH_VERSION` 为空，第 0 步会直接判失败。**单独在终端敲 `exec bash` 没用**——每个命令块都是新 shell，那个子 bash 立刻退出 |
| **Windows** | 仓库目录右键 → **Git Bash Here**，在这个窗口里启动 AI 工具 | 默认是 PowerShell；**不需要 WSL**，Git for Windows 自带的 Git Bash 就够 |

Windows 具体两种做法见 **0.2.0**，这里不重复。

## 0.3 remote 身份判定（**每轮必须先做，以 URL 为准，绝不看名字**）

> **铁律 1**：这是整份提示词里最不能出错的地方。remote 名字随便叫，URL 说了算。

这台机器上的 remote 名字我有可能搞反（`origin` 和 `upstream` 互换）。所以**不要相信名字**，
按 URL 内容重新识别，把结果存进变量，后面所有命令只用变量，不再出现写死的 remote 名：

- URL 含 `farmer-snack/NLCLI`（大小写不敏感） → 这是**我自己的仓库**，赋值给 `MY_REMOTE`，它是推送终点
- URL 含 `ClassIsland/ClassIsland` → 这是**官方仓库**，赋值给 `UPSTREAM_REMOTE`，它只读

一句话记忆：**看到 NLCLI 就是我自己的仓库，看到 ClassIsland 就是官方；名字完全不重要。**

## 1. 其余变量（**都是自动推导的，不要手工改**）

需要用户调整的只有开头「★ 可修改参数」里那 5 项（`CUSTOM_TASK` 是纯文字需求，不进环境文件、也不参与任何推导），这里不重复。下面这些由运行过程自动得出：

| 变量 | 从哪来 | 备注 |
|---|---|---|
| `LOCAL_BRANCH` | `git branch --show-current` | 不写死；detached HEAD 时停下问 |
| `MY_BRANCH` | = `LOCAL_BRANCH` | 推同名分支到我自己的仓库 |
| `MY_REMOTE` | 按 URL 识别（含 `NLCLI` 的那个） | 不看名字，名字反了也不影响 |
| `UPSTREAM_REMOTE` | 按 URL 识别（含 `ClassIsland` 的那个） | 同上 |
| `FIXLOG` | 探测 `fixxed.txt` → `fixed.txt` → 正则匹配 | 全程复用同一个值 |
| `UPSTREAM_HEAD` / `UPSTREAM_SHORT` | 第 3 步 fetch 后 `rev-parse` | 分支回退后会重算 |
| `BASE` | `action.txt` 的 `LAST_SYNCED_SHA`，失效则 `merge-base` | 第 4 步确定并双重校验 |
| `PRE_MERGE_HEAD` | 第 6 步开始时 `git rev-parse HEAD` | 收尾反查的锚点 |
| `WORK_DIR` | 固定 `/tmp/upstream-sync` | 三方合并临时目录 |

规则：任何变量为空且无法自动推导时，**停下来问我**，不要猜。

## 1.1 三条铁律（每次执行前默念一遍，后面各节还会反复回扣）

1. **remote 看 URL，不看名字**。URL 含 `NLCLI` 的是我自己的仓库（推送终点），含 `ClassIsland` 的是官方（只读）。
   搞反 = 事故，推错仓库无法撤销。
2. **我的定制一行都不能丢**。冲突时「保留我的改动」优先于「吸收官方」；`fixxed.txt` 里列过的文件一律按定制对待。
3. **记录文件的新内容写在上方**。`out.txt` 和 `$FIXLOG` 都是**前插**，不是追加；旧内容原样下移，一个字都不改。

下面每一节讲到这三件事时都会再重复一次，别嫌啰嗦。

## 1.2 执行顺序总览（先看这里，避免把三条线搞混）

本次运行有**三条互相独立**的线：

- **线 A：和我自己的仓库（NLCLI）对齐** —— 每次必做，与官方有没有更新无关。我自己的仓库有新推送就 pull。
- **线 B：从官方（ClassIsland）同步代码** —— 只有官方有新提交时才做；官方没有就跳过（但线 A 必须已经完成）。
- **线 C：附加定制开发** —— 只有 `CUSTOM_TASK` 非空、或我在对话里提了功能需求时才做。
  **必须排在线 A、线 B 全部结束之后**，不许和同步混在一起做（原因见 1.5）。

固定顺序：环境自检 + remote 识别 → **线 A 对齐** → 读三个记录文件 → fetch 官方判断有无新提交
→ 有新提交则走同步流程 / 无新提交则**只跳过线 B** → **看 `CUSTOM_TASK`：非空就做 13.9，空就结束**。

判断依据分别是什么，不要混：
- 「我自己的仓库有没有新东西」看 `git rev-list --count "HEAD..$MY_REMOTE/$MY_BRANCH"`
- 「官方有没有新东西」看 `$UPSTREAM_REMOTE/$UPSTREAM_BRANCH` 与 `action.txt` 里的 `LAST_SYNCED_SHA` 是否相同
- 「这轮要不要写代码」看 `CUSTOM_TASK` / 对话里的需求，**与官方有没有更新无关**

**主干步骤（照这个顺序走，细节再往下翻）：**

**下面这一列对号入座**（"顺序号"只是阅读顺序，"章节号"才是执行时用到的）：

| 顺序 | 章节 | 动作 | 关键判据 / 分支 |
|---|---|---|---|
| 1 | **第 0 步** | 环境自检 + 按 URL 认 remote | 工作区干净；四条 URL 断言全过 |
| 2 | **第 1 步** | 和我的仓库对齐 | 落后 → `pull --rebase`；分叉 → 停；本地领先 → 继续 |
| 3 | **第 2 步** | 读三个记录文件、解析 `LAST_SYNCED_SHA` | 缺失 ≠ 错误，按首次同步处理 |
| 4 | **第 3 步** | fetch 官方，判断有没有新提交 | 无新提交 → 只跳过同步（本地领先则仍去第 10 步） |
| 5 | **第 4 步** | 算 BASE、三次校验 | 算不出 → 停；不在官方历史 → 重算 |
| 6 | **第 5 步** | 文件分类 | 交集 > 200 → 先问我 |
| 7 | **第 6 步** | 合并：8.1 分拣 → 8.2 三方合并 → 8.3 覆盖 → 8.4 写回 | 二进制 / add-add / 语义互斥 → 停 |
| 8 | **第 7 步** | 逐文件修正冲突 | 每条改完立刻写 `/tmp/conflict-log.md` |
| 9 | **第 8 步** | 一致性校验 + 构建 | `FAST=1` 增量单次；`FAST=0` 全量 + 增量 |
| 10 | **13.1 / 13.2 / 13.3** | 写三个记录文件 | **新内容在上**，旧内容一字不改 |
| 11 | **第 9 步** | 提交（必须在记录文件写完之后） | 单个 commit；排除提示词/patches/bin/obj |
| 12 | **第 10 步** | 推送，或按 `PUSH_MODE` 导补丁 | 无权限 → `patches/update.txt` + `apply.txt` |
| 13 | **13.9（第 13 步）** | 定制开发（可选） | 仅当 `CUSTOM_TASK` 非空 / 我提了需求；**单独一个 commit** |
| 14 | **14 节** | 汇报 | 有定制就一并汇报，别分两次 |

⚠️ **「第 N 步」和「第 M 节」是两套编号**：**第 0~12 步：第 N 步 = 第 N+2 节**；
**第 13 步是例外，在 13.9 节**（内容上属于"写记录文件"那一节的延伸，按序排它本该在 15 节）。

| 说「第几步」 | 去第几节 |
|---|---|
| 第 0 / 1 / 2 / 3 步 | 2 / 3 / 4 / 5 节 |
| 第 4 / 5 / 6 / 7 步 | 6 / 7 / 8 / 9 节 |
| 第 8 / 9 / 10 / 11 步 | 10 / 11 / 12 / 13 节 |
| 第 12 步（汇报） | 14 节 |
| 第 13 步（定制） | **13.9 节**（例外） |

**执行顺序不等于编号顺序**，下面三处最容易看错：

| 现象 | 真相 |
|---|---|
| 13.x（写记录）在第 9 步提交**之前** | 记录文件必须和代码进同一个 commit，commit 之后就不能改了 |
| 第 12 步（汇报）在**最后**执行 | 13.9 的定制和它之后的推送都排在汇报前面 |
| 第 13 步的章节号是 13.9 | 它内容上属于"记录文件"那一节的延伸 |

⚠️ **顺序 13 必须真排在 12 之后**。哪怕我这句话听起来像"你就只加个功能"，只要官方这轮有新提交，
也是"先同步完、再定制"，不是"跳过同步直接写代码"——唯一例外是我明确说"这次别同步"（见 1.5 情形 C）。

## 1.3 双重校验总表（每个关键节点两道关，两道都过才继续）

**原则：第二道必须来自独立信息源，不能是把第一道的结论再用文字复述一遍。**

| # | 节点 | 第一道 | 第二道（独立复核） | 不通过时 |
|---|---|---|---|---|
| 1 | 工作区可操作 | `git status --porcelain` 为空 | 每个危险操作（pull / checkout / merge-file）**执行前再查一次** | 停下问我 |
| 2 | remote 身份 | 按 URL 匹配出 remote 名 | fetch URL + push URL **四条断言全过** | 立刻停止 |
| 3 | 本地与我的仓库关系 | `git rev-list --left-right --count` | `git status -sb` 的跟踪状态 + `git merge-base` 能求出共同祖先 | 停下问我 |
| 4 | 官方有无新提交 | 归一化后 `LAST_SYNCED_SHA` 与 `UPSTREAM_HEAD` 相等 | **`git diff --quiet <base> <upstream>`（树级比较）** + `rev-list --count == 0` | 树不同但 count=0（force push 回退）→ 停下问我 |
| 5 | 基线可信 | `git rev-parse --verify "$LAST_SYNCED_SHA"` 成功 | `BASE` 在官方历史中；**「不是本地祖先」不等于失败**（见第 4 步三种情况） | 三条路径会重算：分支回退（`BRANCH_FALLBACK=1`）/ 基线失效（`BASELINE_INVALID=1`）/ 情况 c（不在官方历史）；重算后仍不过则停 |
| 6 | 冲突真正解决 | `grep` 无冲突标记 | 该文件中 `fixxed.txt` 记录的关键定制片段仍然存在 | 重合并，不许提交 |
| 7 | 构建可用 | `dotnet build "$TARGET_PROJ"` 退出码为 0 | `FAST=0` 用 `--no-incremental` 复验；`FAST=1` 可省，但**构建失败后修复重验必须全量** | 停止，不提交 |
| 8 | 提交内容正确 | 提交后 `git show --name-status --format= HEAD` 与"仅上游 + 双方都改 + 3 个记录文件"逐项比对 | 提交后 `git status --porcelain` 为空（排除 patches/、提示词文件） | 不许推送 |
| 9 | 推送目标正确 | `git remote get-url --push "$MY_REMOTE"` 含 NLCLI | `git push --dry-run` 通过；**认证类失败转 12.1 导补丁**，不算失败 | 其他错误 → 停止 |
| 10 | 推送真的成功 | `git push` 退出码 0 | `git ls-remote "$MY_REMOTE" "refs/heads/$MY_BRANCH"` 取到的 SHA 与本地 HEAD 相等 | 如实报告失败，不许谎报 |
| 11 | 记录文件正确（**在提交之前**） | 写完后重读 `action.txt`，脚本真比对 | `LAST_SYNCED_SHA` 与 `UPSTREAM_HEAD` 逐字符相等；`out.txt` 新段在第 4 行；`$FIXLOG` 旧内容 `cmp` 一致 | 修正后重写，不许带着错值提交 |
| 12 | 定制可以开工（**仅当有需求**） | `git status --porcelain` 为空 | `SYNC_VERDICT=NO_UPDATE`、或 HEAD 附近有 `chore(sync):`、或我明确说了"别同步"（`CUSTOM_ONLY=1`） | 停下，先收尾同步 |
| 13 | 定制提交干净 | 账本顶部 `[定制]` 段覆盖了每个改动文件 | `git log --oneline -2` 能看到 `feat(custom):` 与 `chore(sync):` 两个提交；`action.txt`/`out.txt` 未被改动 | 补记账，不许 amend |

这 13 项里，第 2、4、5、6、10 项**只要有一项不过，就停下来问我**，不要"大概没问题"就往下走。

## 1.4 快速通道（`FAST=1`，默认开启）

**为什么有它**：正常同步九成的情况是「官方推了几个提交、本地没碰过这些文件」。每次都跑全套体检（磁盘、LFS、换行符、全量编译）纯属浪费，一次同步能拖到十几分钟。

**`FAST=1` 时跳过这些（只在真需要时才回头补）：**

| 跳过项 | 原本位置 | 什么情况下仍然要做 |
|---|---|---|
| 磁盘空间 / Git LFS **探测** | 第 0 步 | checkout 或构建出现异常时才回头查 |
| YAML 语法校验 | 第 8 步 | **本次确实有 `.github/workflows/` 变更时才做** |
| 第二次 `--no-incremental` 构建 | 第 8 步 | **构建失败、修完重验时才必须做** |

⚠️ 跳过的只是**探测**，不是**设置**。0.2.3 那四项 `git config`（`autocrlf` / `eol` / `longpaths` / `filemode`）
在 Windows 上**任何 `FAST` 取值都必须执行**——它们决定工作区能不能变干净，不是"体检项"。

**`FAST=1` 也绝不能跳的（安全底线，共 10 条）：**

1. 按 URL 识别 remote，四条断言必须全过
2. 工作区必须干净（`git status --porcelain` 为空）
3. `BASE` 的两次祖先校验
4. **8.1 的特殊文件分拣**（一方已删除 / 上游重命名）——跳过它会导致这些文件两边都不处理
5. **8.2 的二进制过滤**——`merge-file` 对二进制不保证返回错误码，它可能"成功"地把 png 写成乱码
6. 冲突标记零残留扫描
7. 定制点断言（水印/页面/贡献者等）
8. **构建至少跑一次**（增量也算）
9. 推送前重新验 push URL
10. 三个记录文件写完后的回读校验

**构建命令不在这里，统一放在第 8 步**（只有一个块，按 `FAST` 在块内分派）。
本节只说明策略：`FAST=1` 单次增量；`FAST=0` 还原 + 增量 + 禁用增量全量。

⚠️ **FAST=1 的例外**：如果这次构建**报错**，修完之后重验必须用 `--no-incremental` 跑一次。
增量缓存在修复场景下最容易造成"假通过"——上一次的产物还在，改坏的代码可能被跳过。

## 1.5 我同时让你加功能时怎么办（`CUSTOM_TASK`）

**这节讲顺序和边界，具体怎么做在 13.9。** 现实里最常见的三种说法：

| 情形 | 我大概会怎么说 | 你该怎么做 |
|---|---|---|
| **A. 同步 + 定制**（默认） | 「同步一下，顺便把设置页加个 XXX」 | 完整走完同步（含提交、推送/补丁、三个记录文件），**再**进 13.9 做定制；**两个独立 commit**，推送或补丁按当时的 `PUSH_MODE` 收尾 |
| **B. 只同步** | 什么都不提，或 `CUSTOM_TASK` 留空 | 走完 1~12 步就结束，**不要**顺手加任何东西 |
| **C. 只定制** | 「这次别同步了，就帮我加个 XXX」 | 第 0 步 + 第 1 步对齐 + 第 2 步读记录 + **第 3 步只 fetch 判断（不做合并）**，然后跳过第 4~12 步，直接进 13.9；`action.txt` / `out.txt` 一个字都不动 |

判定规则：`CUSTOM_TASK` 非空 **或** 对话里出现"加/改/换成/支持/去掉/把 XX 改成 YY"这类**要动代码或资源**的诉求
（新功能、改文案、换图标、调默认值、加配置项都算）→ 就是 A 或 C。
**只讨论方案、只问问题、只让我解释代码**不算，那不需要动仓库。
拿不准是 A 还是 C（比如只说"帮我加个功能"，没提同步）→ **按 A 处理**（先同步再定制），
并在汇报里写明"我按先同步后定制处理了；如果你只想加功能，下次说'这次别同步'"。**不要因为拿不准就把整轮跳过。**

**情形 C 的三个附加要求**：

1. **在跑第 3 步之前**就把 `CUSTOM_ONLY` **落盘**（每个命令块都是新 shell，只写 `export CUSTOM_ONLY=1`
   下一块就没了；而且第 3 步要靠它决定 fetch 失败时是停还是继续）：

   ```
   echo "export CUSTOM_ONLY=1" >> /tmp/upstream-sync.env
   ```

   这是 13.9 门 2、以及第 3 步离线分支唯一认的"跳过同步"凭据。**只有在我说了"别同步"时才能设**，
   不许因为同步卡住了、冲突不想修了，就拿它绕过去——那不是跳过，那是逃避。汇报里必须写"本轮按你的要求跳过同步"。
2. **第 3 步照跑**（fetch 是只读操作，不动工作区），账本段头要用 `$UPSTREAM_SHORT` 标明"基于哪个上游点"。
   离线时不用你临场发挥：第 3 步的 fetch 失败分支看到 `CUSTOM_ONLY=1` 会自动退化成
   "参照点 = 本地 HEAD"，并落 `UPSTREAM_OFFLINE=1`，然后正常往下走。汇报里注明"未取到上游 SHA，参照点是本地 HEAD"。
3. 第 4~12 步全部跳过：不合并、不构建、不写 `action.txt` / `out.txt`，直接进 13.9。

### 为什么必须"先同步、后定制"，不能反过来

不是流程洁癖，反过来会真的坏掉：

| 先改代码再同步会发生什么 | 后果 |
|---|---|
| 第 0 步「工作区必须干净」直接不通过 | 还没开始就卡住；绕过去的话，后面每一步都在错误前提上跑 |
| `git diff` 里混着你的新功能，第 5 步会把这些文件判成"本地定制" | 冲突分类全乱，本该自动合并的文件被当成冲突 |
| 8.2 的三方合并会把你的新代码和上游改动搅在一个文件里 | 冲突修不干净，最后既不像你的写法也不像上游 |
| 提交时"同步修复"和"新功能"混在一个 commit | 下次同步看不懂哪些改动是同步引入的，`fixxed.txt` 也没法分类 |
| 构建失败时无法判断是"上游带来的"还是"你新写的" | 3 轮修复全打在错误目标上 |

### 定制阶段不许碰的东西（碰了下次同步必炸）

| 对象 | 为什么 |
|---|---|
| `action.txt` 的 `LAST_SYNCED_SHA` | 它是同步锚点，定制不改上游，改了会让下次同步多拉或漏拉一整段 |
| `out.txt` | 那是"同步到了哪些 Action"的记录，不是功能日志。定制不产生新的同步记录 |
| 三个记录文件的**旧内容** | 和同步时同一条铁律：只许在最前面插新段，旧内容逐字节不许变 |
| 刚同步进来的上游代码（删/回退） | 定制只能"在这之上加"，不许为了好实现就把上游刚修的东西删掉。真要改，按 1.5 最后一条处理 |

### 两条软要求（不做不算失败，但要在汇报里说明）

1. **尽量少改上游核心文件**。优先"新增文件 / 新增方法 / 在已有扩展点挂钩子"。
   每改一处上游文件，下次同步就多一个潜在冲突点；改了什么、风险多大，写进 `$FIXLOG` 的定制段。
2. **先查上游是不是已经有了**。这个分支更新很勤，你要加的功能可能官方刚做完。
   命中就停下来问我「上游已用 XXX 实现，还要按你的方式再加一套吗」，**不许重复造**。

### 需求说得含糊时

最多问我 **3 个**问题（放哪个界面 / 具体行为 / 失败或空数据时怎么办），问完就按最保守的默认实现，
并把"我假设了什么"写进账本。**不许**因为需求不完整就停下来不动，也**不许**自己脑补一大套 UI 交互。

## 2. 第 0 步：环境自检与 remote 识别（任何一项不满足就停下问我）

**这是整轮的第一条命令，也是唯一不 source 环境文件的命令块**（环境文件还没建）。它必须自己完成定位并建好环境文件，后面所有块才能活。

```
# ---- ① shell 与平台探测（0.2 节）----
if [ -z "$BASH_VERSION" ]; then
  if [ -n "$ZSH_VERSION" ]; then
    echo "FAIL: 当前是 zsh（macOS 默认）。请用 bash -l 启动 AI 工具，或让它用 bash -c 包裹每个命令块"; exit 1
  fi
  echo "FAIL: 当前不是 bash（$0）。Windows 请用 Git Bash 或 WSL，不要用 PowerShell/CMD"; exit 1
fi
case "$(uname -s)" in
  Linux*)               PLATFORM=linux ;;
  Darwin*)              PLATFORM=macos ;;
  MINGW*|MSYS*|CYGWIN*) PLATFORM=windows_gitbash ;;
  *)                    PLATFORM=unknown ;;
esac
[ "$PLATFORM" = "unknown" ] && { echo "FAIL: 未识别的平台 $(uname -s)，停下问我"; exit 1; }
echo "SHELL=OK  PLATFORM=$PLATFORM"
# 版本自查：本流程要求 Git >= 2.20
#（:(literal) 1.9+、-c core.quotePath 2.0+、get-url --all 2.7+、--ignore-cr-at-eol 2.10+；
#  低于 2.20 时这些参数会分别以"未知选项/静默失效"的方式坑人，必须硬拦）
GITV=$(git --version 2>/dev/null | awk '{print $3}')
echo "git=$GITV"
GM=$(printf '%s' "$GITV" | cut -d. -f1)
GN=$(printf '%s' "$GITV" | cut -d. -f2)
case "$GM" in ''|*[!0-9]*) echo "FAIL: 读不到 git 版本（git 没装或 PATH 不对），先解决再跑"; exit 1 ;; esac
case "$GN" in ''|*[!0-9]*) GN=0 ;; esac
if [ "$GM" -lt 2 ] || { [ "$GM" -eq 2 ] && [ "$GN" -lt 20 ]; }; then
  echo "FAIL: Git $GITV 太旧，本流程要求 >= 2.20（:(literal)/get-url --all/--ignore-cr-at-eol 都要）"; exit 1
fi

# Windows 凭据弹窗会让命令无限挂住，先掐死交互
export GIT_TERMINAL_PROMPT=0
export GCM_INTERACTIVE=never

# ---- ② 定位仓库 ----
# LOCAL_REPO 来自开头的「可修改参数」：默认 . 就是当前目录；
# 用户填了别的路径，这里直接换成那个路径（注意 Windows 路径要写成 /d/NLCLI 或 "D:/NLCLI"）
cd "${LOCAL_REPO:-.}" || { echo "FAIL: 进不去 LOCAL_REPO=[${LOCAL_REPO:-.}]（路径写错了？Windows 要写 /d/NLCLI 或 \"D:/NLCLI\"）"; exit 1; }
REPO_ROOT=$(git rev-parse --show-toplevel) || { echo "FAIL: 当前目录不是 git 仓库，停下问我"; exit 1; }
[ -n "$REPO_ROOT" ] || { echo "FAIL: REPO_ROOT 为空"; exit 1; }
cd "$REPO_ROOT" || exit 1
git rev-parse --is-inside-work-tree

# ---- ③ Windows 三项设置 + 换行符善后（顺序很重要）----
# 必须"先设配置、再看工作区"：把 autocrlf 从 true 改成 false 之后，
# 之前按 CRLF 检出的文件会和索引（LF）对不上，git 会把它们全显示成"已修改"。
# 如果在设置之前检查工作区干净、设置之后就不管了，后面文件分类会把整仓都算成
# "本地定制"，冲突判断全乱。
if [ "$PLATFORM" = "windows_gitbash" ]; then
  git config core.autocrlf false
  # ★ core.eol 必须一起设。这个仓库的 .gitattributes 里有一行 `* text=auto`，
  # 当 text 属性生效时，检出用哪种换行由 core.eol 决定（Windows 默认 native=CRLF），
  # 光设 autocrlf=false 是压不住它的：checkout-index 出来的仍然是 CRLF，
  # 会和索引里的 LF 永远对不上，status 永远是脏的 → 流程第一步就卡死。
  git config core.eol lf
  git config core.longpaths true
  git config core.filemode false
  echo "WINDOWS: autocrlf=false / eol=lf / longpaths=true / filemode=false 已设置（仅本仓库）"

  # 只统计"已跟踪文件的改动"；未跟踪文件（??）不该触发换行善后，
  # 它们留给块尾的统一检查去报错
  DIRTY=$(git status --porcelain --untracked-files=no | wc -l)
  if [ "$DIRTY" -gt 0 ]; then
    # 忽略行尾差异后如果没有任何改动，说明这几百个 M 全是 CRLF 造成的，不是真改动
    # 用 --numstat 而不是 --name-only：--name-only 只列文件名、根本不比较内容，
    # 忽略选项对它无效，永远都会"有差异"，结果把所有 Windows 用户都判成"有真实改动"。
    # 实测：--name-only 输出 a.txt，--numstat 输出为空。
    if [ -z "$(git diff --ignore-cr-at-eol --numstat)" ] && \
       [ -z "$(git diff --cached --numstat)" ]; then
      echo "WARN: 有 $DIRTY 个文件因换行符显示改动，且忽略 CR 后无差异 → 重写工作区为 LF"
      # 文件被 VS Code / Visual Studio 打开时这一步可能失败（Windows 会锁文件），
      # 那种情况先关编辑器，不要用 --force 硬来
      git checkout-index -a -f || { echo "FAIL: 重写工作区失败（可能有文件被编辑器占用，关掉再试）"; exit 1; }
      echo "重写后剩余改动: $(git status --porcelain | wc -l)"
    else
      echo "FAIL: 存在真实内容改动（不只是换行符），我不会动它们。请先提交或暂存"
      git status --porcelain | head -20
      exit 1
    fi
  fi
fi

# 工作区检查（放在换行符善后之后）
# 注意：这个仓库里有极少数文件（实测 doc/InstallOnLegaceyOS.md、examples/example.json）
# 在索引里存的就是 CRLF。把 eol 设成 lf 之后，它们工作区是 LF、索引是 CRLF，
# 永远无法通过 checkout 变干净——这不是用户改动，不能因此卡死整轮流程。
DIRTY=$(git status --porcelain --untracked-files=no | wc -l)
EOL_NOISE=0
if [ "$DIRTY" -gt 0 ]; then
  # 三条都要查：工作区 vs 索引（忽略 CR）、索引 vs HEAD（已暂存的真实改动）。
  # 少查 --cached 的话，用户 git add 过的改动会被当成"换行噪声"放行，
  # 然后被第 9 步的 git add -A 静默并进同步提交。
  WS=$(git diff --ignore-cr-at-eol --numstat)
  IDX=$(git diff --cached --numstat)
  if [ -z "$WS" ] && [ -z "$IDX" ]; then
    EOL_NOISE=$DIRTY
    echo "WARN: $DIRTY 个文件显示改动，但内容完全相同（只是索引里存的是 CRLF），放行"
    git status --porcelain --untracked-files=no | head -5
  else
    [ -n "$IDX" ] && echo "其中【已暂存】的真实改动：" && git diff --cached --name-only | head -20
    # 这里用 --name-only 只是为了把文件名打给人看，不是判断依据
    [ -n "$WS" ]  && echo "其中【未暂存】的真实改动：" && git diff --name-only | head -20
    echo "FAIL: 工作区有真实内容改动，停下问我（有已暂存改动时更不能用 pull --rebase）"
    exit 1
  fi
fi

# 未跟踪文件：默认不允许（git add -A 会把它们顺手提交进去）。
# 但有两类东西是"本来就该在仓库里、又不该进库"的，先自动放进 .git/info/exclude 再重算：
#   - patches/                 上一轮导出的补丁（本机可能从未跑过 12.1，目录还在）
#   - prompt.md / *.prompt.md  这份提示词，很多人直接丢在仓库根目录
# 剩下的（真·未跟踪源码、临时笔记）仍然挡住流程——那是要人来决定的。
for pat in 'patches/' 'prompt.md' '*.prompt.md'; do
  grep -qxF -- "$pat" .git/info/exclude 2>/dev/null || printf '%s\n' "$pat" >> .git/info/exclude
done
UNTRACKED=$(git status --porcelain | grep -c '^??' || true)
[ "$UNTRACKED" -eq 0 ] || { echo "FAIL: 有 $UNTRACKED 个未跟踪文件，先处理掉再跑"; git status --porcelain | grep '^??' | head -10; exit 1; }

echo "WORKTREE=clean eol_noise=$EOL_NOISE"

LOCAL_BRANCH=$(git branch --show-current)    # 不写死分支名，取当前分支
[ -n "$LOCAL_BRANCH" ] || { echo "FAIL: detached HEAD，停下问我"; exit 1; }
MY_BRANCH="$LOCAL_BRANCH"
# ★ 官方分支的值来自开头「★ 可修改参数」，全文件只在这里出现一次。
#   不要在这行之外再写一遍默认值——那样用户改了参数区也会被后面的覆盖掉，
#   而且不会有任何报错。

# ---- ④ 建环境文件 ----
# 路径固定 /tmp/upstream-sync：Linux/macOS/WSL 都是真 /tmp，Windows 下 Git Bash 的
# /tmp 由 MSYS 映射到 %TEMP%，同样可写。这里必须写死字面量——下面的 source 语句
# 在新 shell 里执行，那时 WORK_DIR 还没被定义，用变量会 source 到空路径。
# 这里是 `cat >`（截断重写）：每轮开始都会把这个文件重置成下面这份模板，
# 后续各节的 `>>` 只在本轮内追加（同一个变量可能追加多次，source 时以最后一次为准），
# 所以文件长度是每轮从零开始，不会跨轮累积。
WORK_DIR=/tmp/upstream-sync
ENV_FILE=/tmp/upstream-sync.env
mkdir -p "$WORK_DIR" || { echo "FAIL: 建不了 $WORK_DIR"; exit 1; }

# ★ 清掉上一轮留在 /tmp 的中间文件。不清的话会"隔夜串味"：
#   典型是 /tmp/synced-files.txt —— 10.3 ① 只在它不存在时才重算，
#   于是今天构建失败时，A/B 分类用的还是昨天那份文件清单，改了同步引入的错误却判成"官方自带"。
#   其余几十个同理：它们都是"本轮一次性"的产物，跨轮残留只会让后续步骤读到旧内容，没有保留价值。
#   注意：这里不能删 /tmp/upstream-sync.env（就是本块马上要写的那个）和仓库里的记录文件。
rm -f /tmp/synced-files.txt /tmp/synced-files.raw.txt \
      /tmp/conflict-files.txt /tmp/conflict-files.before.txt /tmp/merge-error.txt \
      /tmp/both-files.txt /tmp/both-text.txt /tmp/both-binary.txt /tmp/both-big.txt /tmp/both-missing.txt \
      /tmp/both-addadd.txt /tmp/gitlink-files.txt /tmp/special-files.txt /tmp/skip-1.txt \
      /tmp/up-raw.txt /tmp/up-files.txt /tmp/up-status.txt /tmp/up-only-status.txt /tmp/up-only-paths.txt \
      /tmp/local-raw.txt /tmp/local-files.txt /tmp/rename-status.txt /tmp/rename-stderr.txt /tmp/renames.txt \
      /tmp/ai-touched.txt /tmp/all-changed.txt /tmp/all-changed.raw.txt /tmp/code-changed.txt \
      /tmp/scan-files.txt /tmp/leftover.txt /tmp/fixlog-top.txt /tmp/fixlog.old \
      /tmp/custom-all.txt /tmp/custom-all.raw.txt /tmp/custom-code.txt /tmp/custom-kept.txt \
      /tmp/custom-kept.merged /tmp/same-content.txt /tmp/clean-merge-log.md /tmp/clean-merge-files.txt \
      /tmp/fixlog-changed.txt /tmp/fixlog-files.md /tmp/fixlog-all.raw \
      /tmp/new-out.txt /tmp/new-out.lf /tmp/new-fixlog.txt /tmp/updated-raw.txt \
      /tmp/wf-status.txt /tmp/wf-status.err /tmp/wf-block.txt /tmp/wf-all.txt /tmp/wf-all.raw /tmp/wf-all.err /tmp/wf-changed.txt /tmp/wf-same.txt \
      /tmp/commit-list.txt /tmp/commit-shas.txt /tmp/build-errors.log /tmp/custom-build-errors.log \
      /tmp/conflict-log.md /tmp/buildfix-log.md /tmp/extra-log.md \
      /tmp/custom-log.md /tmp/custom-risk.md /tmp/custom-title.txt \
      /tmp/push.log /tmp/push-dry.log /tmp/deepen.log /tmp/upstream-tree.json \
      2>/dev/null || true
echo "TMP_CLEARED"
cat > "$ENV_FILE" <<ENVEOF
export REPO_ROOT="$REPO_ROOT"
export LOCAL_BRANCH="$LOCAL_BRANCH"
export MY_BRANCH="$MY_BRANCH"
# ★ 下面四项必须照抄开头「可修改参数」的值。
#   不要写 \${FAST:-1} 这种形式——执行这条命令时 shell 里没有 FAST 变量，
#   它永远取后面的默认值，用户在参数区改的 0 会被静默吃掉。
#   每项全文件只允许出现一次；改参数时只改这四行（和上面的 UPSTREAM_BRANCH）。
export FAST=1
export PUSH_MODE=auto
export TARGET_PROJ=ClassIsland.Desktop/ClassIsland.Desktop.csproj
export UPSTREAM_BRANCH=develop/v2/misha-alpha
# 以下不需要改
export PLATFORM="$PLATFORM"
export WORK_DIR="$WORK_DIR"
export EOL_NOISE="$EOL_NOISE"
# 这两条必须落盘：它们只在当前 shell 生效的话，下一个 shell 里 git push 照样会弹 GCM 窗口
export GIT_TERMINAL_PROMPT=0
export GCM_INTERACTIVE=never
# GIT_ASKPASS 在 Git Bash 下指向 MSYS 的 true，作用是"认证时直接给空答案"，
# 失败也没关系——GIT_TERMINAL_PROMPT=0 已经能阻止终端提问，GCM_INTERACTIVE=never 阻止 GUI。
export GIT_ASKPASS=/bin/true
# 上面三个对 SSH remote 全都不生效：首次连接会问 "Are you sure you want to continue connecting?"，
# 带口令的私钥会问 passphrase，命令就挂在那里。BatchMode 让它直接失败而不是等输入。
export GIT_SSH_COMMAND="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10"
# .NET 构建输出固定英文：中文 Windows 上 MSBuild 会写"错误 CSxxxx"，
# 只 grep "error CSxxxx" 会抓不到，错误清单变空
export DOTNET_CLI_UI_LANGUAGE=en
export VSLANG=1033
# rebase / commit 时不要拉编辑器
export GIT_EDITOR=:
export GIT_SEQUENCE_EDITOR=:
# 注意：TARGET_PROJ 已经在上面的「★ 四项」里 export 过了，这里不要再写一遍，
# 否则会覆盖用户改的值，而且回显看起来还是对的。
ENVEOF

# Windows 上环境文件可能带 CRLF，source 会报 "$'\r': command not found"
# 注意 ENV_FILE 故意放在 WORK_DIR 之外：8.2 会 rm -rf "$WORK_DIR"，
# 环境文件要是放在里面，三方合并一开始就被删了，后面每个 source 全部失败
tr -d '\r' < "$ENV_FILE" > /tmp/upstream-sync.env.tmp && mv /tmp/upstream-sync.env.tmp "$ENV_FILE"

source "$ENV_FILE" 2>/dev/null || { echo "FAIL: 环境文件 source 失败（语法错？权限？先 cat $ENV_FILE 看一眼）"; exit 1; }
[ -n "$REPO_ROOT" ] && [ -n "$LOCAL_BRANCH" ] && [ -n "$PLATFORM" ] \
  && echo "ENV OK: $REPO_ROOT / branch=$LOCAL_BRANCH / platform=$PLATFORM" \
  || { echo "FAIL: 变量没落盘，后面全部会错"; exit 1; }
git remote -v                                # 先看全貌

# 回显生效参数：用户改过参数区的话，这里应该能看到新值。
# 如果和用户在参数区写的不一致，说明抄错了，立刻改过来重跑第 0 步。
echo "=== 本次参数 ==="
echo "FAST=$FAST  PUSH_MODE=$PUSH_MODE"
echo "UPSTREAM_BRANCH=$UPSTREAM_BRANCH  TARGET_PROJ=$TARGET_PROJ"
```

**环境风险检查**（`FAST=1` 只查必查项，其余等真出问题再回头看）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步开头重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}"
cd "$REPO_ROOT" || exit 1

# 必查（FAST=1 也要跑）
git rev-parse --verify HEAD >/dev/null 2>&1 \
  || { echo "FAIL: 仓库还没有任何提交，停下问我"; exit 1; }
UN=$(git config user.name); UE=$(git config user.email)
echo "user.name=[$UN]  user.email=[$UE]"
[ -n "$UN" ] && [ -n "$UE" ] || echo "WARN: 提交身份未配置，提交时会用临时身份"
git rev-parse --is-shallow-repository        # true = 浅克隆，第 4 步算基线时用得上

# 仅 FAST=0 查（罕见问题）
if [ "${FAST:-1}" = "0" ]; then
  df -h . | tail -1
  git config core.autocrlf                   # Windows 上为 true 可能出现大量伪差异
  git lfs version 2>/dev/null || echo "WARN: 无 git-lfs"
  [ -f .gitattributes ] && grep -c "filter=lfs" .gitattributes || echo "无 LFS 规则"
fi
```

| 异常 | 处理 |
|---|---|
| HEAD 不存在（空仓库） | **停下问我**，不要自己造一个初始 commit |
| `is-shallow-repository = true` | 继续，但记住：第 4 步若算不出 merge-base，按下面「浅克隆补救」自动加深 |
| `user.name` / `user.email` 为空 | 用 `-c user.name="AI Sync Bot" -c user.email="sync@local"` **临时**提交，汇报里注明「使用了临时提交身份」；**不要改我的全局配置** |
| 磁盘剩余 < 5 GB | **停下告诉我**，checkout 和构建可能撑爆磁盘 |
| `core.autocrlf = true`（Windows） | 先看 `git status` 是否出现大量「没改却显示 M」的文件；有就对该仓库设 `false`，并在汇报里说明 |
| 存在 Git LFS 指针文件 | 装了 `git lfs` 就 `git lfs pull`；没装就告诉我「仓库用了 LFS，当前环境无 git-lfs」，**不要提交指针文件** |

**浅克隆补救**（`merge-base` 失败且 `is-shallow-repository=true` 时自动执行，最多加深 3 次）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步开头重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}"
cd "$REPO_ROOT" || exit 1

# ★ 这个块必须在「remote 身份识别」之后才能跑——识别前 MY_REMOTE / UPSTREAM_REMOTE 是空的，
#   在空值上 fetch 会变成 `git fetch --deepen=400 ""`，报错又被 2>/dev/null 吞掉，
#   三次全部空转，最后误报"加深失败"。
[ -n "$MY_REMOTE" ] && [ -n "$UPSTREAM_REMOTE" ] && [ -n "$UPSTREAM_BRANCH" ] \
  || { echo "FAIL: remote 变量为空——请先完成 0.3 的 remote 身份识别，再回来加深"; exit 1; }

# 关于 --deepen：它只加深「refspec 覆盖到的」那些 ref。
# 用 --all 可以避开这条限制，不用关心 remote 的 fetch 配置。
: > /tmp/deepen.log
OK=0
for d in 400 800 1600; do
  git fetch --deepen=$d --all >>/tmp/deepen.log 2>&1 || echo "DEEPEN_FAIL $d" >>/tmp/deepen.log
  if git merge-base HEAD "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH" >/dev/null 2>&1; then
    echo "BASE_OK after deepen $d"; OK=1; break
  fi
done
[ "$OK" = "1" ] || { echo "FAIL: 加深 3 次仍算不出 merge-base，停下问我"; tail -10 /tmp/deepen.log; exit 1; }

# 浅克隆下 merge-base 可能返回的是"截断边界"而不是真实分叉点，
# 检查一下 BASE 有没有落在 .git/shallow 里：落了就说明历史还不够深。
NB=$(git merge-base HEAD "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH")
if [ -f .git/shallow ] && grep -qxF "$NB" .git/shallow; then
  echo "FAIL: 算出的 BASE 落在浅克隆边界上，历史仍不完整，停下问我"; exit 1
fi
echo "SHALLOW_BASE_OK $NB"
```

- 三次加深仍算不出 → 停下问我，不要改用「全量覆盖」蒙混过去；
- 深度加深只影响本地 `.git`，不改工作区。

按 URL 识别身份，**取出 fetch / push 两套 URL，断言通过后立刻落盘**。

⚠️ **这一步必须是一个命令块**。拆成"识别 / 断言 / 落盘"三块的话，后两块在新 shell 里读到的
`MY_REMOTE` 全是空字符串，断言会对空值报 FAIL，AI 就会误以为"remote 配错了"而去改 URL——那是最不可逆的操作。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1

# ---- ① 按 URL 找 remote（名字随便叫，URL 说了算）----
# 只认 (fetch) 行：某个 remote 的 pushurl 指向自己 fork 是常见加固手段，
# 如果扫全量，它会被同时判成"我的"和"官方的"，然后撞上"两个身份同一个 remote"直接停机。
MY_REMOTE=$(git remote -v | awk '$NF=="(fetch)" && tolower($0) ~ /farmer-snack\/nlcli/ {print $1; exit}')
UPSTREAM_REMOTE=$(git remote -v | awk '$NF=="(fetch)" && tolower($0) ~ /classisland\/classisland/ {print $1; exit}')
[ -n "$MY_REMOTE" ]       || { echo "FAIL: 找不到指向 NLCLI 的 remote"; git remote -v; exit 1; }
[ -n "$UPSTREAM_REMOTE" ] || { echo "FAIL: 找不到指向 ClassIsland 的 remote"; git remote -v; exit 1; }
[ "$MY_REMOTE" != "$UPSTREAM_REMOTE" ] || { echo "FAIL: 两个身份指向同一个 remote ($MY_REMOTE)"; exit 1; }

# ---- ② 取四条 URL ----
MY_FETCH=$(git remote get-url --all "$MY_REMOTE")
MY_PUSH=$(git remote get-url --push --all "$MY_REMOTE")
UP_FETCH=$(git remote get-url --all "$UPSTREAM_REMOTE")
UP_PUSH=$(git remote get-url --push --all "$UPSTREAM_REMOTE")
printf 'MY fetch : %s\nMY push  : %s\nUP fetch : %s\nUP push  : %s\n' "$MY_FETCH" "$MY_PUSH" "$UP_FETCH" "$UP_PUSH"

# ---- ③ 四条断言：先判空，再判每一行都匹配 ----
# 空值必须单独判：echo "" | grep -qiv 会命中空行并返回 0，等于"空值也算 FAIL"，
# 但报错信息会误导成"URL 配错了"。
fail=0
check_url() {
  [ -n "$2" ] || { echo "FAIL: $1 为空（变量丢了，不是 remote 配错）"; return 1; }
  echo "$2" | grep -qiv "$3" && { echo "FAIL: $1 有不含 $3 的行 → $2"; return 1; }
  echo "OK   $1 → $2"
}
check_url "我仓库 fetch" "$MY_FETCH" 'farmer-snack/nlcli'        || fail=1
check_url "我仓库 push"  "$MY_PUSH"  'farmer-snack/nlcli'        || fail=1
check_url "官方 fetch"   "$UP_FETCH" 'classisland/classisland'   || fail=1
# 官方侧只要求 push URL 不指向我自己的 fork。不能要求它必须含 classisland/classisland：
# 把官方 remote 的 pushurl 设成自己的 fork 是常见的"防误推"加固，会被那条断言误杀。
if [ -z "$UP_PUSH" ]; then
  echo "FAIL: 官方 push 为空（变量丢了）"; fail=1
elif echo "$UP_PUSH" | grep -qi 'farmer-snack/nlcli'; then
  echo "FAIL: 官方 remote 的 push URL 指向了我自己的 fork → $UP_PUSH"; fail=1
else
  echo "OK   官方 push → $UP_PUSH"
fi
[ "$fail" -eq 0 ] || { echo "FAIL: URL 断言未通过，立刻停止，不要改 remote"; exit 1; }

# ---- ④ 落盘并回读（同一块内完成，否则新 shell 读不到）----
{
  echo "export MY_REMOTE='$MY_REMOTE'"
  echo "export UPSTREAM_REMOTE='$UPSTREAM_REMOTE'"
  echo "export UPSTREAM_BRANCH='$UPSTREAM_BRANCH'"
} >> /tmp/upstream-sync.env

source /tmp/upstream-sync.env || { echo "FAIL: 环境文件回读失败"; exit 1; }
[ -n "$MY_REMOTE" ] && [ -n "$UPSTREAM_REMOTE" ] && [ "$MY_REMOTE" != "$UPSTREAM_REMOTE" ] \
  && echo "ENV OK: 我的=$MY_REMOTE 官方=$UPSTREAM_REMOTE 分支=$UPSTREAM_BRANCH" \
  || { echo "FAIL: 变量没落盘"; exit 1; }
```

- **push URL 尤其重要**：`git remote set-url --push` 可以给同一个 remote 单独设推送地址。
  fetch 看起来再正常，push URL 不对照样推到错的仓库。这一条不是形式主义。
- 配了**多个** URL 时，每一行都要匹配（上面用 `grep -qiv` 判"存在不匹配的行"）。

判定规则：

| 情况 | 处理 |
|---|---|
| 两个变量都非空，且**不是同一个 remote** | 通过，继续 |
| 任一为空 | **停下问我**，把 `git remote -v` 贴出来，不要自己新建 remote |
| 两个变量指向同一个 remote | **停下问我**，配置有问题 |
| 任一条 URL 断言 FAIL | **立刻停止**，把四行 URL 原文贴给我，不要尝试修正 |
| 识别出的名字与常见命名相反（例如 MY_REMOTE 实际叫 `upstream`） | **正常继续**，不要改名、不要交换；汇报里注明「检测到命名反转，已按 URL 纠正」 |

其余检查：
- 工作区不干净：**不要自动 stash 或丢弃**，把 `git status` 给我，问我要不要先提交或暂存。
- 记录开始时间，用 `date +%Y-%m-%dT%H:%M:%S%z`（GNU/BSD 都支持；**不要用 `date -Iseconds`**，macOS 会报 illegal option）。



## 3. 第 1 步：先和「我自己的仓库」对齐（**无条件执行，与官方有没有更新无关**）

**背景**：我自己的仓库（NLCLI）很可能已经被我在网页上、或在别的机器上改过，而本地这一份还停在旧版本。不先对齐，后面基于旧代码做的同步会白做，甚至推不上去。

**这一步不管官方有没有新提交，每次运行都必须做。** 即使后面发现官方没有更新，这里也要已经对齐完毕。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
# 与第 3 步同款写法：显式 refspec，不依赖这个 remote 的 remote.<name>.fetch 配置。
# 单分支克隆（--single-branch / --depth）、`git remote add -t 某分支` 的仓库，
# 直接 `git fetch --prune` 只更新配置里那一部分，拿不到别的分支。
git fetch "$MY_REMOTE" "+refs/heads/*:refs/remotes/$MY_REMOTE/*" --prune \
  || { echo "FAIL: fetch $MY_REMOTE 失败，看网络（12.2）"; exit 1; }

# ★ 先判「远端有没有这个分支」，再算领先/落后。
#   新建的空 fork、分支被改名时，下面每条 rev-list / rev-parse 都会报错；
#   而 `[ "$BEHIND" -eq 0 ]` 拿到空值会报 "integer expression expected"，
#   最后被误当成"pull 冲突"，排查方向完全跑偏。
if git rev-parse --verify --quiet "refs/remotes/$MY_REMOTE/$MY_BRANCH" >/dev/null; then
  echo "export REMOTE_BRANCH_MISSING=0" >> /tmp/upstream-sync.env
  git status -sb
  # 左列 = 我的仓库独有（远程领先我多少），右列 = 本地独有（我领先远程多少）
  git rev-list --left-right --count "$MY_REMOTE/$MY_BRANCH"...HEAD
  SYNCED_FROM_SHA=$(git rev-parse --short=7 "$MY_REMOTE/$MY_BRANCH")
else
  echo "NOTE: $MY_REMOTE 上还没有 $MY_BRANCH 分支（新建的 fork，或分支被改名）"
  echo "      跳过对齐；第 10 步推送时会创建这个分支"
  SYNCED_FROM_SHA=$(git rev-parse --short=7 HEAD)   # 用本地 HEAD 当参照点，别留空值
  echo "export REMOTE_BRANCH_MISSING=1" >> /tmp/upstream-sync.env
fi

# 把"对齐前/对齐后"都记下来：13.1 的 SYNCED_FROM 和汇报模板都要用，
# 不落盘的话跨 shell 就只剩"我记得当时是多少"，容易写错。
{
  echo "export SYNCED_FROM_SHA=$SYNCED_FROM_SHA"
  echo "export MY_REMOTE_URL=$(git remote get-url "$MY_REMOTE")"
} >> /tmp/upstream-sync.env
echo "ALIGNED: $MY_REMOTE/$MY_BRANCH @ $SYNCED_FROM_SHA"
```

**如果上一步输出显示「左 > 0」（我的仓库比我新），不要只看看表格就往下走——
必须真的把远端合进来。这一步是独立命令块：**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步开头重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空"; exit 1; }

# 远端根本没有这个分支（上面已标记）：没有可 pull 的东西，直接过
if [ "${REMOTE_BRANCH_MISSING:-0}" = "1" ]; then
  echo "ALIGNED_OK: 远端还没有 $MY_BRANCH，无需 pull（首次推送会创建）"
  exit 0
fi

BEHIND=$(git rev-list --count "HEAD..$MY_REMOTE/$MY_BRANCH") \
  || { echo "FAIL: 算不出落后提交数，停下问我"; exit 1; }
echo "落后的提交数=$BEHIND"

if [ "$BEHIND" -eq 0 ]; then
  echo "ALIGNED_OK: 无需 pull"
else
  # 只有工作区干净才能 rebase；第 0 步已经保证了，这里再确认一次
  [ -z "$(git status --porcelain --untracked-files=no)" ] \
    || { echo "FAIL: 工作区不干净，不能 rebase，停下问我"; exit 1; }

  # GIT_EDITOR=: 防止 rebase 中途拉起编辑器把命令挂死
  if GIT_EDITOR=: git pull --rebase "$MY_REMOTE" "$MY_BRANCH"; then
    echo "PULL_OK: 已对齐到 $(git rev-parse --short=7 HEAD)"
    echo "export SYNCED_FROM_SHA=$(git rev-parse --short=7 "$MY_REMOTE/$MY_BRANCH")" \
      >> /tmp/upstream-sync.env
  else
    echo "FAIL: pull --rebase 失败（大概率是冲突）"
    echo "冲突文件："; git diff --name-only --diff-filter=U | head -20
    echo "不要自己 --abort 换 merge、不要 --force；把上面清单发给我"
    exit 1
  fi
fi
```

先确认两边**有共同历史**：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空，停下问我"; exit 1; }

# 远端分支是否存在（仓库可能是新建的空仓库、或分支被改名）
if git rev-parse --verify --quiet "$MY_REMOTE/$MY_BRANCH" >/dev/null; then
  git merge-base HEAD "$MY_REMOTE/$MY_BRANCH" || echo "NO_COMMON_ANCESTOR"
else
  echo "REMOTE_BRANCH_MISSING: $MY_REMOTE 上还没有 $MY_BRANCH 分支"
fi
```

- 若输出 `NO_COMMON_ANCESTOR`：**立刻停下来告诉我**，不要用 `--allow-unrelated-histories` 硬合，等我决定策略。

有共同历史时，按 `git rev-list` 输出（形如 `3	0`）分四种情况：

| 输出 | 含义 | 处理 |
|---|---|---|
| 左 > 0，右 = 0 | 我的仓库比我新，本地是旧版 | **执行 `git pull --rebase "$MY_REMOTE" "$MY_BRANCH"`** |
| 左 = 0，右 = 0 | 已对齐 | 跳过，继续下一步 |
| 左 = 0，右 > 0 | 本地有未推送的提交 | **继续，不要停下**。远端没有新东西，不需要 pull；这些提交留到第 10 步一起推。汇报里注明「本地已有 N 个未推送提交，本次一并推送」 |
| 左 > 0，右 > 0 | 历史分叉 | **停下来问我**，不要自作主张 rebase 或 merge |

**为什么"本地领先"不能停下问你**：无推送权限的机器（`PUSH_MODE=auto/patch`）每轮同步完都会留下
未推送的提交。如果这里一看到"领先"就停，**下一次运行会在同一个地方再停一次，永远走不下去**。
远端没新提交时本地领先是正常状态，不是异常；真正的风险是"分叉"（两边都有新提交），那个才停。

硬性要求：
- pull 一律加 `--rebase`（保持线性历史）；pull 出现冲突就**立刻停下**，把冲突文件交给我，不要自动解决、不要 `--abort` 后偷偷换策略。
- pull 前工作区不干净，先回第 0 步处理，不要 stash 完就 pull。
- pull 完成后确认 `git status --porcelain` 为空，并记录 `$MY_REMOTE/$MY_BRANCH` 的最新短 SHA，写进汇报。
- 这一步**只动 MY_REMOTE**，不碰官方 remote。

## 4. 第 2 步：读取历史记录（必须在第 1 步对齐之后做）

对齐完成后，这些文件可能已被我仓库的版本更新过，所以要**重新**读取。

**先探测日志文件名，结果存进 `$FIXLOG`，从这一步到第 11 步写入全程复用它，不要中途重新判断**（单双 x、大小写都兼容）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
# 探测顺序：根目录 fixxed.txt → 根目录 fixed.txt → 全仓库递归找 fix+ed.txt（不分大小写）
if [ -f fixxed.txt ]; then
  FIXLOG=fixxed.txt
elif [ -f fixed.txt ]; then
  FIXLOG=fixed.txt
else
  # 用 git ls-files 而不是 ls：ls 只看当前目录，
  # 日志被放进 doc/ 或 .config/ 这类子目录时，ls 找不到就会"新建"一个空的，
  # 你原来那份定制记录从此再也不会被读到。
  FIXLOG=$(git ls-files | grep -iE '(^|/)fix+ed\.txt$' | head -1)
  if [ -z "$FIXLOG" ]; then
    FIXLOG=fixxed.txt
    echo "NO_FIXLOG: 全仓库都没找到日志文件，稍后将新建 fixxed.txt（若你确实有历史记录，请告诉我路径）"
  fi
fi

# 多个候选并存时明确提示，不要静默挑一个
CANDS=$(git ls-files | grep -icE '(^|/)fix+ed\.txt$' || true)
[ "$CANDS" -le 1 ] || { echo "WARN: 仓库里有 $CANDS 个疑似日志文件，本次用 $FIXLOG，其余的我没有动"; }

[ -f "$FIXLOG" ] && echo "FIXLOG=$FIXLOG（已存在，$(wc -l < "$FIXLOG") 行）" || echo "FIXLOG=$FIXLOG（将新建）"
echo "export FIXLOG='$FIXLOG'" >> /tmp/upstream-sync.env
```

**读之前先验编码**——Windows 上用记事本编辑过 txt，很容易变成 ANSI/GBK；
这种文件继续用 UTF-8 往上写，会变成中英混杂的乱码，而且 git diff 会显示整个文件都改了：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1

command -v iconv >/dev/null 2>&1 \
  || { echo "FAIL: 环境里没有 iconv，无法验证编码。请安装（Git Bash 自带；Linux 装 glibc-common）"; exit 1; }

for f in "$FIXLOG" action.txt out.txt; do
  [ -f "$f" ] || continue

  # ① 必须是合法 UTF-8
  if ! iconv -f UTF-8 -t UTF-8 "$f" >/dev/null 2>&1; then
    echo "FAIL: $f 不是合法 UTF-8（多半是记事本存成了 ANSI/GBK）"
    echo "      请用 VSCode 打开 → 右下角编码 → 通过编码保存 → UTF-8，然后重跑"
    exit 1
  fi

  # ② 光合法还不够：部分 GBK 双字节序列本身就是合法 UTF-8（解出来是别的字符），
  #    iconv 会放过。同时用 GBK 解一遍，两种都能解且含非 ASCII → 让用户确认。
  if iconv -f GBK -t UTF-8 "$f" >/dev/null 2>&1; then
    # 这里不能用 grep -P（macOS 的 BSD grep 不支持，会静默失败 → 这句 WARN 永远不出现）；
    # 也不能在 UTF-8 locale 下写 [^ -~]（范围会被拆成三个字面字符，几乎匹配所有字母）。
    # LC_ALL=C + [^ -~] 才是"含非 ASCII 字节"的正确写法。
    if LC_ALL=C grep -q '[^ -~]' "$f" 2>/dev/null; then
      echo "WARN: $f 同时能被 UTF-8 和 GBK 解析。若打开看到乱码，请另存为 UTF-8。"
    fi
  fi

  # ③ BOM 会带来两个麻烦：首行匹配失败（out.txt 表头）、前插后 BOM 跑到文件中段
  if [ "$(head -c3 "$f" | od -An -tx1 | tr -d ' \n')" = "efbbbf" ]; then
    echo "NOTE: $f 带 UTF-8 BOM，剥掉"
    # 用 tail -c +4 砍掉前 3 字节，不要用 sed 's/^\xEF\xBB\xBF//'：
    # macOS 的 BSD sed 不认 \xHH 转义，那条命令会静默不动，BOM 一直留着。
    tail -c +4 "$f" > "$f.nobom" && mv "$f.nobom" "$f" \
      || { echo "FAIL: 剥 BOM 失败"; exit 1; }
  fi

  echo "ENCODING_OK  $f"
done
```

**总则：下文凡出现 `fixxed.txt` 字样，实际执行时一律替换为 `$FIXLOG`。** 无论是读取、grep、插入还是 `ls`，都不要用字面量 `fixxed.txt`。

**先把三个文件的存在状态摊开看——不存在是正常情况，不是错误：**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$FIXLOG" ] || { echo "FAIL: FIXLOG 为空，上面探测那步没执行"; exit 1; }

for f in "$FIXLOG" action.txt out.txt; do
  if [ -f "$f" ]; then echo "EXISTS  $f ($(wc -l < "$f") 行)"; else echo "MISSING $f"; fi
done
```

| 文件 | 默认状态 | 不存在时怎么处理 |
|---|---|---|
| `$FIXLOG` | 通常**存在**且有历史内容 | 不算错，稍后新建；汇报里注明「首次建立改动日志」 |
| `action.txt` | **大概率不存在** | **判定为首次同步**：`BASE` 走 `git merge-base`，不报错、不问我 |
| `out.txt` | **大概率不存在** | 不算错，第 11 步写入时先建表头（见 13.2） |

判定细节：
- **三个都不存在** → 标准首次同步，直接往下走。
- **只有 `action.txt` 缺失** → 首次同步，但 `out.txt` 有历史，可在汇报里提一句。
- **`action.txt` 存在、`out.txt` 缺失（或反之）** → 继续，但汇报里注明「记录文件不完整，缺 X」，**不要凭猜测补内容**。
- 任何情况下都**不许**因为文件缺失就停下来问我要不要创建——这是预期内的情况。

然后按顺序读：

1. `$FIXLOG` —— 我此前做过的改动记录。**存在就必须完整读完**；不存在则跳过，本次运行结束时新建。
   你必须**完整读完它**，弄清：我改过哪些文件、为什么改、改成了什么。
   里面的文件是「我自己的改动」，本次同步**必须保留我的修改意图**，绝不能被官方版本直接覆盖。
   本次结束后，你要把新段落**插到文件最前面**（旧内容整体下移），**严格沿用文件里已有的书写格式、标题层级和字段名**，不要另创一套格式。
2. `action.txt` —— 上次同步的进度锚点，其中 `LAST_SYNCED_SHA` 是上次同步到的官方 commit，同时是本次三方合并的**基线（base）**。若不存在，判定首次同步，基线用 `git merge-base` 推导。
3. `out.txt` —— 上次同步得到的 Action 清单，用于对比本次变化。

**解析基线变量**（第 3、4 步都要用，必须在此显式取出，不要等到用时才临时 grep）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
LAST_SYNCED_SHA=$(grep -m1 '^[[:space:]]*LAST_SYNCED_SHA' action.txt 2>/dev/null | cut -d= -f2 | tr -d ' \r')
echo "export LAST_SYNCED_SHA=$LAST_SYNCED_SHA" >> /tmp/upstream-sync.env
echo "LAST_SYNCED_SHA=$LAST_SYNCED_SHA"
```

为空 → 视为首次同步（`action.txt` 不存在或没有该字段），后续走 `merge-base`。

## 5. 第 3 步：拉取官方代码（**先探测分支，再算 HEAD，顺序不能反**）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$UPSTREAM_REMOTE" ] && [ -n "$UPSTREAM_BRANCH" ] \
  || { echo "FAIL: UPSTREAM_REMOTE/UPSTREAM_BRANCH 为空，回第 0 步"; exit 1; }

# ---- ① fetch：显式写 refspec ----
# 为什么不直接 `git fetch <remote> --prune`：能不能拿到 <remote>/<分支>，
# 取决于这个 remote 的 remote.<name>.fetch 配置。单分支克隆（--single-branch / --depth）、
# `git remote add -t 某分支` 的仓库，refspec 只覆盖部分分支，整仓 fetch 也拿不到别的分支。
# 显式 refspec 就不依赖配置了。
if ! git fetch "$UPSTREAM_REMOTE" \
      "+refs/heads/*:refs/remotes/$UPSTREAM_REMOTE/*" --prune; then
  if [ "${CUSTOM_ONLY:-0}" = "1" ]; then
    # 情形 C（只定制、且离线）：拿不到上游 SHA 也能干活，账本段头用本地 HEAD 当参照点。
    # 没有这个分支的话，离线时这里 exit 1，情形 C 的"退化规则"永远走不到。
    echo "WARN: 官方仓库 fetch 失败；CUSTOM_ONLY=1，跳过同步相关计算，用本地 HEAD 作参照点"
    {
      echo "export UPSTREAM_BRANCH='$UPSTREAM_BRANCH'"
      echo "export BRANCH_FALLBACK=0"
      echo "export UPSTREAM_HEAD=$(git rev-parse HEAD)"
      echo "export UPSTREAM_SHORT=$(git rev-parse --short=7 HEAD)"
      echo "export UPSTREAM_REF=HEAD"
      echo "export UPSTREAM_OFFLINE=1"
    } >> /tmp/upstream-sync.env
    echo "UPSTREAM: 离线（只定制模式），参照点=$(git rev-parse --short=7 HEAD)"
    exit 0
  fi
  echo "FAIL: fetch 官方仓库失败，看网络（12.2）"; exit 1
fi

# ---- ② 探测分支是否存在，不存在才回退 ----
if git rev-parse --verify --quiet "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH" >/dev/null; then
  echo "OK: 使用官方分支 $UPSTREAM_BRANCH"
  BRANCH_FALLBACK=0
else
  echo "WARN: 官方 $UPSTREAM_BRANCH 不存在，尝试回退 master"
  UPSTREAM_BRANCH=master
  BRANCH_FALLBACK=1
  git rev-parse --verify --quiet "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH" >/dev/null \
    || { echo "FAIL: 官方 master 也不存在，停下问我"; exit 1; }
fi

# ---- ③ 分支确定之后才算 HEAD（放在回退之前算，回退后拿到的就是空值）----
UPSTREAM_HEAD=$(git rev-parse "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH") \
  || { echo "FAIL: 取不到 $UPSTREAM_REMOTE/$UPSTREAM_BRANCH 的 HEAD"; exit 1; }
[ -n "$UPSTREAM_HEAD" ] || { echo "FAIL: UPSTREAM_HEAD 为空，停下问我"; exit 1; }
UPSTREAM_SHORT=$(git rev-parse --short=7 "$UPSTREAM_HEAD")

# ---- ④ 全部落盘（同一块，否则新 shell 读不到）----
{
  echo "export UPSTREAM_BRANCH='$UPSTREAM_BRANCH'"
  echo "export BRANCH_FALLBACK=$BRANCH_FALLBACK"
  echo "export UPSTREAM_HEAD=$UPSTREAM_HEAD"
  echo "export UPSTREAM_SHORT=$UPSTREAM_SHORT"
  echo "export UPSTREAM_REF=$UPSTREAM_REMOTE/$UPSTREAM_BRANCH"
} >> /tmp/upstream-sync.env

source /tmp/upstream-sync.env || { echo "FAIL: 环境文件回读失败"; exit 1; }
echo "UPSTREAM: $UPSTREAM_REMOTE/$UPSTREAM_BRANCH @ $UPSTREAM_SHORT (fallback=$BRANCH_FALLBACK)"
```

- **顺序不能反**：先 fetch → 再探测分支 → 最后 `rev-parse`。反过来（先算 HEAD、再回退分支）会在回退时
  得到一个空的 `UPSTREAM_HEAD`，后续所有 `git diff "$UPSTREAM_HEAD"` 直接 fatal。
- 回退发生后，**`action.txt` 里的 `LAST_SYNCED_SHA` 一律作废**（那是旧分支的 commit，不能当 master 的基线），
  第 4 步必须强制走 `git merge-base` 重算，汇报里写明「同步源已从 develop/v2/misha-alpha 回退到 master」。
- 回退只影响**同步源**，不影响推送目标：`MY_BRANCH` 仍是本地当前分支名。
- 回退只对**本轮**生效：下次运行仍先按参数区的 `UPSTREAM_BRANCH` 探测，如果 develop 仍不存在会再次自动回退到 master
  （`action.txt` 里的分支名只供人查看，代码不会拿它覆盖参数区）。
- **回退时跳过下面「官方无新提交」的判断**——`LAST_SYNCED_SHA` 属于旧分支，拿它跟 master 比毫无意义，
  直接当作「有更新」继续。

**先跑这条判断，不要靠肉眼比 40 位 SHA**（比错了要么白跑一轮，要么漏同步）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${UPSTREAM_HEAD:?FAIL: UPSTREAM_HEAD 为空，回第 3 步}" \
  "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空}"
cd "$REPO_ROOT" || exit 1

if [ "${BRANCH_FALLBACK:-0}" = "1" ]; then
  echo "HAS_UPDATE: 分支已回退到 $UPSTREAM_BRANCH，旧基线作废，按有更新处理"
  VERDICT=HAS_UPDATE
elif [ -z "$LAST_SYNCED_SHA" ]; then
  echo "FIRST_SYNC: action.txt 没有基线（或文件不存在），按首次同步处理"
  VERDICT=FIRST_SYNC
else
  # 先把两侧都归一化成完整 SHA 再比：action.txt 里可能是 7 位短 SHA，
  # 直接字符串比会永远不等，结果每轮都判成"有更新"。
  A=$(git rev-parse --verify --quiet "${LAST_SYNCED_SHA}^{commit}" 2>/dev/null)
  if [ -z "$A" ]; then
    # 基线对象在本地不存在：浅克隆没拉到、换了台机器没 fetch、官方 force push 删掉了、
    # 或者 action.txt 是从别的仓库拷来的。
    # ★ 这里不能 exit：第 4 步本来就有"解析不到就 merge-base 重算"的兜底，exit 会把那条路堵死。
    # 按"有更新"处理是安全侧——最坏情况是从 merge-base 重新走一遍文件级同步，不会漏东西。
    echo "WARN: LAST_SYNCED_SHA 在本地解析不到 [$LAST_SYNCED_SHA]"
    echo "      按 HAS_UPDATE 处理，基线交给第 4 步用 merge-base 重算"
    echo "export BASELINE_INVALID=1" >> /tmp/upstream-sync.env
    VERDICT=HAS_UPDATE
  else

    # 第二道换成"树级比较"，才是真正独立的信息源：
    # 如果只写 rev-list --count "$A".."$UPSTREAM_REF"，当 A 就是 UPSTREAM_HEAD 时它恒为 0，
    # 两道校验变成同一个判断，永远发现不了异常。
    if git diff --quiet "$A" "$UPSTREAM_REF" 2>/dev/null; then SAME_TREE=1; else SAME_TREE=0; fi
    N=$(git rev-list --count "$A".."$UPSTREAM_REF" 2>/dev/null || echo ERR)

    echo "第一道：$A vs $UPSTREAM_HEAD → $( [ "$A" = "$UPSTREAM_HEAD" ] && echo 相同 || echo 不同 )"
    echo "第二道：树差异=$( [ "$SAME_TREE" = "1" ] && echo 无 || echo 有 )，区间提交数=$N"

    if [ "$SAME_TREE" = "1" ] && [ "$N" = "0" ]; then
      VERDICT=NO_UPDATE
      echo "NO_UPDATE: 官方 HEAD 与上次同步点内容一致"
    elif [ "$SAME_TREE" = "0" ] && [ "$N" != "0" ] && [ "$N" != "ERR" ]; then
      VERDICT=HAS_UPDATE
      echo "HAS_UPDATE: $N 个新提交"
    elif [ "$N" = "ERR" ]; then
      echo "FAIL: rev-list 算不出区间提交数（对象缺失 / 浅克隆），停下问我"; exit 1
    else
      # 两道结论打架。最常见的是「官方 force push 回退」：新 HEAD 是旧同步点的**祖先**，
      # 所以 A..upstream 的提交数是 0，但两棵树不同。
      # 这种情形**故意不自动恢复**：merge-base(HEAD, upstream) 会算出 upstream HEAD 本身，
      # 于是整段回退会被判成"本地改动"全部保留——看起来同步成功，实际把官方回退吃掉了，
      # 还顺手把 action.txt 记成"已同步到回退后的点"，下一轮再也发现不了。
      # 所以停下问人。第 4 步的「情况 c」只处理"基线不在官方历史"的常规失效，不接管这里。
      echo "FAIL: 两道校验结论矛盾（树差异=$( [ "$SAME_TREE" = "1" ] && echo 无 || echo 有 )，区间提交数=$N）"
      echo "  最可能：官方 force push 回退（新 HEAD 是旧基线的祖先）"
      echo "  别自己选：先确认官方是不是回退了，再决定按 merge-base 重算还是保持现状"
      exit 1
    fi

  fi
fi

echo "export SYNC_VERDICT=$VERDICT" >> /tmp/upstream-sync.env
echo "VERDICT=$VERDICT"
```

- 两道**结论一致才继续**；打架就停下问我。第一道是"字符串相等"，第二道是**树级比较**
  （`git diff --quiet`）——两者信息源不同，才能真正交叉验证。

若判定为 `NO_UPDATE`，说明**官方仓库**没有新提交：

- 本次**只跳过同步**，不是跳过整轮。第 1 步的对齐必须已经完成。
- **不做**三方合并、**不修改** `action.txt` / `out.txt`、**不写** `$FIXLOG` 的同步段、**不为同步而 commit**；
  保持工作区干净，这样下次运行时的自检才能通过（这是幂等要求）。
  （唯一例外是有附加需求，见下面第三条——那时会为**定制**单独 commit，同步记录仍然不动。）
- **但如果本地领先 origin（之前有没推成功的提交），仍然要执行第 10 步推送。**
  否则会出现：上一轮 commit 成功、push 失败（没权限/网络），这一轮官方又没更新 →
  永远走不到推送，那些提交就永远卡在本地。判断：
  分支存在时用 `[ "$(git rev-list --count "$MY_REMOTE/$MY_BRANCH"..HEAD 2>/dev/null || echo 0)" -gt 0 ]`；
  远端还没有这个分支（`REMOTE_BRANCH_MISSING=1`）时直接去第 10 步——推送会创建它。
  不要直接写 `[ "$(git rev-list --count ...)" -gt 0 ]`：分支不存在时 rev-list 退出 128、输出空，
  `[ "" -gt 0 ]` 会报 "integer expression expected" 并当成假，本地提交就永远推不上去。
- **走到这里不等于整轮结束。** 先看有没有附加需求：
  - `CUSTOM_TASK` 为空、对话里也没提功能 → 直接结束并告诉我：
    「官方无新提交，本次同步跳过；我已把本地与 NLCLI 对齐到 <短 SHA>，本地 <分支> 与其一致/领先 N 个提交」。
  - `CUSTOM_TASK` 非空、或对话里提了功能 → **不要结束**，跳到 13.9 做定制。
    此时 `action.txt` / `out.txt` **仍然一个字都不动**（没有同步，就没有新的同步记录），只写 `$FIXLOG` 的 `[定制]` 段。
    13.9 的门 2 就是靠 `SYNC_VERDICT=NO_UPDATE` 认"同步已经处理过了"，不要再自己造一个判断。
  - 本地领先 **且** 要定制时：先去第 10 步推掉旧提交，再进 13.9。
    两批提交可以一次 push 出去，但**必须是两个独立 commit**（同步的、定制的），不许 squash。

**特别注意**：这里判断的只是官方有没有新提交。我自己的仓库有没有新推送，
由第 1 步独立判断和处理——两者不要混为一谈。我自己的仓库有新推送就照样 pull，哪怕官方一动不动。

## 6. 第 4 步：确定合并基线

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空，回第 3 步}"
cd "$REPO_ROOT" || exit 1

# 分支回退时旧基线作废（那是另一个分支的 commit），必须重算
if [ "${BRANCH_FALLBACK:-0}" = "1" ]; then
  echo "NOTE: 分支已回退到 $UPSTREAM_BRANCH，action.txt 的旧基线作废"
  BASE=$(git merge-base HEAD "$UPSTREAM_REF") \
    || { echo "FAIL: 回退后 merge-base 失败，停下问我"; exit 1; }
  echo "export BASELINE_REBUILT=1" >> /tmp/upstream-sync.env
elif [ "${BASELINE_INVALID:-0}" = "1" ]; then
  # 第 3 步已经判定 action.txt 的 SHA 在本地解析不到（浅克隆 / 换机器 / force push 删了）。
  # 直接走 merge-base，不要再 rev-parse 一次——那条路刚才已经失败过了，
  # 而且这样 BASELINE_INVALID 才不是个只写不读的死变量。
  echo "NOTE: action.txt 的基线已判定失效（第 3 步），改用 merge-base 重算"
  BASE=$(git merge-base HEAD "$UPSTREAM_REF" 2>/dev/null) \
    || { echo "FAIL: merge-base 失败，停下问我"; exit 1; }
  echo "export BASELINE_REBUILT=1" >> /tmp/upstream-sync.env
else
  # ^{commit} 不能省：--verify 对 tree/blob 也返回成功，基线可能变成非提交对象
  BASE=$(git rev-parse --verify --quiet "${LAST_SYNCED_SHA}^{commit}" 2>/dev/null)
  if [ -z "$BASE" ]; then
    BASE=$(git merge-base HEAD "$UPSTREAM_REF" 2>/dev/null)
    echo "export BASELINE_REBUILT=1" >> /tmp/upstream-sync.env
  fi
fi

[ -n "$BASE" ] || { echo "FAIL: 算不出 BASE（浅克隆？merge-base 失败？），停下问我，不要全量覆盖"; exit 1; }
echo "export BASE=$BASE" >> /tmp/upstream-sync.env
echo "BASE=$BASE ($(git rev-parse --short "$BASE"))"
```

**双重校验（基不可信则全盘皆错）：**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${BASE:?FAIL: BASE 为空}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空}"
cd "$REPO_ROOT" || exit 1

# 退出码要分三态：0=是祖先，1=不是祖先（正常情况），>1=对象读不到（浅克隆/对象缺失）
git merge-base --is-ancestor "$BASE" "$UPSTREAM_REF"; RC_UP=$?
git merge-base --is-ancestor "$BASE" HEAD;           RC_HEAD=$?
echo "RC_UP=$RC_UP  RC_HEAD=$RC_HEAD"

if [ "$RC_UP" -gt 1 ] || [ "$RC_HEAD" -gt 1 ]; then
  echo "FAIL: 对象读不到（浅克隆历史不全？），停下问我"; exit 1
fi

if [ "$RC_UP" -ne 0 ]; then
  # 情况 c：BASE 不在官方历史里 → 基线失效（官方 rebase、action.txt 来自别的仓库/机器、
  # 浅克隆缺对象等），改用 merge-base 重算。
  # ★ 注意边界：官方 **force push 回退** 不走这里——那种情形第 3 步就停下了（见那里的注释），
  #   因为 merge-base 会算出 upstream HEAD，把整段回退当成"本地改动"保留，静默吃掉官方回退。
  echo "NOTE: BASE 不在官方历史中（action.txt 的基线已失效），改用 merge-base 重算"
  NB=$(git merge-base HEAD "$UPSTREAM_REF" 2>/dev/null) \
    || { echo "FAIL: merge-base 也失败，停下问我"; exit 1; }
  echo "export BASE=$NB" >> /tmp/upstream-sync.env
  echo "export BASELINE_REBUILT=1" >> /tmp/upstream-sync.env
  BASE=$NB
  git merge-base --is-ancestor "$BASE" "$UPSTREAM_REF" \
    || { echo "FAIL: 重算后仍不在官方历史中，停下问我"; exit 1; }
  echo "NEW_BASE=$BASE"
fi

echo "export BASE_CASE=$([ "$RC_HEAD" -eq 0 ] && echo a || echo b)" >> /tmp/upstream-sync.env
echo "BASE_OK: case=$([ "$RC_HEAD" -eq 0 ] && echo a || echo b)"

# ★ 最后一道闸：BASE 必须就是 action.txt 里的 LAST_SYNCED_SHA。
# 唯一允许不同的是 BASELINE_REBUILT=1（基线确实失效，上面三种情况已经重算过）。
#
# 为什么非要这条：文件级同步下「LAST_SYNCED_SHA 不是本地 HEAD 祖先」是**常态**，
# 但一旦因为「不是祖先」或者「看起来改动量太大」就擅自回退 merge-base，等于把
# fork 以来官方的全部改动重新合一遍。实测踩过：上游 1 个提交会膨胀成 49 个、
# 50 个文件膨胀成 226 个、0 冲突变成 6 个假冲突；更糟的是上游新代码会被
# 当成「本地定制」跳过，导致同步不完整还看不出来。
if [ "${BASELINE_REBUILT:-0}" != "1" ] && [ -n "${LAST_SYNCED_SHA:-}" ]; then
  EXPECT=$(git rev-parse --verify --quiet "${LAST_SYNCED_SHA}^{commit}" 2>/dev/null)
  if [ -n "$EXPECT" ] && [ "$BASE" != "$EXPECT" ]; then
    echo "FAIL: BASE($(git rev-parse --short "$BASE" 2>/dev/null)) != action.txt 基线($(git rev-parse --short "$EXPECT"))"
    echo "  BASELINE_REBUILT 不是 1，说明基线被无理由换掉了。"
    echo "  最常见的原因：看到「不是本地祖先」就回退 merge-base —— 那是错的。"
    echo "  回本节开头，严格用 action.txt 的 LAST_SYNCED_SHA 当 BASE，不要用 merge-base。"
    exit 1
  fi
fi
echo "BASE_LOCKED: BASE 已锁定，等于 action.txt 的基线"
```

**三种情况，别一看到「不是祖先」就当成坏事：**

| 情况 | 判定 | 处理 |
|---|---|---|
| **a** `RC_HEAD=0` | 在官方历史中，且是本地祖先 | 最规范，直接用 |
| **b** `RC_UP=0 且 RC_HEAD=1` | 在官方历史中，但**不是**本地祖先 | **这是本项目的常态**（文件级同步不产生 merge commit）。**直接用，不要回退** |
| **c** `RC_UP≠0` | 不在官方历史中 | 基线失效：官方 rebase 过、`action.txt` 来自别的仓库/机器、浅克隆缺对象。上一段已用 merge-base 重算并落盘 |

> **情况 c 不包括「官方 force push 回退」**：那种情形第 3 步就停下问你了（新 HEAD 是旧基线的祖先，
> 自动重算会把整段回退当成"本地改动"吞掉）。看到 `BASELINE_REBUILT=1` 时，汇报里必须说明基线换过。

⚠️ **情况 b 千万不要"因为规模大就回退 merge-base"**：文件级同步下，`merge-base HEAD 上游` 永远停在
**当初 fork 的那个 commit**，之后不会再移动。一旦回退到它，等于把 fork 以来官方的**全部**改动
重新合一遍——已经同步过的内容会被二次应用，冲突量爆炸，还可能拿旧版覆盖你的定制。

规模只用来**提醒你**（下一节的交集数量），不用来改基线。

发生回退（情况 c）时，汇报里必须写明：「action.txt 的基线已失效，已改用 merge-base 重算（旧 `xxx` → 新 `yyy`）」。

## 7. 第 5 步：文件分类（本次同步的核心判断）

分别看「官方从 BASE 到 HEAD 改了什么」和「本地从 BASE 到 HEAD 改了什么」：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
git -c core.quotePath=false diff --no-renames --name-status "$BASE" "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH"   # 官方侧改动
git -c core.quotePath=false diff --no-renames --name-status "$BASE" HEAD                                  # 本地侧改动（含我自己的定制）
```

按下面规则给每个文件定性（**这是本次任务最重要的规则**）：

| 情况 | 判定 | 处理方式 |
|---|---|---|
| 只有官方改了，本地没动 | 无冲突 | **直接把官方版本写入本地**（`git checkout "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH" -- <file>`） |
| 只有本地改了，官方没动 | 我的定制 | **原样保留本地**，不做任何改动 |
| 两边都改了，但改的位置不重叠 | 无冲突 | 直接采用**三方合并**结果，两边改动都保留 |
| 两边都改了同一处 | 有冲突 | **由你（AI）自动修正**：理解并保留我的定制意图，同时吸收官方的新内容 |
| 官方新增、本地不存在 | 无冲突 | 直接新增到本地 |
| 官方删除、本地未改过 | 无冲突 | 跟随官方删除（删除前先在汇报里列出，并写进 fixxed.txt） |
| 官方删除、但本地改过该文件 | 我的定制 | **保留本地文件**，并在 fixxed.txt 中说明「官方已删除但本地保留」 |

补充判定：**`fixxed.txt` 里列出的文件，一律按「我的定制」对待**，即使用 git 看起来本地没改，
也走「保留本地 / 冲突需人工裁决」分支。

**双重校验：先量规模，再决定怎么干。**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
# --no-renames：把重命名拆成「删+增」，逐文件处理才不会漏掉旧路径
# core.quotePath=false：否则含中文/空格的路径会被转义成 "\344\270\255.cs"，后面 checkout 必然失败
GD="git -c core.quotePath=false diff --no-renames"

# 必须先落地再看结果：直接 | sort > file 的话，git diff 报错会被管道吞掉，
# 三份清单全变 0 行，AI 会当成"本次没有改动"直接提交空变更。
$GD --name-only "$BASE" "$UPSTREAM_REMOTE/$UPSTREAM_BRANCH" > /tmp/up-raw.txt \
  || { echo "FAIL: 官方侧 diff 失败（BASE 或上游引用无效），停下问我"; exit 1; }
$GD --name-only "$BASE" HEAD > /tmp/local-raw.txt \
  || { echo "FAIL: 本地侧 diff 失败，停下问我"; exit 1; }

LC_ALL=C sort /tmp/up-raw.txt    > /tmp/up-files.txt
LC_ALL=C sort /tmp/local-raw.txt > /tmp/local-files.txt
LC_ALL=C comm -12 /tmp/up-files.txt /tmp/local-files.txt > /tmp/both-files.txt
wc -l /tmp/up-files.txt /tmp/local-files.txt /tmp/both-files.txt

# 两个 0 要区分：真没改动 vs 命令静默失败
UP_N=$(wc -l < /tmp/up-files.txt); LOC_N=$(wc -l < /tmp/local-files.txt)
if [ "$UP_N" -eq 0 ] && [ "$LOC_N" -eq 0 ]; then
  echo "NOTE: 双方都是 0 个改动——先确认不是基线选错（git diff --stat $BASE $UPSTREAM_REF）"
fi
echo "export GD=\"$GD\"" >> /tmp/upstream-sync.env
```

- **交集文件数 > 200** → 先停下来把这三个数字告诉我，问是不是真要全量合并（大概率是官方整体重构了），不要闷头干。
- **交集文件数 ≤ 200** → 正常继续，逐个走三方合并。
- 合并结束后必须复核：**实际产生冲突的文件数**（`git merge-file` 退出码 >0 的数量）应当 ≤ 交集文件数；
  若两者数量明显不符（比如交集 30 个，最终只处理了 5 个），说明漏了文件，回第 5 步重算，不许继续提交。

## 8. 第 6 步：执行同步

**动手前先记一个锚点**，后面反查"到底改了哪些文件"要用：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
PRE_MERGE_HEAD=$(git rev-parse HEAD)
echo "export PRE_MERGE_HEAD=$PRE_MERGE_HEAD" >> /tmp/upstream-sync.env
echo "PRE_MERGE_HEAD=$PRE_MERGE_HEAD"
```

### 8.1 先挑出三种特殊情况（**它们不能进 merge-file**）

**动手前必须先跑这段**，把"一方已删除 / 上游重命名"的文件单独列出来。
不跑的话，这些文件会在 8.2 被静默过滤掉、在 8.3 又被"交集排除"跳过，
最后既没合并也没覆盖，结果取决于上一次的状态——这是最难发现的一类错误。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${BASE:?FAIL: BASE 为空，回第 4 步}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空，回第 3 步}"
cd "$REPO_ROOT" || exit 1
[ -f /tmp/both-files.txt ] || { echo "FAIL: /tmp/both-files.txt 不存在，回第 5 步"; exit 1; }

: > /tmp/special-files.txt
: > /tmp/both-addadd.txt
: > /tmp/gitlink-files.txt
: > /tmp/same-content.txt    # 双方新增但内容完全一致；8.2 必须跳过它们，不能算"三方缺失"
: > /tmp/custom-kept.txt     # 8.1/8.3 记"保持现状"的文件；8.3 结束后无条件并入 extra-log.md

# ① 把交集分成三类：一方不存在 / 双方各自新增 / 子模块指针
while IFS= read -r f; do
  # 子模块（gitlink，mode 160000）：它指向的 commit 在父仓库里根本不存在，
  # `git cat-file -e HEAD:路径` 必然失败，会被误判成"我没这个文件"。
  # 必须先用 ls-tree 看 mode，不能拿 cat-file 判存在性。
  # :(literal) 必须加：文件名里带 * [ ? 时，pathspec 会做成通配匹配，
  # 一次返回多个条目，head -1 取到的可能是别的文件的 mode（把普通文件判成子模块）。
  m1=$(git ls-tree HEAD -- ":(literal)$f" 2>/dev/null | awk '{print $1}' | head -1)
  m2=$(git ls-tree "$UPSTREAM_REF" -- ":(literal)$f" 2>/dev/null | awk '{print $1}' | head -1)
  if [ "$m1" = "160000" ] || [ "$m2" = "160000" ]; then
    echo "$f" >> /tmp/gitlink-files.txt
    # 双方都改子模块指针时，本流程不自动合（父仓库里没有对方的 commit 对象）。
    # 按"保本地"原则保留当前指针，但必须在账本里留痕，不能静默吞掉。
    printf '%s\t%s\n' "$f" "子模块指针双方都变了，保留本地指针（需要上游版本请手动 git submodule update）" >> /tmp/custom-kept.txt
    continue
  fi

  # 符号链接（mode 120000）：merge-file 会把链接目标当普通文本合，8.4 的 cp 又会把它
  # 写成普通文件（类型都变了）。双方都改时直接停，别悄悄弄坏。
  if [ "$m1" = "120000" ] || [ "$m2" = "120000" ]; then
    echo "FAIL: $f 是符号链接且双方都改过，脚本不自动合并符号链接；停下问我"; exit 1
  fi

  b=0; o=0; t=0
  git cat-file -e "$BASE:$f" 2>/dev/null && b=1
  git cat-file -e "HEAD:$f" 2>/dev/null && o=1
  git cat-file -e "$UPSTREAM_REF:$f" 2>/dev/null && t=1

  if [ "$b" = "0" ] && [ "$o" = "1" ] && [ "$t" = "1" ]; then
    # 双方各自新增了同名文件（BASE 里没有）。8.2 会因 BASE 缺失跳过它，
    # 8.3 又因"在交集里"跳过它——不单独挑出来就两边都不处理。
    # 但内容完全一样时不算冲突（两边各自加了个同样的模板/配置文件很常见），
    # 直接留本地即可，否则会为一份完全相同的文件停下来问用户。
    if git diff --quiet "$UPSTREAM_REF:$f" "HEAD:$f" 2>/dev/null; then
      echo "SAME_CONTENT $f（双方新增但内容一致，保留本地）"
      echo "$f" >> /tmp/same-content.txt
    else
      echo "$f" >> /tmp/both-addadd.txt
    fi
  elif [ "$o" != "1" ] || [ "$t" != "1" ]; then
    printf '%s\tbase=%s\tours=%s\ttheirs=%s\n' "$f" "$b" "$o" "$t" >> /tmp/special-files.txt
    # 这两种"保持现状"的结果不会产生冲突标记，第 7 步也不会碰它们，
    # 必须在账本里留痕，否则用户永远不知道上游改过/删过这个文件。
    # （o=0 且 t=0 = 双方都删了，属于一致结果，不用记）
    if [ "$o" = "0" ] && [ "$t" = "1" ]; then
      printf '%s\t%s\n' "$f" "上游有改动，但本地已删除，保留删除" >> /tmp/custom-kept.txt
    elif [ "$o" = "1" ] && [ "$t" = "0" ]; then
      printf '%s\t%s\n' "$f" "上游已删除，但本地有改动，保留本地" >> /tmp/custom-kept.txt
    fi
  fi
done < /tmp/both-files.txt

echo "=== 一方不存在（$(wc -l < /tmp/special-files.txt) 个）==="; cat /tmp/special-files.txt
echo "=== 双方各自新增同名（$(wc -l < /tmp/both-addadd.txt) 个）==="; cat /tmp/both-addadd.txt
echo "=== 子模块指针（$(wc -l < /tmp/gitlink-files.txt) 个）==="; cat /tmp/gitlink-files.txt
echo "=== 双方新增且内容相同（$(wc -l < /tmp/same-content.txt) 个，保留本地）==="; cat /tmp/same-content.txt

# 双方各自新增同一路径 = git merge 里的 "both added" 冲突，必须人工裁决，
# 不能默默保留本地那一份（上游内容会整段丢失且无痕迹）。
if [ -s /tmp/both-addadd.txt ]; then
  echo "FAIL: 以下文件双方各自新增、内容可能不同，请告诉我保留哪一份或怎么合："
  cat /tmp/both-addadd.txt
  exit 1
fi

# ② 上游重命名。diff.renameLimit=0 关掉上限——默认 1000，大更新时 git 会
#    直接放弃检测、退化成 D+A，下面的"重命名+本地修改"就永远拦不住了。
# ★ 警告走的是 **stderr**，必须单独接：只重定向 stdout 的话，
#   "inexact rename detection was skipped" 根本不会进这个文件，下面那句 grep 永远不命中，
#   于是这条防线形同虚设（真遇到时 git 静默退化成 D+A，重命名+本地修改直接被 8.3 当删除处理）。
git -c core.quotePath=false -c diff.renameLimit=0 diff -M --name-status \
  "$BASE" "$UPSTREAM_REF" > /tmp/rename-status.txt 2> /tmp/rename-stderr.txt \
  || { echo "FAIL: 重命名检测失败，停下问我"; exit 1; }
if grep -qi 'rename detection was skipped' /tmp/rename-stderr.txt; then
  echo "FAIL: git 放弃了大文件集的改名检测（见 /tmp/rename-stderr.txt），停下问我"; exit 1
fi
awk -F'\t' '$1 ~ /^R/ {print $2" -> "$3}' /tmp/rename-status.txt | tee /tmp/renames.txt

# ③ 改名的源路径或目标路径命中本地改动 → 必须人工裁决
NEED_DECISION=0
while IFS=$'\t' read -r st oldf newf; do
  [ -z "$oldf" ] && continue
  for p in "$oldf" "$newf"; do
    if grep -Fxq -- "$p" /tmp/both-files.txt; then
      echo "NEED_DECISION: 上游把 $oldf 改名为 $newf，但本地也动过 $p"
      NEED_DECISION=1
    fi
  done
done < <(awk -F'\t' '$1 ~ /^R/' /tmp/rename-status.txt)

[ "$NEED_DECISION" -eq 0 ] || { echo "FAIL: 有重命名叠加本地修改，停下问我，不要自己选"; exit 1; }
echo "RENAMES_OK"
```

**① 重命名（上面③判定通过时）**

- 本地**没碰过**源文件 → 跟随上游：源文件删除、目标文件新增（由 8.3 的 A / D 分支自动完成）；
- 本地**改过**源文件 → 上面已经拦下来，停下问我。

**② 一方文件不存在**（`git show` 会失败，不能硬塞给 merge-file）

| 情况 | 标记 | 处理 |
|---|---|---|
| 本地已删除、上游有修改 | `ours=0 theirs=1` | **保持删除**：**不要执行任何 git 命令**（文件已经不在了，`git rm` 会报错）。写进 fixxed：「上游有改但本地保留删除」 |
| 上游已删除、本地有修改 | `ours=1 theirs=0` | **保留本地文件**：同样**什么都不做**，文件保持现状。写进 fixxed：「上游已删除但本地保留」 |
| 上游已删除、本地也没改 | 不在这里 | 本地没改 → 不在交集 → 由 **8.3 的 D 分支**执行 `git rm` |

**注意这张表里两处都是"什么都不做"**。这是因为 `git checkout` / `git rm` 在这两种状态下都会报错，
而它们要的最终结果恰好就是"保持当前工作区状态"。不要画蛇添足。

**③ 二进制文件**（`git diff --numstat "$BASE" HEAD -- <file>` 输出 `-\t-`）

- 双方都改 → **停下问我**，绝不做文本合并（会把 png/ico/字体/nupkg 写坏）；
- 只有一方改 → 由 8.2（双方都改）或 8.3（仅上游改）处理，二进制只在"双方都改"时才需要拦截。

### 8.2 常规文本文件的三方合并

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${BASE:?FAIL: BASE 为空}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空}"
cd "$REPO_ROOT" || exit 1
rm -rf "$WORK_DIR" && mkdir -p "$WORK_DIR"   # 清掉上一轮残留，避免读到旧 base/ours/theirs
: > "$WORK_DIR/map.txt"

# 8.1 已经挑走的文件（纯路径列）
awk -F'\t' '{print $1}' /tmp/special-files.txt > /tmp/skip-1.txt 2>/dev/null || : > /tmp/skip-1.txt
cat /tmp/both-addadd.txt /tmp/gitlink-files.txt /tmp/same-content.txt >> /tmp/skip-1.txt 2>/dev/null || true

: > /tmp/both-binary.txt
: > /tmp/both-text.txt
: > /tmp/both-big.txt
: > /tmp/both-missing.txt
: > /tmp/merge-error.txt
: > /tmp/clean-merge-log.md
: > /tmp/clean-merge-files.txt

while IFS= read -r f; do
  grep -Fxq -- "$f" /tmp/skip-1.txt && continue

  # 三方都必须存在。少一个，`git show` 会留下空文件，而空文件是 merge-file 的
  # 合法输入——它会把"空 vs 有内容"当成正常差异合出垃圾结果，还不报错。
  git cat-file -e "$BASE:$f" 2>/dev/null         || { echo "$f" >> /tmp/both-missing.txt; continue; }
  git cat-file -e "HEAD:$f" 2>/dev/null          || { echo "$f" >> /tmp/both-missing.txt; continue; }
  git cat-file -e "$UPSTREAM_REF:$f" 2>/dev/null || { echo "$f" >> /tmp/both-missing.txt; continue; }

  # 二进制：两侧都要判。只判本地侧的话，"本地小改 + 上游换成二进制"会被漏掉，
  # 送进 merge-file 就是 255 或损坏内容。
  bin=0
  git -c core.quotePath=false diff --numstat "$BASE" HEAD -- ":(literal)$f" 2>/dev/null | grep -qE '^-[[:space:]]+-' && bin=1
  git -c core.quotePath=false diff --numstat "$BASE" "$UPSTREAM_REF" -- ":(literal)$f" 2>/dev/null | grep -qE '^-[[:space:]]+-' && bin=1
  if [ "$bin" = "1" ]; then
    # 双方都改成了二进制，但内容可能完全一样。文件级同步的典型情况：上一轮已经把
    # 同一份二进制应用过一次，本地和上游逐字节相同。一样就没有可合的东西，
    # 记进 same-content 跳过，不要当成"二进制冲突"停下问人。
    if git diff --quiet "HEAD:$f" "$UPSTREAM_REF:$f" 2>/dev/null; then
      echo "SAME_BINARY $f（双方都是二进制且内容一致，保留本地）"
      echo "$f" >> /tmp/same-content.txt
    else
      echo "$f" >> /tmp/both-binary.txt
    fi
    continue
  fi

  # 体积取两侧较大者（只看本地会漏掉"上游塞进一个大文件"）
  s1=$(git cat-file -s "HEAD:$f" 2>/dev/null || echo 0)
  s2=$(git cat-file -s "$UPSTREAM_REF:$f" 2>/dev/null || echo 0)
  [ "$s1" -ge "$s2" ] && sz=$s1 || sz=$s2
  [ "$sz" -gt 2097152 ] && { echo "$sz $f" >> /tmp/both-big.txt; continue; }

  echo "$f"
done < /tmp/both-files.txt > /tmp/both-text.txt

echo "可文本合并: $(wc -l < /tmp/both-text.txt)"
echo "二进制:     $(wc -l < /tmp/both-binary.txt)"; cat /tmp/both-binary.txt
echo "超大:       $(wc -l < /tmp/both-big.txt)";    cat /tmp/both-big.txt
echo "三方缺失:   $(wc -l < /tmp/both-missing.txt)"; cat /tmp/both-missing.txt

# ★ 覆盖审计：交集里每个文件必须落进某个桶，否则就是"两边都不处理"的静默漏项
TOTAL=$(wc -l < /tmp/both-files.txt)
PICKED=$(cut -f1 /tmp/special-files.txt 2>/dev/null | wc -l)
ADDADD=$(wc -l < /tmp/both-addadd.txt)
GITL=$(wc -l < /tmp/gitlink-files.txt)
SAME=$(wc -l < /tmp/same-content.txt)
TEXT=$(wc -l < /tmp/both-text.txt)
BIN=$(wc -l < /tmp/both-binary.txt)
BIG=$(wc -l < /tmp/both-big.txt)
MIS=$(wc -l < /tmp/both-missing.txt)
COVERED=$(( PICKED + ADDADD + GITL + SAME + TEXT + BIN + BIG + MIS ))
echo "覆盖 $COVERED / 交集 $TOTAL"
if [ "$COVERED" -ne "$TOTAL" ]; then
  echo "FAIL: 有 $((TOTAL-COVERED)) 个文件没被任何一类覆盖，停下问我，不要继续"
  exit 1
fi

[ ! -s /tmp/both-binary.txt ]  || { echo "FAIL: 以下二进制文件双方都改过，停下问我用哪一边："; cat /tmp/both-binary.txt; exit 1; }
[ ! -s /tmp/both-big.txt ]     || { echo "FAIL: 以下超大文件双方都改过，停下问我怎么处理："; cat /tmp/both-big.txt; exit 1; }
[ ! -s /tmp/both-missing.txt ] || { echo "FAIL: 以下文件三方中有一方不存在（8.1 本应挑出），停下问我："; cat /tmp/both-missing.txt; exit 1; }

n=0
while IFS= read -r f; do
  # 临时文件名只用编号（f0001/f0002…），不要拿路径拼。
  # Windows 有 260 字符路径上限，用 "全路径转下划线 + 哈希" 遇到深层目录会直接
  # "File name too long"，而且 core.longpaths 只管 git 自己，管不了 shell 重定向。
  n=$((n+1))
  b=$(printf 'f%04d' "$n")

  # 三个 show 都要看退出码：失败时 > 已经建出空文件，空文件会被 merge-file 当合法输入
  git show "$BASE:$f"          > "$WORK_DIR/base.$b"   || { echo "FAIL: show BASE:$f 失败"; exit 1; }
  git show "HEAD:$f"           > "$WORK_DIR/ours.$b"   || { echo "FAIL: show HEAD:$f 失败"; exit 1; }
  git show "$UPSTREAM_REF:$f"  > "$WORK_DIR/theirs.$b" || { echo "FAIL: show $UPSTREAM_REF:$f 失败"; exit 1; }

  # -L 指定标签：不加的话冲突标记里是 /tmp/upstream-sync/ours.f0001 这种路径，
  # 后面逐块判断"哪边是我的、哪边是官方的"时容易看错。
  git merge-file -L ours -L base -L theirs -p \
    "$WORK_DIR/ours.$b" "$WORK_DIR/base.$b" "$WORK_DIR/theirs.$b" > "$WORK_DIR/merged.$b"
  rc=$?

  # 退出码语义：0=干净；1..127=冲突块数量；>=128=merge-file 自身报错
  # （shell 退出码无符号，git 文档里的"负数"实际是 255）
  if [ $rc -eq 0 ]; then
    echo "CLEAN    $f"
    echo "- $f：三方合并干净，自动采用合并结果" >> /tmp/clean-merge-log.md
    echo "$f" >> /tmp/clean-merge-files.txt
  elif [ $rc -lt 128 ]; then
    echo "CONFLICT($rc) $f"
  else
    echo "ERROR($rc) $f  —— merge-file 自身失败，不写回"
    echo "$f" >> /tmp/merge-error.txt
    rm -f "$WORK_DIR/merged.$b"     # 删掉空结果，防止 8.4 误写回
    continue                        # 不进 map.txt
  fi

  # 空结果保护：三方都是空，结果为空才正常（占位文件、.keep、空 .json 这类）。
  # 只要有一方非空，合并结果就不该为空——那才是 merge-file 出错。
  # 只写 `[ ! -s merged ] && FAIL` 的话，正常合并出来的空文件会被误判成"merge-file 挂了"。
  if [ ! -s "$WORK_DIR/merged.$b" ]; then
    if [ -s "$WORK_DIR/ours.$b" ] || [ -s "$WORK_DIR/theirs.$b" ] || [ -s "$WORK_DIR/base.$b" ]; then
      echo "FAIL: 合并结果为空（$f），但源文件非空，怀疑 merge-file 出错，停下问我"; exit 1
    fi
    echo "EMPTY_OK  $f（三方都是空文件，结果为空正常）"
  fi

  printf '%s\t%s\t%s\n' "$f" "$b" "$rc" >> "$WORK_DIR/map.txt"
done < /tmp/both-text.txt

[ ! -s /tmp/merge-error.txt ] || { echo "FAIL: merge-file 自身报错，停下问我："; cat /tmp/merge-error.txt; exit 1; }
echo "合并完成：map.txt $(wc -l < "$WORK_DIR/map.txt") 行 / 可合并 $(wc -l < /tmp/both-text.txt) 个"
[ "$(wc -l < "$WORK_DIR/map.txt")" -eq "$(wc -l < /tmp/both-text.txt)" ] \
  || { echo "FAIL: 写回清单条数 != 可合并文件数"; exit 1; }
```

- **临时文件名用递增编号**（`f0001`、`f0002`…），不要用 `basename`，也不要用"路径转下划线"：
  前者会让不同目录的同名文件（多个 `AboutSettingsPage.axaml.cs`）互相覆盖；后者遇到深层路径会把
  Windows 的 260 字符上限撑爆（`core.longpaths` 只作用于 git，不作用于 shell 的 `>`）。
  编号与原路径的对应关系记在 `$WORK_DIR/map.txt`。
- 退出码 `0` → 干净合并，把 `merged.*` 写回本地对应文件；
- 退出码 `>0` → 有冲突块（数量即退出码），进第 7 步；
- 退出码 `>=128`（shell 里表现为 255）→ merge-file 自身报错（编码/二进制/权限），**停下问我**，不要当成"无冲突"。
  注意 git 文档说的"负数"在 shell 里拿不到负数。
- **BOM 说明**：这个仓库约有 114 个文本文件带 UTF-8 BOM（开头 3 字节 `EF BB BF`）。
  `merge-file` 会把 BOM 当作第一行的一部分，正常情况三方都有或都没有，不会有影响。
  只有当冲突恰好出现在**文件第 1 行、且差异只是 BOM 有无**时，**保持本地原来的 BOM 状态**，
  不要把 BOM 删掉或加两遍——`.NET` 的编译器对带 BOM 的源文件有特殊处理，改错会报奇怪的行号偏移。

### 8.3 仅上游改动的文件：直接采用上游版本

双方都改过的文件已经单独处理，剩下的「只有上游改过」的文件不需要任何合并，直接取上游版本：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${BASE:?FAIL: BASE 为空}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空}"
cd "$REPO_ROOT" || exit 1
[ -f /tmp/both-files.txt ] || { echo "FAIL: both-files.txt 不存在，回第 5 步"; exit 1; }

# --no-renames 很关键：不加的话重命名会输出 R100<TAB>旧名<TAB>新名 三个字段，
# 下面的 read -r st f 会把新名读进 f、旧名直接丢掉，删错文件。
git -c core.quotePath=false diff --no-renames --name-status \
  "$BASE" "$UPSTREAM_REF" > /tmp/up-status.txt \
  || { echo "FAIL: 官方侧 diff 失败，停下问我"; exit 1; }

# 去掉双方都改的（它们已在 8.2 处理）
# 必须用 FILENAME==ARGV[1]，不能用 NR==FNR：
# 两个文件求差集时，若第一个文件是空的（零交集，也就是最常见的"完全没冲突"），
# NR==FNR 对第二个文件的每一行都成立 → 整份清单被 next 吞掉 → 一个文件都不处理，
# 而 AI 会以为"同步完成"，直接提交一个空变更。
# ⚠️ 两个文件的列数不同：
#   both-files.txt 每行只有"路径"一列（$1），
#   up-status.txt 每行是"状态<TAB>路径"（$2 才是路径）。
# 第一个文件若误写成 $2，$2 是空的，both[""]=1，一个都排除不掉 →
# up-only 变成"上游全部改动"，交集文件会被 8.3 再覆盖一遍（覆盖掉 8.2 的合并结果）。
awk -F'\t' 'FILENAME==ARGV[1]{both[$1]=1;next} !($2 in both)' \
  /tmp/both-files.txt /tmp/up-status.txt > /tmp/up-only-status.txt \
  || { echo "FAIL: 求差集失败"; exit 1; }

echo "上游改动 $(wc -l < /tmp/up-status.txt) 个，其中仅上游改 $(wc -l < /tmp/up-only-status.txt) 个"
cut -f1 /tmp/up-only-status.txt | sort | uniq -c

# 对账：上游改动 = 仅上游改 + 交集里被 8.1/8.2 处理掉的
# 不等就说明有漏项，宁可停下也别提交半成品
UPN=$(wc -l < /tmp/up-status.txt)
ONLY=$(wc -l < /tmp/up-only-status.txt)
BOTH=$(wc -l < /tmp/both-files.txt)
echo "对账：上游 $UPN =? 仅上游 $ONLY + 交集 $BOTH = $((ONLY+BOTH))"
[ "$UPN" -eq "$((ONLY+BOTH))" ] \
  || { echo "FAIL: 上游改动数 != 仅上游 + 交集，有文件既没合并也没覆盖，停下问我"; exit 1; }

APPLIED=0; FAILED=0; SKIPPED_CUSTOM=0
while IFS=$'\t' read -r st f; do
  [ -z "$f" ] && continue
  case "$st" in
    A|M|T)
      # :(literal) 强制把路径当字面量。不加的话，上游若有 a[1].txt 这种名字，
      # 会被当成通配符，顺手把同目录的 a1.txt 也覆盖掉。
      if git checkout "$UPSTREAM_REF" -- ":(literal)$f"; then
        echo "$st  $f"; APPLIED=$((APPLIED+1))
      else
        echo "FAIL $st $f"; FAILED=$((FAILED+1))
      fi ;;
    D)
      # 上游删了，但 fixxed.txt 里记着这是我的定制 → 保留，不删
      # ★ 这里扫**全文**是有意的：历史上任何一轮记过的定制，上游删了都要保留。
      #   13.3 的账本反查只看"本轮顶部段落"，两处口径不同、不要统一（目标不一样）。
      if [ -n "$FIXLOG" ] && [ -f "$FIXLOG" ] && grep -Fq -- "$f" "$FIXLOG"; then
        echo "KEEP_CUSTOM  $f（上游已删除，但 fixxed 里有记录，保留本地）"
        printf '%s\t%s\n' "$f" "上游已删除，但 fixxed.txt 记为定制，保留本地" >> /tmp/custom-kept.txt
        SKIPPED_CUSTOM=$((SKIPPED_CUSTOM+1))
      elif git rm -q --ignore-unmatch -- ":(literal)$f"; then
        echo "DEL   $f"; APPLIED=$((APPLIED+1))
      else
        echo "FAIL D $f"; FAILED=$((FAILED+1))
      fi ;;
    *)
      echo "FAIL: 未知状态 [$st] $f，停下问我，不要瞎猜"
      exit 1 ;;
  esac
done < /tmp/up-only-status.txt

echo "应用 $APPLIED 个，保留定制 $SKIPPED_CUSTOM 个，失败 $FAILED 个"
[ "$FAILED" -eq 0 ] || { echo "FAIL: 有文件没应用成功，停下问我，不要提交半成品"; exit 1; }

# ★ 把"保持现状"的决定无条件并入 extra-log。
#   不能放在第 7 步：零冲突时 AI 很可能直接跳过第 7 步，那些记录就丢了。
#   用标记文件防重跑重复追加。
if [ -s /tmp/custom-kept.txt ] && [ ! -f /tmp/custom-kept.merged ]; then
  while IFS=$'\t' read -r f why; do
    [ -n "$f" ] && echo "- $f：$why" >> /tmp/extra-log.md
  done < /tmp/custom-kept.txt
  : > /tmp/custom-kept.merged
fi
```

### 8.4 把三方合并结果写回工作区

8.2 只在 `$WORK_DIR` 里生成了结果，还要落回仓库。**冲突文件也照样写回**——写回去后文件里带着
`<<<<<<<` 标记，第 7 步直接在这个仓库文件上改，比在临时文件上改不容易搞错路径。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}"
cd "$REPO_ROOT" || exit 1

# map.txt 必须存在：8.2 无论有没有可合并文件都会建它（哪怕是空的）。
# 缺失 = 8.2 没跑完或 WORK_DIR 被清过，绝不能当成"本次无冲突"放过去。
[ -f "$WORK_DIR/map.txt" ] || { echo "FAIL: map.txt 不存在，8.2 没跑完，回 8.2"; exit 1; }

WROTE=0; FAILED=0
while IFS=$'\t' read -r f b rc; do
  [ -z "$f" ] && continue
  # 只写回 map.txt 里真正成功的那些（rc<128 且结果非空）
  if [ ! -s "$WORK_DIR/merged.$b" ]; then
    echo "FAIL: 结果为空或不存在 $f（b=$b rc=$rc）"; FAILED=$((FAILED+1)); continue
  fi
  if cp "$WORK_DIR/merged.$b" "$f"; then
    echo "WROTE  $f"; WROTE=$((WROTE+1))
  else
    echo "FAIL: 写回失败 $f（文件被占用？）"; FAILED=$((FAILED+1))
  fi
done < "$WORK_DIR/map.txt"

echo "写回 $WROTE 个，失败 $FAILED 个"
[ "$FAILED" -eq 0 ] || { echo "FAIL: 有文件写回失败，停下问我，不要留半成品"; exit 1; }

# 落回后再扫一遍：有标记是预期（待第 7 步修），但要能数清有几个文件。
# 只扫本次真正写回的文件——全仓扫会把 patches/ 里的补丁、记录文件里的 ==== 也算进来。
: > /tmp/conflict-files.txt
while IFS=$'\t' read -r f b rc; do
  [ -z "$f" ] && continue
  [ -f "$f" ] || continue
  if grep -qE '^(<<<<<<< |>>>>>>> |\|\|\|\|\|\|\| )' "$f"; then
    echo "$f" >> /tmp/conflict-files.txt
  elif [ "$rc" != "0" ] && grep -qE '^=======$' "$f"; then
    # 防御性兜底，正常流程里走不到：这一步是"刚写回、还没修"，有冲突就必然三件套齐全，
    # 第一个 if 已经命中。保留它只是防万一（比如某种 merge-file 只写出部分标记）。
    # 真正要抓"只剩中间那条 ======="的是 7.1 的重扫（修完之后），那里是无条件查的。
    echo "$f" >> /tmp/conflict-files.txt
  fi
done < "$WORK_DIR/map.txt"
echo "带冲突标记的文件: $(wc -l < /tmp/conflict-files.txt) 个"
cat /tmp/conflict-files.txt

# 把"修之前"的清单另存一份：第 7 步修完后要重扫、跟这份对比，
# 这样才能证明冲突真的清干净了，而不是"我没再扫"。
cp /tmp/conflict-files.txt /tmp/conflict-files.before.txt 2>/dev/null \
  || : > /tmp/conflict-files.before.txt
echo "已保存修前清单：$(wc -l < /tmp/conflict-files.before.txt) 个文件"
```

**可执行位是在哪里丢的**（别搞错归因，否则会对不需要修的文件白修）：

- **8.2 走过的文件**（`git show ... > base/ours/theirs`）——`>` 创建的是 umask 决定的普通文件，
  mode **在这一步就丢了**；8.4 的 `cp` 只是照搬那个 644，不会恢复。这类文件才需要 chmod。
- **8.3 覆盖的文件**（`git checkout <ref> -- path`）——git 按索引里的 mode 写回，`100755` 会保留。
  这类文件不需要 chmod，硬修反而可能把不该可执行的文件改成可执行。

处理方式按平台分：

- **Linux / macOS**：`chmod +x <文件>` 修回来，用 `git ls-files -s <文件>` 查原来的 mode（`100755` 表示可执行）。
- **Windows（Git Bash）**：`core.filemode=false`，`chmod` 基本不起作用，改了也不会进索引。
  这种文件直接**跳过权限修复**，在汇报里注明「N 个可执行位未恢复，Windows 平台限制」，不要谎报已修好。

## 9. 第 7 步：冲突的人工（AI）修正规则

> **铁律 2 回扣**：合并的第一目标是**保住我的定制**，第二目标才是吸收官方。
> 宁可少吸收一个上游特性，也不能丢一行我的改动。

对每个带冲突标记的文件：

**开始处理之前，清空本轮的两份手工日志**（conflict-log / buildfix-log；extra-log 已由第 0 步清空，并在 8.3 结束灌入了"保持现状"记录，这里不能再清）：

```
: > /tmp/conflict-log.md
: > /tmp/buildfix-log.md
# extra-log.md 不在这里清：第 0 步已经清过，而且 8.3 结束时已经把
# "保持现状"的 custom-kept 灌进去了。这里再清会把那些记录抹掉。
echo "LOGS_RESET"
```

0. **每处理完一个文件，立刻把结果追加到 `/tmp/conflict-log.md`**，不要攒着——
   后面还要构建、排查、写三个记录文件，跨了十几个命令块，细节很容易记错。
   （13.3 只从 `/tmp/conflict-files.before.txt` 取文件名，把它标成「冲突修正」；
    这里写的过程细节不进账本，只是给你自己排查时看的）

   ```
   cat >> /tmp/conflict-log.md <<EOF
   - <文件路径>:<行号>
     - 原状: <改之前是什么>
     - 改后: <改成了什么>
     - 理由: <为什么这么改>
     - 影响面: <波及哪些调用方/模块>
   EOF
   ```

   - 开始处理冲突前先 `: > /tmp/conflict-log.md` 清空，避免带上上一轮的旧内容；
   - 本次完全没有冲突也要建这个文件（写一行 `- 本次无冲突需要修正。`），13.3 直接读。

1. 先读 `fixxed.txt` 中关于该文件的条目，明确「我当初为什么改这里、想达到什么效果」。
2. 再读冲突区块的官方侧（`theirs`），理解官方这次动了什么。
3. 产出**一份不带任何冲突标记**的最终文件，要求：
   - 我的定制意图 100% 保留（这是我的底线，宁可保留我的版本，也不要为了吸收官方改动而丢掉我的改动）；
   - 官方与本处无关的其他改动照常吸收；
   - 如果官方改动和我的定制**在语义上真的互斥、无法同时成立**，**不要擅自取舍**，把这个文件、两边差异、你的建议方案列成清单，停下来问我。
4. 改完执行下面的自检。**必须锚定整行**——否则源码里的 `// ======` 横幅、
   记录文件里的 `====` 分隔线都会被误判成冲突标记，AI 会去"修"一个不存在的问题：

   ```
   F="<文件>"
   grep -nE '^(<<<<<<< |=======$|>>>>>>> |\|\|\|\|\|\|\| )' "$F"
   rc=$?
   # 三态：0=有残留，1=干净，≥2=grep 自己出错（文件不存在/读不了）
   # 只写 `grep ... && FAIL` 会把"grep 报错"也走成干净——那才是真正的假阴性。
   [ "$rc" -eq 1 ] || { echo "FAIL: 冲突标记残留或 grep 出错（rc=$rc），不许往下走"; exit 1; }
   echo "CLEAN"
   ```

   > 这里包含 `=======$` 是有意的：文件本来就是冲突文件，只剩中间那条分隔符是真实的漏修形态。
   > 10.1 的全改动扫描不加它——那里扫的是所有改动文件，Markdown 的 7 等号下划线会误报。

5. **第二道校验：确认我的定制没被吃掉。** 只"没有冲突标记"是不够的——AI 完全可能把两边都删掉、或整段用官方版本覆盖，一样没有冲突标记，但我的改动没了。

   从 `fixxed.txt` 里找到该文件对应的定制描述，抽 1–3 个**可 grep 的关键片段**做存在性断言：

   ```
   grep -n 'windows-arm64' .github/workflows/build_release.yml   # 计数必须 > 0
   ```

   - 断言失败（计数为 0）→ 说明我的定制被合并掉了，**回到冲突区块重新合**，不许提交。
   - 若该文件的定制无法用字符串表达（比如"调整了任务顺序"），改为**逐行对照 `ours` 侧**：
     凡是被删除或被改写的行，必须能说出"这是我有意为之"的理由，说不出就恢复。
   - 把每个冲突文件用了哪条断言、结果如何，写进 `fixxed.txt` 的对应条目。

特别提醒：`.github/workflows/*.yml` 属于 YAML，改动后必须保证 YAML 语法合法（缩进、锚点、`on:` 触发条件等）。

### 7.1 全部修完之后，重扫一遍（必做）

上面是逐文件处理。全部处理完之后，**必须重新扫一次**并把结果覆盖到 `/tmp/conflict-files.txt`
——不重扫的话，13.3 里"冲突标记残留"那一栏读到的还是**修之前**的数量，账本会写成"残留 N 个"，
而实际上已经修好了。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}"
cd "$REPO_ROOT" || exit 1

: > /tmp/conflict-files.txt
if [ -f /tmp/conflict-files.before.txt ]; then
  while IFS= read -r f; do
    [ -n "$f" ] && [ -f "$f" ] || continue
    # 这里保留 =======：清单里的文件本来就有过冲突，只剩中间那条分隔符 = 没修干净。
    grep -qE '^(<<<<<<< |=======$|>>>>>>> |\|\|\|\|\|\|\| )' "$f" && echo "$f" >> /tmp/conflict-files.txt
  done < /tmp/conflict-files.before.txt
fi

N=$(wc -l < /tmp/conflict-files.txt | tr -d ' ')
echo "冲突标记残留: $N 个文件"
if [ "$N" -gt 0 ]; then
  cat /tmp/conflict-files.txt
  echo "FAIL: 还有文件带冲突标记，回去修，不许往下走"
  exit 1
fi
echo "CONFLICTS_CLEARED"
```

注意上面读的是 `/tmp/conflict-files.before.txt`（修之前那份清单，由 8.4 结束时保存）。

## 10. 第 8 步：一致性校验与构建验证（**必须做，不通过就不许提交**）

### 10.1 残留检查与语法校验

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}" "${PRE_MERGE_HEAD:?FAIL: PRE_MERGE_HEAD 为空，回第 6 步}"
cd "$REPO_ROOT" || exit 1

# 只扫「本次改过的文件」，不要 grep -r 整个仓库：
#   ① 全仓扫会命中历史文档里自带的 ===== 横幅（Markdown 标题下划线很常见）→ 每轮误报；
#   ② 也会命中 fixxed.txt 里引用的冲突片段（账本里贴了 <<<<<<< 原文）→ 同样误报；
#   ③ 二进制文件被 grep 命中时输出 "Binary file matches" 且退出码 0，看起来像有残留。
# 只认 <<<<<<< / >>>>>>> / |||||||：git 写冲突块时这两个必然成对出现，
# 而单独一行 ======= 在普通文档里完全合法，不能当证据。
CHANGED=$(git -c core.quotePath=false diff --no-renames --name-only "$PRE_MERGE_HEAD") \
  || { echo "FAIL: 取改动清单失败（PRE_MERGE_HEAD 无效或仓库不对），停下问我"; exit 1; }
FIXLOG_RE=$(printf '%s' "${FIXLOG:-fixxed.txt}" | sed 's/\./\\./g')   # 记录文件名里的 . 要转义
if [ -n "$CHANGED" ]; then
  printf '%s\n' "$CHANGED" \
    | grep -vE "^(action\.txt|out\.txt|${FIXLOG_RE}|patches/.*)$" > /tmp/scan-files.txt
else
  : > /tmp/scan-files.txt        # 空清单时别留一行空行，否则下面会报"1 个文件"却什么都没扫
fi
echo "本次改动的文件: $(wc -l < /tmp/scan-files.txt) 个"

: > /tmp/leftover.txt
while IFS= read -r f; do
  [ -n "$f" ] && [ -f "$f" ] || continue      # 已删除的文件跳过
  grep -nIE '^(<<<<<<< |>>>>>>> |\|\|\|\|\|\|\| )' "$f" >> /tmp/leftover.txt
done < /tmp/scan-files.txt

if [ -s /tmp/leftover.txt ]; then
  cat /tmp/leftover.txt
  echo "FAIL: 上面是残留的冲突标记，回去修，不许往下走"; exit 1
fi
echo "NO_CONFLICT_MARKERS"
```

**只有当本次确实改动了 `.github/workflows/*.yml` 时才做这一步**——`FAST=1` 下绝大多数同步不涉及 workflow，直接跳过：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1

# 取「工作区对同步前锚点」的改动，而不是 BASE→上游——后者会把上游删除的 workflow 也算进来，
# 对着一堆已删除文件跑 open() 必然 FAIL，AI 会去修一个不存在的文件。
# 这里不要写成 `|| true`：diff 报错（锚点无效/仓库不对）会被吞成"没有改动"，
# 于是 YAML 校验被静默跳过，汇报里还写着"未执行（本次无 workflow 变更）"。
CHANGED=$(git -c core.quotePath=false diff --no-renames --name-only "$PRE_MERGE_HEAD" -- .github/workflows/) \
  || { echo "FAIL: 取 workflow 改动失败（PRE_MERGE_HEAD 或仓库不对），停下问我"; exit 1; }
echo "本次改动的 workflow: ${CHANGED:-（无）}"
[ -n "$CHANGED" ] || { echo "SKIP: 本次没有改动 workflow，跳过 YAML 校验"; }

# PyYAML 不一定装了（Git Bash 常常连 python3 都没有），没有就跳过并如实说明，不能算 FAIL
YAML_OK=0
if command -v python3 >/dev/null 2>&1 && python3 -c 'import yaml' >/dev/null 2>&1; then YAML_OK=1; fi
[ "$YAML_OK" = "1" ] || echo "WARN: 无 python3/PyYAML，本次跳过 YAML 语法校验（汇报里必须写明）"

fail=0
while IFS= read -r f; do
  [ -n "$f" ] || continue
  [ -f "$f" ] || { echo "SKIP(已删除) $f"; continue; }
  case "$f" in *.yml|*.yaml) ;; *) echo "SKIP(非YAML) $f"; continue ;; esac
  [ "$YAML_OK" = "1" ] || continue
  python3 -c "import yaml,sys;yaml.safe_load(open(sys.argv[1],encoding='utf-8'));print('OK  '+sys.argv[1])" "$f" \
    || { echo "FAIL $f"; fail=1; }
done <<EOF
$CHANGED
EOF
[ "$fail" -eq 0 ] || { echo "FAIL: YAML 语法有问题，修好再提交"; exit 1; }
```

- **有 FAIL → 停下修，修不好就告诉我，不要提交。**
- 没有 python3/PyYAML 导致跳过时，汇报里必须写「YAML 校验未执行（环境缺 PyYAML）」，不许写成"通过"。

### 10.2 构建验证

**先做三项前置检查，缺一项就会白编一遍**（都是毫秒级，`FAST=1` 下也照做——昨天踩的坑正是它们）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1

# ① 子模块（"-" 前缀 = 未初始化；MSB3202 就是它引起的）
#    直接在这里处理掉，不要只打印状态——只打印的话 AI 会"看到了但没动"，一路编到 MSB3202。
if [ -f .gitmodules ]; then
  git submodule status
  if git submodule status 2>/dev/null | grep -q '^-'; then
    echo "NOTE: 有未初始化子模块，执行 init"
    # SSH 地址在没有密钥的机器上会挂住；先试一次 HTTPS 改写，不行再报错
    git -c url."https://github.com/".insteadOf="git@github.com:" \
        submodule update --init --recursive \
      || { echo "FAIL: 子模块初始化失败（.gitmodules 用了 SSH 且没有密钥？）停下问我"; exit 1; }
  fi
fi

# ② tag（失败 = 无 tag，AssemblyInfo.cs 会把 git 的报错文本当版本号 → CS7034）
#    同样在这里补齐，不要只打印一行让用户自己发现。
if ! git describe --tags >/dev/null 2>&1; then
  echo "NOTE: 本地没有可用 tag，补拉一次"
  git fetch "$UPSTREAM_REMOTE" --tags 2>/dev/null || echo "WARN: 补 tag 失败（离线/无权限）"
  git describe --tags >/dev/null 2>&1 \
    || echo "WARN: 补 tag 后仍无 tag，构建可能报 CS7034（AssemblyInfo 把报错当版本号）"
fi
git describe --tags 2>&1 | head -1

# ③ .NET SDK 提示（真正的版本判定在下面的构建块里用 --list-sdks 做）
#    这里不能用 dotnet --version 硬判：global.json 钉 10.0 而机器只有 8/9 时，
#    --version 本身就会失败，会把后面的"版本不足"友好分支提前堵死，而且不会落 BUILD_RESULT。
if command -v dotnet >/dev/null 2>&1; then
  echo "dotnet --version: $(dotnet --version 2>/dev/null || echo '读不到（global.json 可能钉了未安装的 SDK，下面用 --list-sdks 判）')"
else
  echo "NO_DOTNET: 当前环境没有 dotnet，构建验证无法执行（汇报里必须写明）"
  echo "export BUILD_RESULT='未执行（环境无 dotnet SDK）'" >> /tmp/upstream-sync.env
fi
```

- 子模块和 tag 的处理**已经写进上面的代码块**：看到 `-` 前缀会自动 `submodule update --init`；
  `git describe` 失败会自动 `fetch --tags`。你只需要确认输出里没有 FAIL/WARN。
- **不要直接 `dotnet build <sln>`**：解决方案里含 Android / macOS 等平台项目，当前系统装不了对方的 workload，必然 NETSDK1147。
  **统一构建桌面宿主 `ClassIsland.Desktop`**——它会连带编译 Core、Shared、业务主项目和当前平台的平台层，
  覆盖面最接近真实发布产物：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1

# Desktop 宿主不存在（仓库结构变了、或分支不同）时自动回退，不要让整轮卡死。
# 但必须留下 BUILD_TARGET_FALLBACK=1 的痕迹——回退后的目标覆盖面比 Desktop 小，
# 汇报里要写明"构建面已缩水"，不能笼统说"构建通过"。
if [ ! -f "$TARGET_PROJ" ]; then
  echo "WARN: 找不到 $TARGET_PROJ，回退到 ClassIsland/ClassIsland.csproj"
  TARGET_PROJ=ClassIsland/ClassIsland.csproj
  echo "export TARGET_PROJ=$TARGET_PROJ" >> /tmp/upstream-sync.env
  echo "export BUILD_TARGET_FALLBACK=1" >> /tmp/upstream-sync.env
fi
[ -f "$TARGET_PROJ" ] || { echo "FAIL: 两个候选项目都不存在，停下问我"; exit 1; }
echo "TARGET_PROJ=$TARGET_PROJ"
```

**构建只有一个命令块，按 `FAST` 分派**（下面这段是唯一的构建入口，不要在上面再单独跑一次
`dotnet build` 试手——那会白编一遍，而且失败结果不会进 `BUILD_RESULT`）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${TARGET_PROJ:?FAIL: TARGET_PROJ 为空}"
cd "$REPO_ROOT" || exit 1
[ -f "$TARGET_PROJ" ] || { echo "FAIL: $TARGET_PROJ 不存在"; exit 1; }

# 没有 dotnet 时直接落"未执行"，不要往下走——否则 dotnet build 会以 127 退出，
# 把"未执行"覆盖成"失败"，后续还会进 3 轮修复流程白折腾。
if ! command -v dotnet >/dev/null 2>&1; then
  echo "NO_DOTNET: 未执行构建验证"
  echo "export BUILD_RESULT='未执行（环境无 dotnet SDK）'" >> /tmp/upstream-sync.env
  exit 0
fi

# 版本要够：上游 global.json 钉 10.0。只装 8/9 时 dotnet --version 本身就会失败，
# 所以用 --list-sdks 判，而不是解析 --version 的输出。
dotnet --list-sdks 2>/dev/null | head -5
MAXV=$(dotnet --list-sdks 2>/dev/null | sed 's/^[^0-9]*//' | cut -d. -f1 | sort -n | tail -1)
[ -n "$MAXV" ] || { echo "FAIL: 读不到任何 SDK"; exit 1; }
[ "$MAXV" -ge 10 ] || {
  echo "FAIL: 需要 .NET 10 SDK，当前最高只有 $MAXV.x。装好再重跑，不要带着旧 SDK 硬编"
  echo "export BUILD_RESULT='未执行（SDK 版本不足：最高 $MAXV.x）'" >> /tmp/upstream-sync.env
  exit 1
}

set -o pipefail

if [ "${FAST:-1}" = "1" ]; then
  # 快速通道：单次增量，靠 MSBuild 缓存只重编受影响工程
  dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal 2>&1 | tail -20
  BE=${PIPESTATUS[0]}          # 必须紧跟着取，中间任何 echo 都会覆盖它
  echo "BUILD_EXIT=$BE"
  if [ "$BE" -eq 0 ]; then
    echo "export BUILD_RESULT='通过（增量单次）'" >> /tmp/upstream-sync.env
  else
    echo "export BUILD_RESULT='失败（BUILD_EXIT=$BE）'" >> /tmp/upstream-sync.env
  fi
else
  # 完整：restore + 增量 + 禁用增量全量各一次
  dotnet restore "$TARGET_PROJ"; RESTORE_EXIT=$?
  dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal; BE=$?
  dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal --no-incremental; BE2=$?
  echo "RESTORE_EXIT=$RESTORE_EXIT BUILD_EXIT=$BE BUILD_EXIT_2=$BE2"
  if [ "$RESTORE_EXIT" -eq 0 ] && [ "$BE" -eq 0 ] && [ "$BE2" -eq 0 ]; then
    echo "export BUILD_RESULT='通过（restore + 增量 + 全量）'" >> /tmp/upstream-sync.env
  else
    echo "export BUILD_RESULT='失败（r=$RESTORE_EXIT b=$BE b2=$BE2）'" >> /tmp/upstream-sync.env
  fi
fi
```

判定：

| 情况 | 结论 |
|---|---|
| `FAST=1`，`BUILD_EXIT=0` | 通过 |
| `FAST=0`，三个退出码全 0 | 通过 |
| 非 0 | 失败 → 进 10.3 修复流程 |
| 输出 `NO_DOTNET` / `SDK 版本不足` | **未执行**，不是失败。汇报里必须照实写，不许写"通过" |


**两种模式共同的规则：**
- **`FAST=1` 下一旦构建失败，修复后的重验必须切到 `--no-incremental`**——缓存会掩盖"改坏了但没重编"的情况；
- `FAST=0` 的两次构建结果不一致（一次过、一次不过）→ 以失败为准，这通常意味着增量缓存掩盖了问题。
- 构建可能耗时较久，耐心等待，不要中途判定为卡死。
- **构建失败 → 进入 10.3 的修复流程，不许直接提交、不许推送。**
- 构建失败时**保留工作区现状，不要 `git checkout .` 回滚**，我要看到现场。
- 构建通过 → 汇报里写明「构建通过」，继续。

### 10.3 构建失败后的修复流程（**每轮 3 步，最多 3 轮**）

**先看清楚结构，别把"步"当成"轮"**：

```
一轮 = ① 定性  →  ② 修  →  ③ 重验
        ↑                      │
        └──── 不通过就回 ① ────┘   （最多 3 轮）

③ 通过            → 进入第 9 步提交
③ 不通过且未满 3 轮 → 回 ① 开下一轮
③ 不通过且已满 3 轮 → 停止，交材料给我
```

常见的错误是"修一次、重验不过就停"——那等于放弃。同步引入的编译错误通常不止一处，
第一轮修完还剩几个是正常的，继续下一轮。

#### ① 定性（每轮都从这里开始）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${TARGET_PROJ:?FAIL: TARGET_PROJ 为空}" "${PRE_MERGE_HEAD:?FAIL: 锚点丢失}"
cd "$REPO_ROOT" || exit 1

# 轮次要落盘：每轮的 ①②③ 都是独立 shell，不落盘的话"第几轮"只存在于 AI 的记忆里，
# 上下文一压缩就丢，最后要么修一次就宣称"已满 3 轮"，要么无限循环。
FIX_ROUND=$(( ${FIX_ROUND:-0} + 1 ))
echo "export FIX_ROUND=$FIX_ROUND" >> /tmp/upstream-sync.env
echo "=== 第 $FIX_ROUND 轮（上限 3）==="
if [ "$FIX_ROUND" -gt 3 ]; then
  echo "FAIL: 已超过 3 轮，立即停止，把材料交给用户"; exit 1
fi

# 「本次同步动过的文件」只在第一轮算一次并冻结：
# 每轮重新算的话，AI 自己上一轮改过的文件会被算进"A 类=同步引入"，
# 于是越来越敢改官方逻辑；新建文件又永远不在清单里，一直判成 B 类。
[ -f /tmp/synced-files.txt ] || {
  # ★ 先写进临时文件再排序，不要写成 `{ ... || exit 1; } | sort > 目标`：
  #   管道左侧跑在子 shell 里，exit 1 只退出子 shell，右边的 sort 照样成功，
  #   整条管道的退出码是 0，后面的 || 永远不触发 → git diff 失败被静默吞掉，
  #   清单为空或不完整，本轮所有编译错误都会被误判成"官方自带"而不去修。
  { git -c core.quotePath=false diff --no-renames --name-only "$PRE_MERGE_HEAD"
    git ls-files --others --exclude-standard
  } > /tmp/synced-files.raw.txt || { echo "FAIL: 取改动清单失败（git 报错），停下问我"; exit 1; }
  LC_ALL=C sort -u /tmp/synced-files.raw.txt > /tmp/synced-files.txt \
    || { echo "FAIL: 清单排序失败"; exit 1; }
  [ -s /tmp/synced-files.txt ] \
    || { echo "FAIL: 改动清单为空，本轮什么都没改？停下问我，不要拿空清单继续修构建"; exit 1; }
  echo "已冻结 synced-files.txt（$(wc -l < /tmp/synced-files.txt) 个文件）"
}

# 定量性用的构建必须 --no-incremental：
# 增量编过、全量不过 是真实场景，用增量去"看还有什么错误"会得到空输出，
# AI 就会以为"没有错误了"，白跑一轮。
set -o pipefail
dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal --no-incremental 2>&1 \
  | tee /tmp/build-errors.log | grep -E 'error [A-Z]+[0-9]+|error :|错误 ?[A-Z]+[0-9]+|错误 ?:' | sort -u
echo "错误行数: $(grep -cE 'error [A-Z]+[0-9]+|error :|错误 ?[A-Z]+[0-9]+|错误 ?:' /tmp/build-errors.log || true)"
```

先把每个错误归到下面三类之一，并在对话里列出来：

| 类型 | 判断依据 | 修复策略 |
|---|---|---|
| **A. 同步引入** | 报错文件在本次同步改动清单内 | 必须修，修完重验 |
| **B. 官方自带** | 报错文件不在清单内，且未同步时官方版本同样编不过 | 只做最小兼容修复，不要大改官方逻辑，并在汇报里注明 |
| **B2. 环境占用**（Windows） | 报错含 `being used by another process`、`MSB3021`、`MSB3027`、`拒绝访问` | **不要改代码**。告诉我是 Visual Studio / 杀软占着 `bin`、`obj`，关掉后重跑 |
| **C. 定制与上游 API 冲突** | 本地定制调用了官方已改名/改签名的 API | **修定制侧**，让定制适配新 API，不许为了保留定制去改官方语义 |

- **CS0246 / 找不到类型（真实踩过）**：先查这个类型是不是**本地分支删掉的文件**——
  `git cat-file -e "HEAD:<路径>"` 为假、`git cat-file -e "$UPSTREAM_REF:<路径>"` 为真。
  是的话优先**保留本地删除**：把引用它的文件回退到本地版本（`git checkout HEAD -- <文件>`），
  不要为了编译把本地删掉的水印/开发版组件再拉回来；构建面缩水在汇报里写明。
- **本地删除但上游这一轮没改的文件**：它不在 `both-files` 里，8.1 看不见，只会在构建时以 CS0246 暴露——
  按上面这条"先查本地删除"处理，不要直接去上游补文件。

无法归类 → 停下来把错误和判断依据给我，不要硬猜。

#### ② 修（只改出错点及其直接依赖）

**修复范围的硬边界：**

- 只改**出错点及其直接依赖**，不做顺手重构、不做格式化、不重命名无关符号。
- 保持原有行为不变，除非原行为本身就是 bug（此时要在 fixxed.txt 里写明"原行为是 bug"的理由）。
- **禁止用下面这些手段把编译糊过去**（发现即视为任务失败）：
  - 注释掉报错的代码、直接删功能
  - `catch (Exception) { }` 空吞异常
  - 关闭 `TreatWarningsAsErrors`、改 csproj 降级、改 `TargetFramework` 绕过
  - 用 `!` 或 `#nullable disable` 压制警告而不做真实判空
  - 删测试、改断言让测试通过

**防御性编程的适用范围（很重要，别把它当成"必须大改官方代码"的许可）：**

| 你改动的位置 | 要求 |
|---|---|
| **本次新增的代码**（新文件、新方法） | 下表**全部适用** |
| **你自己写的定制代码**（fixxed.txt 里记着的那些） | 下表**全部适用** |
| **官方既有代码**（只是为了编过而改的） | 只要求：不新增空吞异常、不新增无判空的解引用、不注释掉功能。**不要为了"更防御"去改官方签名或重构**——那会让下一轮同步的冲突面继续扩大 |

特别说明两条容易被误用的：

- **"异步方法带 `CancellationToken`"**：只对**你新增的公开异步方法**成立。
  官方已有的方法哪怕缺 `CancellationToken` 也不许改签名（改了会破坏接口实现和 override，直接编不过）。
- **"禁止 `!`"**：如果某个位置上游已经用 nullable 标注为可能为空、但你确认框架保证了非空，
  可以写 `x!`，**必须同时加一行注释说明"为什么这里能保证非空"**。不许为了消掉 CS8602
  而批量加 `!`，也不许因为"禁止 `!`"就卡在那里耗完 3 轮。

**具体清单：**

| 场景 | 要求 |
|---|---|
| 可能为 null 的入参 | 先判空，给出明确异常（`ArgumentNullException`）或安全默认值，不靠调用方保证 |
| 集合 / 数组索引 | 访问前校验长度或改用 `TryGetValue` / `ElementAtOrDefault` |
| 外部输入（配置、命令行参数、文件内容、网络响应） | 一律校验：非空、范围、格式；非法输入要给出可读错误，不许静默继续 |
| 异常处理 | 只捕获**具体类型**；捕获后要么记日志、要么包装成业务异常重新抛出，不静默吞 |
| 资源 | 用 `using` / `await using`，不在 `finally` 里做可能抛异常的操作 |
| 异步 | 方法签名带 `CancellationToken`；禁止 `async void`（事件处理器除外）；禁止 `.Result` / `.Wait()` 阻塞 |
| 并发 | 共享可变状态要么加锁、要么用不可变结构；不用 `lock(this)` |
| 依赖 | 不新增 NuGet 包；确有必要必须说明理由并告诉我 |
| 日志 | 新增的失败分支要有日志，日志里不打印敏感信息（路径、令牌） |

修完一处就在对话里说明：**哪个文件、什么错误码、根因是什么、怎么改的、影响面多大**。

#### ③ 重验

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
set -o pipefail
dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal; BUILD_A=$?
dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal --no-incremental; BUILD_B=$?
echo "BUILD_A=$BUILD_A BUILD_B=$BUILD_B"

# 修复成功后必须回写 BUILD_RESULT，否则 action.txt 会一直记着第一次的"失败"
if [ "$BUILD_A" -eq 0 ] && [ "$BUILD_B" -eq 0 ]; then
  echo "export BUILD_RESULT='通过（修复后重验）'" >> /tmp/upstream-sync.env
else
  echo "export BUILD_RESULT='失败（修复后仍不过：A=$BUILD_A B=$BUILD_B）'" >> /tmp/upstream-sync.env
fi
```

- **修复后的重验固定跑这两条，不受 `FAST` 影响**——修复场景必须全量，缓存不可信。
- 两次都通过 → 进入第 9 步提交（`BUILD_RESULT` 已被改写成「通过（修复后重验）」）。
- **不通过 → 看 `$FIX_ROUND`**：
  - `< 3` → 回 **①**（它会自动把轮次 +1），继续修；
  - `= 3` → **立即停止**，把三样材料交给用户，不许再改。

  ```
  source /tmp/upstream-sync.env 2>/dev/null || exit 1
  echo "当前轮次 FIX_ROUND=${FIX_ROUND:-?} / 上限 3"
  [ "${FIX_ROUND:-3}" -lt 3 ] && echo "ROUND_CONTINUE" || echo "ROUND_EXHAUSTED"
  ```
- **最多 3 轮**（每轮 = 定性 + 修 + 重验）。第 3 轮的 ③ 仍失败 → **立即停止**，把下面三样交给我，不许继续瞎改：
  1. 仍未消除的错误清单（原样贴输出）
  2. 已经尝试过的修改及失败原因
  3. 当前 `git diff --stat` 和 `git diff`（我要能看清你动了什么）

#### 修复留痕

每一处修复都要写进 `fixxed.txt` 的新增段落，包含：**文件 / 错误码 / 根因 / 修法 / 影响面 / 是否属于防御性补强**。
这部分内容同时要出现在最终汇报的「构建修复」小节里。

同样**边修边落盘**，不要等到 13.3 再回忆（`/tmp/buildfix-log.md`，13.3 会拼进去）：

```
cat >> /tmp/buildfix-log.md <<EOF
- <文件:行号>  <错误码>
  - 根因: <为什么这里编不过>
  - 修法: <怎么改的>
  - 影响面: <波及哪些调用方/模块>
  - 是否防御性补强: <是/否，补了哪一项（判空/边界/异常/资源/并发）>
EOF
```

开始修之前先 `: > /tmp/buildfix-log.md`；本次构建一次通过也要建（写一行 `- 无，构建一次通过。`）。

### 10.4 收尾执行顺序（一次说清，别来回跳）

构建通过后，按这个顺序走完，中间不要回头：

1. **13 节（第 11 步）** 写三个记录文件：`action.txt`、`out.txt`、`$FIXLOG`
2. **11 节（第 9 步）** `git add -A -- . ':(exclude)patches' …` + `git commit`
3. **12 节（第 10 步）** 推送，或按 `PUSH_MODE` 导出补丁
4. **13.9（第 13 步）** 只有 `CUSTOM_TASK` 非空 / 我提了功能需求时才做：定制 → 构建 → 写 `[定制]` 段 → **第二个** commit → 再推/再导补丁
5. **14 节（第 12 步）** 汇报（有定制就一次汇报完，别拆成两次）

**章节号比步骤号大 2**，看到「13 节」不要以为是第 13 步——那是第 11 步的内容。
**13.9 才是第 13 步**，它排在推送之后、汇报之前，别把两者搞混。

## 11. 第 9 步：提交（**必须先把第 11 步的三个记录文件写完，再执行本步**）

先跳到 13 节（第 11 步）写完 `action.txt` / `out.txt` / `$FIXLOG`，再回来一起提交
（暂存时要用 pathspec 排除 `patches/`、`bin/`、`obj/`，见下面的命令块）。
顺序颠倒会导致记录文件落在 commit 之外，工作区永远是脏的。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${UPSTREAM_SHORT:?FAIL: UPSTREAM_SHORT 为空}" \
  "${FIXLOG:?FAIL: FIXLOG 为空}" "${BUILD_RESULT:?FAIL: BUILD_RESULT 为空}"
cd "$REPO_ROOT" || exit 1

# ---- 先确认三个记录文件都已写好 ----
for f in action.txt out.txt "$FIXLOG"; do
  [ -s "$f" ] || { echo "FAIL: $f 不存在或为空，先回 13 节写完再提交"; exit 1; }
done
echo "三个记录文件就绪：action.txt / out.txt / $FIXLOG"

# ---- 暂存：显式排除不该进库的东西 ----
# 不要直接 git add -A：补丁产物、提示词文件、bin/obj 都会被一起提交
# ★ 先清掉写记录文件时可能残留的中间文件：上一块中途失败/被打断时，
#   action.txt.lf / out.txt.tmp / out.txt.bak / out.txt.lf / $FIXLOG.tmp 会留在工作区，
#   直接 add 就会被当成"新文件"提交进仓库，下次第 0 步又因为未跟踪文件被挡住。
rm -f action.txt.lf out.txt.tmp out.txt.lf out.txt.bak "$FIXLOG.tmp" 2>/dev/null || true

git add -A -- . \
  ':(exclude)patches' \
  ':(exclude)bin' ':(exclude)*/bin' \
  ':(exclude)obj' ':(exclude)*/obj' \
  ':(exclude)*.tmp' \
  ':(exclude)action.txt.lf' ':(exclude)out.txt.lf' ':(exclude)out.txt.bak' \
  ':(exclude)*.prompt.md' ':(exclude)prompt.md'

# 保险：任何残留的可执行产物都踢出暂存区
git reset -q -- patches 2>/dev/null || true
git diff --cached --name-only | grep -E '(^|/)(bin|obj)/' \
  && { echo "FAIL: bin/obj 进了暂存区，先修 .gitignore 再 git reset"; exit 1; }
git diff --cached --name-only | grep -E '\.(tmp|lf|bak)$' \
  && { echo "FAIL: 中间文件进了暂存区（上面这些），git reset 掉再提交"; exit 1; }

echo "=== 暂存区统计 ==="
git diff --cached --name-status | awk '{print $1}' | sort | uniq -c
echo "=== 记录文件是否已暂存 ==="
git diff --cached --name-only | grep -E "^(action\.txt|out\.txt|${FIXLOG})$" || {
  echo "FAIL: 记录文件没进暂存区（被上面的 exclude 规则误伤了？）"; exit 1; }

# 身份没配时用临时身份，不改我的全局配置
AUTHOR_OPTS=()
git config user.name  >/dev/null 2>&1 || AUTHOR_OPTS+=(-c user.name="AI Sync Bot")
git config user.email >/dev/null 2>&1 || AUTHOR_OPTS+=(-c user.email="sync@local")

# heredoc 不加引号：$UPSTREAM_SHORT / $BUILD_RESULT 等会自动展开，
# 不用 AI 手抄占位符（手抄十个里错三个）。
CN_ALL=$(git rev-list --count "$BASE".."$UPSTREAM_REF" 2>/dev/null || echo 0)

git "${AUTHOR_OPTS[@]}" commit -F - <<MSGEOF
chore(sync): 同步 $UPSTREAM_BRANCH 至 $UPSTREAM_SHORT

- 基线 $(git rev-parse --short "$BASE") -> 目标 $UPSTREAM_SHORT，上游 $CN_ALL 个提交
- 上游改动 $(wc -l < /tmp/up-files.txt | tr -d ' ') 个文件，双方都改 $(wc -l < /tmp/both-files.txt | tr -d ' ') 个，发生过冲突 $(cat /tmp/conflict-files.before.txt 2>/dev/null | wc -l | tr -d ' ') 个（已全部修正）
- 冲突处理详见 $FIXLOG 顶部段落
- 本地定制全部保留（水印/更新页/关于页/开源提示/贡献者）
- 构建验证：$TARGET_PROJ → $BUILD_RESULT
MSGEOF

echo "=== 提交结果 ==="
git log --oneline -1
echo "=== 提交信息里是否还有未替换的占位符 ==="
if git log -1 --format=%B | grep -nE '<[^>]{1,40}>'; then
  echo "FAIL: 提交信息里有占位符残留，用 git commit --amend 修（这是唯一允许 amend 的场景）"
else
  echo "MSG_OK"
fi
```

**提交后立刻验一次**（记录文件必须真的在这个 commit 里，不能只在工作区）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失"; exit 1; }
: "${REPO_ROOT:?}" "${FIXLOG:?}"
cd "$REPO_ROOT" || exit 1

git show --name-only --format= HEAD > /tmp/commit-files.txt
for f in action.txt out.txt "$FIXLOG"; do
  grep -qxF "$f" /tmp/commit-files.txt \
    || { echo "FAIL: $f 不在刚才的 commit 里"; exit 1; }
done
echo "COMMIT_OK: 记录文件已入库"

# 工作区应当干净（patches/ 和提示词文件如果在仓库里，会在 .git/info/exclude 里被忽略）
git status --porcelain | grep -vE '^(\?\? patches/)' | head -5
echo "剩余未提交（上面为空才算干净）"
```

- 提交信息**必须带 body**，列出本次冲突文件与处理方式（上面的模板就是干这个的，用 `-F -` 从标准输入读，
  不要用 `-m` 拼一大串转义）。占位符 `<...>` 全部替换成真实内容，不许原样留下。
- **禁止** `--force`、**禁止** `--no-verify`、**禁止**跳过任何 hook。hook 报错就修，修不了就停下来告诉我。
- **hook 可能改文件**（常见于自动格式化）：commit 成功后必须复核——
  `git status --porcelain` 应为空、`grep` 冲突标记应为零、定制点断言仍通过。
  若 hook 改动了文件，检查它是否引入冲突标记或改坏定制；有问题就修完再补一个 commit（暂存时同样排除 patches/bin/obj，不要 `--amend`），
  并在汇报里注明「pre-commit hook 改动了 N 个文件」。
- `git add` 前先确认不会把 `bin/`、`obj/`、`patches/`（补丁产物）、`/tmp` 下的中间文件、以及这份提示词文件本身加进去。

## 12. 第 10 步：推送到我自己的仓库（三重保险）

**先看 `PUSH_MODE` 决定走哪条路：**

| PUSH_MODE | 行为 |
|---|---|
| `auto`（默认） | 执行本节全部三道；失败且是无权限 → 进 12.1 导出补丁 |
| `direct` | 执行本节全部三道；**失败即停**，不进 12.1，如实报错 |
| `patch` | **跳过本节**，直接执行 12.1 导出补丁 |

**第 1 道：推送前重新验 URL**（不要复用第 0 步的旧结论，中间的 pull 有可能改动 remote 配置）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$UPSTREAM_REMOTE" ] || { echo "FAIL: remote 变量为空，回第 0 步"; exit 1; }

MY_U=$(git remote get-url --push "$MY_REMOTE")
UP_U=$(git remote get-url --push "$UPSTREAM_REMOTE")
echo "我的 push URL: $MY_U"
echo "官方 push URL: $UP_U"

# 注意是 exit 1，不是 echo。只 echo 的话脚本继续往下跑，
# 下一块是独立 shell，根本看不到这行 STOP，照样真推。
echo "$MY_U" | grep -qi 'farmer-snack/nlcli' \
  || { echo "STOP: 我的 push URL 不含 NLCLI，绝不推送"; exit 1; }
echo "$UP_U" | grep -qi 'classisland/classisland' \
  || { echo "STOP: 官方 remote 的 URL 异常，停下问我"; exit 1; }

# 两个 remote 不能是同一个（那意味着配置被搞乱了）
[ "$MY_REMOTE" != "$UPSTREAM_REMOTE" ] \
  || { echo "STOP: 我的和官方指向同一个 remote"; exit 1; }
echo "URL_OK"
```

**第 2 道：dry-run 预演**（真正推送前先跑一遍，确认要推的 ref 和目标都对）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空，回第 0 步"; exit 1; }

set -o pipefail
GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never \
  git push --dry-run "$MY_REMOTE" "HEAD:$MY_BRANCH" > /tmp/push-dry.log 2>&1
DRY_EXIT=$?
echo "DRY_EXIT=$DRY_EXIT"
cat /tmp/push-dry.log
```

**判据一律基于 `/tmp/push-dry.log` 全文，不要用 `tail -5`**：非快进时那几行关键信息
（`[rejected]`、`non-fast-forward`）不在最后 5 行里，`tail -5` 刚好把它们切掉，
只剩一堆 `hint:`，AI 就会误判成"看不懂 → 直接真推"。

**预演结果也要分类看，不要一报错就停住：**

| 预演输出（在 `push-dry.log` 里 grep） | 处理 |
|---|---|
| `DRY_EXIT=0`，或日志含 `Everything up-to-date` | 继续第 3 道（无新提交时第 3 道直接跳过） |
| `could not read Username`、`Authentication failed`、`403`、`Permission denied`、`terminal prompts disabled`、`Invalid username or password`、`Repository not found`、`Permission to .* denied` | **判定无权限 → 进 12.1 导出补丁**，不要再试真推 |
| `non-fast-forward`、`[rejected]`、`fetch first` | 停下问我，不要强推 |
| `Could not resolve host`、`timed out`、`Connection refused`、`GnuTLS`、`RPC failed`（且不含上面的认证关键字） | 进 12.2 代理探测 |
| 其它非零退出 | **原样贴日志停下问我**，不要自己归类 |

⚠️ **判定顺序是「先认证、后网络」**：GitHub 的 `RPC failed; HTTP 403` 同时含 `403` 和 `RPC failed`，
必须先判成无权限，否则会白跑两轮代理探测。

⚠️ `PUSH_MODE=direct` 时，上表所有"进 12.1"的行都改成"停下并贴原始日志"。

⚠️ 无权限的机器上，`--dry-run` 同样要认证，所以它经常就是**第一个暴露「没权限」的地方**。
别把它当成"预演失败 = 不能推"就停住——认证类失败要直接走降级。

**第 3 道：真推 + 事后核对（失败时按 `PUSH_MODE` 决定降级）**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失或损坏，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空，回第 0 步"; exit 1; }

# GIT_TERMINAL_PROMPT=0 必须有：没有它，无凭据的机器会卡在
# "Username for 'https://github.com':" 一直等输入，命令挂死，
# 而且错误分类表依赖的 "could not read Username" 永远不会出现。
GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never GIT_ASKPASS=/bin/true \
  git push "$MY_REMOTE" "HEAD:$MY_BRANCH" > /tmp/push.log 2>&1
PUSH_EXIT=$?
echo "PUSH_EXIT=$PUSH_EXIT"
cat /tmp/push.log
```

- `PUSH_EXIT = 0` → 核对远端：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空"; exit 1; }
LOCAL_HEAD=$(git rev-parse HEAD)
REMOTE_HEAD=$(git ls-remote "$MY_REMOTE" "refs/heads/$MY_BRANCH" | cut -f1)
echo "本地 HEAD : $LOCAL_HEAD"
echo "远端 HEAD : $REMOTE_HEAD"
# 取不到不等于不一致：网络断了 ls-remote 会返回空，直接判"不一致"会把网络问题
# 报成"推送没落地"，让人去查 GitHub，方向完全错。
[ -n "$REMOTE_HEAD" ] || { echo "FAIL: ls-remote 取不到远端 SHA（网络？），无法核对，停下问我"; exit 1; }
[ "$LOCAL_HEAD" = "$REMOTE_HEAD" ] \
  || { echo "FAIL: 远端 HEAD 与本地不一致，推送可能没真正落地，停下问我"; exit 1; }
echo "PUSH_VERIFIED"
```

- `PUSH_EXIT ≠ 0` → **先判断错误类型，不要一律报失败**：

| 日志里的关键字 | 判定 | 处理 |
|---|---|---|
| `Authentication failed`、`403`、`Permission denied`、`could not read Username`、`terminal prompts disabled`、`Invalid username or password` | **无推送权限** | 进入 12.1 降级模式 |
| `non-fast-forward`、`[rejected]`、`fetch first` | 远端有新提交 | 停下问我，不要强推 |
| `Could not resolve host`、`timed out`、`Connection refused`、`early EOF`、`RPC failed`、`GnuTLS` | 网络问题 | 进入 12.2 代理探测，最多尝试 2 轮 |

### 12.1 降级模式：导出补丁（`PUSH_MODE=auto` 或 `patch` 时）

**触发条件（满足任一即进入）：**
- `PUSH_MODE=patch` —— 无条件进入，**跳过推送的前三道**；
- `PUSH_MODE=auto` 且第 2 道或第 3 道判定为「无推送权限」；
- `PUSH_MODE=direct` —— **不进入本节**，直接如实报错。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] || { echo "FAIL: 变量为空"; exit 1; }
mkdir -p patches

# ★ 先加进本仓库的 exclude：否则本轮结束时 patches/ 是未跟踪文件，
# 下一轮第 0 步"未跟踪文件数必须为 0"会直接把你挡在门外。
grep -qxF 'patches/' .git/info/exclude 2>/dev/null || echo 'patches/' >> .git/info/exclude

# 补丁起点依赖「远端真实状态」，所以先拉一次。不拉的话，第 1 步之后远端可能又有新提交，
# merge-base 会基于过期引用算出偏早的起点，对方 git am 必冲突。
git fetch "$MY_REMOTE" --prune 2>/dev/null \
  || echo "WARN: fetch $MY_REMOTE 失败（无权限机器常见），改用 ls-remote 取远端真实 SHA"

# ★ 不能退回去用本地跟踪引用：它可能停在几轮之前（fetch 一直失败），
#   拿它算 merge-base 会得出偏早的起点，补丁里混进已经推送过的提交，对方 git am 必冲突。
#   也不能只判断"分支是否存在"——存在但引用过期，同样会算错。必须拿到远端真实 SHA。
REMOTE_SHA=$(git ls-remote --heads "$MY_REMOTE" "refs/heads/$MY_BRANCH" 2>/dev/null | awk '{print $1}' | head -1)
[ -n "$REMOTE_SHA" ] \
  || { echo "FAIL: ls-remote 拿不到远端 $MY_BRANCH 的 SHA（网络 / 权限 / 分支不存在），补丁起点无法确定，停下问我"; exit 1; }
LOCAL_TRACK=$(git rev-parse --verify --quiet "refs/remotes/$MY_REMOTE/$MY_BRANCH")
if [ "$LOCAL_TRACK" != "$REMOTE_SHA" ]; then
  echo "NOTE: 本地跟踪引用 ${LOCAL_TRACK:-不存在} ≠ 远端 $REMOTE_SHA，按远端算起点"
fi

if ! PATCH_BASE=$(git merge-base HEAD "$REMOTE_SHA" 2>/dev/null); then
  # 远端刚推的提交，本地可能还没有对应对象（前面 fetch 失败过）。
  # fetch 是只读的，一般都有权限，再拉一次通常就齐了；还不行才停。
  echo "NOTE: 本地缺少 $REMOTE_SHA 对应的对象，按分支再 fetch 一次后重算"
  # 先按 SHA 直接取（GitHub 支持按 commit 取），拿不到再退回按分支取。
  # 按分支取的问题是：fetch 期间远端又推进的话，拉下来的是更新的提交，不一定是 $REMOTE_SHA。
  git fetch "$MY_REMOTE" "$REMOTE_SHA" 2>/dev/null \
    || git fetch "$MY_REMOTE" "+refs/heads/$MY_BRANCH:refs/remotes/$MY_REMOTE/$MY_BRANCH" 2>/dev/null \
    || true
  PATCH_BASE=$(git merge-base HEAD "$REMOTE_SHA" 2>/dev/null) \
    || { echo "FAIL: 算不出 merge-base（对象仍缺失，或确实无共同祖先），停下问我"; exit 1; }
fi
echo "export PATCH_BASE=$PATCH_BASE" >> /tmp/upstream-sync.env
PATCH_N=$(git rev-list --count "$PATCH_BASE"..HEAD)
echo "export PATCH_N=$PATCH_N" >> /tmp/upstream-sync.env   # 下一块生成 apply.txt 要用
echo "PATCH_BASE=$PATCH_BASE  待导出 $PATCH_N 个提交"

if [ "$PATCH_N" -eq 0 ]; then
  # 千万不要往下走：format-patch 输出空内容会 > 把上一个有用的补丁截成 0 字节。
  # 但也别把旧补丁留在原地——它是上一轮的，里面那些提交现在都已经在远端了，
  # 用户照着它 git am 会全部冲突（"already applied"），还以为是这次生成的。
  if [ -f patches/update.txt ]; then
    mv -f patches/update.txt patches/update.txt.stale
    echo "NOTE: 旧的 update.txt 已改名成 update.txt.stale（历史产物，不要拿去 git am）"
    echo "      确认本轮不需要补丁后，这个 .stale 可以手动删掉；下一轮生成了新的 update.txt 时它不会被自动清理"
  fi
  echo "NO_PATCH: 远端已包含全部提交，本次无需补丁"
  ls -lh patches/ 2>/dev/null
else
  # --binary 必须有：ClassIsland 里有大量 png/ico/字体/资源文件，
  # 不加这个参数，二进制改动在补丁里只剩一行 "Binary files ... differ"，内容全丢，
  # 对方 git am 之后资源是坏的，构建或运行才暴露。
  # --full-index 也加上：补丁头里带完整 blob SHA，万一要用 git apply --3way 才有得救。
  # --no-encode-email-headers：不加的话中文提交标题会变成 =?UTF-8?q?...?= 一长串，
  # 我用记事本打开补丁会以为文件坏了。git am 两种都能正确还原，只是可读性差别。
  git format-patch "$PATCH_BASE"..HEAD --binary --full-index --no-encode-email-headers --stdout > "patches/update.txt.tmp"
  if [ -s "patches/update.txt.tmp" ]; then
    mv "patches/update.txt.tmp" "patches/update.txt" \
      || { echo "FAIL: 覆盖 patches/update.txt 失败（文件被占用？关掉编辑器/杀软重试）"; exit 1; }
    echo "OK: 已写入 patches/update.txt（$PATCH_N 个提交）"
  else
    rm -f "patches/update.txt.tmp"
    echo "FAIL: 补丁生成为空，旧补丁已保留，停下问我"; exit 1
  fi

  # 再导一份「从本次同步前的 HEAD 起算」的小补丁（只含本次新增的提交）。
  # 为什么必须有：PATCH_BASE 是跟远端算的合并基点，我电脑上可能**已经存在**
  # PATCH_BASE 之后的若干提交（比如上次同步做完了但没推上去）。这时拿 update.txt
  # 去 git am，前面那几个补丁会全部 "patch does not apply"，我当场就卡住了。
  # update-last.txt 用 PRE_MERGE_HEAD 当起点，专治这种情况。
  #   PRE_MERGE_HEAD == PATCH_BASE（本地没有多余提交）时两份内容一样，不重复生成。
  if [ "$PATCH_N" -gt 1 ] && [ -n "${PRE_MERGE_HEAD:-}" ] && [ "$PRE_MERGE_HEAD" != "$PATCH_BASE" ]; then
    if git format-patch "$PRE_MERGE_HEAD"..HEAD --binary --full-index --no-encode-email-headers --stdout > "patches/update-last.txt.tmp" 2>/dev/null \
       && [ -s "patches/update-last.txt.tmp" ]; then
      mv "patches/update-last.txt.tmp" "patches/update-last.txt" \
        || { echo "FAIL: 覆盖 patches/update-last.txt 失败"; exit 1; }
      echo "OK: 已写入 patches/update-last.txt（$((PATCH_N - $(git rev-list --count "$PATCH_BASE".."$PRE_MERGE_HEAD"))) 个提交）"
    else
      rm -f "patches/update-last.txt.tmp"
      echo "WARN: update-last.txt 生成失败，apply.txt 里会少一条分支"
    fi
  else
    rm -f patches/update-last.txt 2>/dev/null || true
    echo "NOTE: 本地没有多余的未推送提交，update-last.txt 与 update.txt 等价，不另生成"
  fi
fi
ls -lh patches/
```

**补丁范围是自动算的，不要人工判断「该包含哪几次」：**

- 远端已经推过的提交 → 在 `merge-base` 之前，**不会重复**进补丁；
- 远端还没推的（本次 + 之前失败的）→ 全在 `merge-base..HEAD` 里，**不会漏**；
- 所以先 `git fetch "$MY_REMOTE"` 再算 `merge-base`，这一顺序不能颠倒。

要求：
- 起点用 `git merge-base HEAD "$MY_REMOTE/$MY_BRANCH"`，确保**所有未推送的 commit**（本次同步 + 之前失败的）都进补丁；
  只导本次一个会把历史打断；
- 补丁是产物不是源码，**不要 `git add patches/`**；仓库若没忽略 `patches/`，写进 `.git/info/exclude`；
- 同时**用命令**生成 `patches/apply.txt`（不要手写，占位符漏替换用户就执行不了）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$PATCH_N" ] || { echo "FAIL: PATCH_N 未设置"; exit 1; }

if [ "$PATCH_N" -eq 0 ]; then
  # 没有补丁可导时，别留一份"教你 git am"的说明——那是给不存在的文件写的，会误导人。
  cat > patches/apply.txt <<EOF
本次没有生成补丁
================

远端 $MY_REMOTE/$MY_BRANCH 已经包含本地全部提交，没有需要带走的改动。
如果目录里有 update.txt.stale，那是上一轮的历史产物，不要拿去 git am。

检查时间 : $(date +%Y-%m-%dT%H:%M:%S%z)
EOF
else

# 第二份补丁的提示行：只有真的生成了 update-last.txt 才写这一条，没有就别编
PATCH_BASE_SHORT=$(git rev-parse --short "$PATCH_BASE" 2>/dev/null)
PRE_SYNC_SHORT=$(git rev-parse --short "${PRE_MERGE_HEAD:-HEAD}" 2>/dev/null)
if [ -f patches/update-last.txt ] && [ "$PRE_SYNC_SHORT" != "$PATCH_BASE_SHORT" ]; then
  LAST_HINT="  输出 $PRE_SYNC_SHORT  →  上次同步的提交已经在本地了，只应用最后一个
                     git am update-last.txt
"
  TWO_FILES="update.txt       完整补丁，从 $PATCH_BASE_SHORT 起，适合本地和远端一致的情况
update-last.txt  只有本次新增的提交，从 $PRE_SYNC_SHORT 起，适合本地已有旧提交的情况"
else
  LAST_HINT=""
  TWO_FILES="update.txt  完整补丁，从 $PATCH_BASE_SHORT 起（本次只有这一份）"
fi

cat > patches/apply.txt <<EOF
怎么用这个补丁
================

前提：电脑上装了 Git，用 Git Bash 打开（不要用 CMD 或 PowerShell，
      Windows 上这两个处理换行和引号容易和补丁打架），然后进入仓库目录。

第 0 步：先确认你现在在哪
------
    git rev-parse --short HEAD

把输出的 7 位字符对照下面几种情况，只跑对应的那一条，别乱套。

  输出 $PATCH_BASE_SHORT  →  本地和远端一致，用完整补丁
                     git am update.txt
$LAST_HINT
  其它              →  停下来告诉我，不要硬试

第 1 步：万一报 "patch does not apply"
------
先把半截状态清掉，再回第 0 步重看 HEAD：

    git am --abort

第 2 步：更新子模块（必须做，否则编译缺包）
------
项目里有子模块 vendors/EdgeTtsSharp。不更新会报 CS0246 找不到
Edge_tts_sharp / WebSocketSharp：

    git submodule update --init --recursive

如果卡在 SSH（提示 Permission denied / Host key verification failed），换成：

    git config --global url."https://github.com/".insteadOf "git@github.com:"
    git submodule update --init --recursive

第 3 步：编译验证
------
    dotnet build ClassIsland.Desktop/ClassIsland.Desktop.csproj -c Release

看到 "0 Error(s)" 就算成了。首次编译要还原依赖，慢几分钟正常。

第 4 步：推送
------
先确认哪个 remote 指向你自己的仓库（名字可能不叫 origin）：

    git remote -v

找到 URL 里含 NLCLI 的那个名字，假设叫 myrepo，然后：

    git push myrepo HEAD:$MY_BRANCH

（myrepo 只是示例名，用 URL 含 NLCLI 的那个；尖括号里的内容要换成真实分支名）

两份补丁的区别
------
$TWO_FILES

两份只用一个，不要都跑。

补丁里有什么
------------
共 $PATCH_N 次提交：
$(git log --pretty='  - %h %s' --no-decorate "$PATCH_BASE"..HEAD)

补丁信息
--------
上游目标 : ClassIsland/ClassIsland @ $UPSTREAM_BRANCH ($UPSTREAM_SHORT)
补丁起点 : $PATCH_BASE
提交数量 : $PATCH_N
生成时间 : $(date +%Y-%m-%dT%H:%M:%S%z)
EOF
fi

[ -s patches/apply.txt ] || { echo "FAIL: apply.txt 生成失败"; exit 1; }
grep -q '\$PATCH_N\|\$MY_BRANCH' patches/apply.txt && { echo "FAIL: 变量没展开"; exit 1; }
echo "OK: patches/apply.txt $(wc -l < patches/apply.txt) 行"
```
- **不要 `--force`、不要反复重试、不要换 remote 绕过认证**。

`patches/apply.txt` **生成出来长这样**（仅用于核对口气，**不要手抄**）：

> 真正的文件是上面那段 `cat > patches/apply.txt <<EOF` 生成的，包括「补丁里有什么」的提交列表
> 也是 `git log` 现算的。这里贴一遍只是让你知道它读起来是什么样——**两处不一致时，以命令生成的为准**，
> 不要按模板补写，更不要手抄提交标题（会和实际补丁对不上）。

```
怎么用这个补丁
================

前提：电脑上装了 Git，用 Git Bash 打开（不要用 CMD 或 PowerShell，
      Windows 上这两个处理换行和引号容易和补丁打架），然后进入仓库目录。

第 0 步：先确认你现在在哪
------
    git rev-parse --short HEAD

把输出的 7 位字符对照下面几种情况，只跑对应的那一条，别乱套。

  输出 <PATCH_BASE 短 SHA>  →  本地和远端一致，用完整补丁
                     git am update.txt
  输出 <PRE_MERGE_HEAD 短 SHA>  →  上次同步的提交已经在本地了，只应用最后一个
                     git am update-last.txt
  其它              →  停下来告诉我，不要硬试

第 1 步：万一报 "patch does not apply"
------
先把半截状态清掉，再回第 0 步重看 HEAD：

    git am --abort

第 2 步：更新子模块（必须做，否则编译缺包）
------
项目里有子模块 vendors/EdgeTtsSharp。不更新会报 CS0246 找不到
Edge_tts_sharp / WebSocketSharp：

    git submodule update --init --recursive

如果卡在 SSH（提示 Permission denied / Host key verification failed），换成：

    git config --global url."https://github.com/".insteadOf "git@github.com:"
    git submodule update --init --recursive

第 3 步：编译验证
------
    dotnet build ClassIsland.Desktop/ClassIsland.Desktop.csproj -c Release

看到 "0 Error(s)" 就算成了。首次编译要还原依赖，慢几分钟正常。

第 4 步：推送
------
先确认哪个 remote 指向你自己的仓库（名字可能不叫 origin）：

    git remote -v

找到 URL 里含 NLCLI 的那个名字，假设叫 myrepo，然后：

    git push myrepo HEAD:<MY_BRANCH>

（myrepo 只是示例名，用 URL 含 NLCLI 的那个；尖括号里的内容要换成真实分支名）

两份补丁的区别
------
update.txt       完整补丁，从 <PATCH_BASE 短 SHA> 起
update-last.txt  只有本次新增的提交，从 <PRE_MERGE_HEAD 短 SHA> 起

两份只用一个，不要都跑。

补丁里有什么
------------
共 <N> 次提交：
<逐条列出提交标题>

补丁信息
--------
上游目标 : ClassIsland/ClassIsland @ <UPSTREAM_BRANCH> (<短 SHA>)
补丁起点 : <BASE 短 SHA>
提交数量 : <N>
生成时间 : <ISO 8601>
```

降级完成后，汇报里必须写：
「**推送未执行（原因），已导出补丁 `patches/update.txt`，共 N 个提交**」。
绝对不许写成「已同步」或「已推送」。

### 12.2 网络失败后的代理探测与自动恢复

触发：`fetch` / `pull` / `push` 出现 DNS 超时、连接重置、`early EOF`、`RPC failed`、`GnuTLS` 等网络类错误。
目标：**优先复用本机已有的代理**，而不是直接失败。最多尝试 2 轮。

**① 先看有没有现成的代理配置**

```
env | grep -iE "http_proxy|https_proxy|all_proxy"
git config --global --get http.proxy
```

有就直接用，跳到第 3 步。

**② 探测常见本地代理端口**（Clash / v2rayN / Watt Toolkit 的默认端口）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }

# 超时别设太短：CONNECT 隧道 + TLS 握手常常超过 3 秒，真代理会被误判成不通。
# 也别设太长：10 次探测定时会把用户晾在那里。5 秒 × 10 次 ≈ 最坏 50 秒。
# 只认 200：302/404 直接排除。
# 端口只留最常见的 5 个：8888/8080 更常是开发服务器或杂项代理，误判成本高、还让探测时间翻倍。
echo "探测本地代理端口（5 个端口 × 2 种协议，最坏约 50 秒）…"
PROXY=""
for p in 7890 7897 1080 10808 10809; do
  code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' -x "http://127.0.0.1:$p" https://github.com 2>/dev/null)
  if [ "$code" = "200" ]; then echo "PROXY_OK http://127.0.0.1:$p"; PROXY="http://127.0.0.1:$p"; break; fi
  code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' -x "socks5h://127.0.0.1:$p" https://github.com 2>/dev/null)
  if [ "$code" = "200" ]; then echo "PROXY_OK socks5h://127.0.0.1:$p"; PROXY="socks5h://127.0.0.1:$p"; break; fi
done
[ -n "$PROXY" ] || { echo "NO_PROXY: 常见端口都没通"; }
# 必须落盘：下一条命令是新 shell，不写进去第 ③ 步拿到的是空值
echo "export PROXY='$PROXY'" >> /tmp/upstream-sync.env
```

Windows 额外看系统代理与进程：

```
netsh winhttp show proxy
# Git Bash 里写 /v 会被 MSYS 当成路径转成 V:\...，必须写 //v
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings" //v ProxyServer 2>/dev/null
# findstr 的开关同理：/i 会被转成 I:\...，写成 //i。保险起见直接用 grep -i。
# tasklist 在中文 Windows 下输出 GBK，但进程名本身是 ASCII，grep 只匹配 ASCII 部分，不受影响。
tasklist | grep -iE 'Steam\+\+|Watt|Clash|v2ray|Netch'
```

macOS 额外：

```
scutil --proxy
```

**③ 找到可用代理就用，但不写全局配置**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$PROXY" ] || { echo "FAIL: 没有可用代理（PROXY 为空），直接去第 ⑤ 步"; exit 1; }
echo "用代理 $PROXY 重试 fetch"
git -c http.proxy="$PROXY" -c https.proxy="$PROXY" fetch "$UPSTREAM_REMOTE" "+refs/heads/*:refs/remotes/$UPSTREAM_REMOTE/*" --prune \
  || { echo "FAIL: 走代理仍失败，去第 ⑤ 步试镜像"; exit 1; }
```

- **不要用 `<PORT>` 这种占位符**：端口是上一步探测出来的，必须经 `$PROXY` 传下来；
  上一条命令与新命令不是同一个 shell，没落盘的变量到这就是空的。
- 若第 ① 步是**环境变量或全局 config 里已有代理**，那本来就不需要传 `-c`，直接 `git fetch` 即可。

- 只用 `-c` 临时传参，**绝不 `git config --global http.proxy`**，避免污染我平时的配置；
- SOCKS 类型端口写成 `socks5h://127.0.0.1:<PORT>`；
- 借代理成功后，汇报里注明「经由本地代理 127.0.0.1:<PORT>」。

**④ 没有可用代理时，尝试启用加速工具**

- 先看进程是否在跑（`tasklist` / `ps aux`）；在跑就直接回第 2 步探端口；
- 未运行但能找到安装路径时可尝试启动（Windows：`%LOCALAPPDATA%\Steam++`、`C:\Program Files\Watt Toolkit`；
  macOS：`/Applications/Steam++.app`），启动后等 8–10 秒再探端口；
- **如果启动需要 UAC 提权、会弹窗、或等完端口仍不通，不要反复尝试**，直接告诉我：
  「检测到 Watt Toolkit / Clash 已安装但未能自动启用，请手动打开其『GitHub 加速』，我随后重试」。

**⑤ 仍不通 → 临时走镜像（只读）**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件 /tmp/upstream-sync.env 缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
git -c url."https://ghfast.top/https://github.com/".insteadOf="https://github.com/" \
    fetch "$UPSTREAM_REMOTE" "+refs/heads/*:refs/remotes/$UPSTREAM_REMOTE/*" --prune \
  || { echo "FAIL: 镜像也不通，进 12.3 逐文件方案"; exit 1; }
```

- 镜像**只用于读取**（fetch / clone）。**推送绝不要走镜像**——认证与落库行为都不可控；
- 已实测可用：`ghfast.top`（整仓 git）、`cdn.jsdelivr.net`（单文件）。
- 若镜像也失败，或只能逐文件下载 → 进入 **12.3**。

**12.2 的硬性约束**：
- 代理只对单条命令生效，不许写进 `.gitconfig`，也不许拼进 remote URL；
- 不许把带 token 的地址写进任何会被提交的文件；
- 2 轮仍失败就停下报告，把试过的端口和错误原文列出来。

### 12.3 jsDelivr 单文件下载（最后手段，只读快照）

**什么时候用**：git 协议（clone / fetch / push）完全不通，但 HTTPS 单文件请求还能走通。
这是**只读快照**方案，拿不到 `.git` 历史，因此后面的合并、提交、推送都会受影响。

**一、正确用法**

1）先拿到 commit SHA（**分支名带斜杠时不能直接用**）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${UPSTREAM_BRANCH:?FAIL: UPSTREAM_BRANCH 为空，回第 0 步}"

# 分支名里的斜杠要转义成 %2F，直接拼会被当成两个路径段
REF_ENC=$(printf '%s' "$UPSTREAM_BRANCH" | sed 's|/|%2F|g')

# 要同步的是官方仓库，所以取 ClassIsland 的 SHA，不是自己 fork 的
SHA=$(curl -sf --http1.1 --max-time 60 \
      "https://api.github.com/repos/ClassIsland/ClassIsland/commits/$REF_ENC" \
      | grep -m1 '"sha"' | cut -d'"' -f4)
echo "SHA=$SHA"
# 校验必须有：取不到时后面拼出来的是 ...@/path，全是 404，
# 而 curl 对 404 也返回 0，会被误当成"文件不存在"而非"取 SHA 失败"
echo "$SHA" | grep -Eq '^[0-9a-f]{40}$' \
  || { echo "FAIL: 没取到合法 SHA（限流/断网/分支已改名），别硬拼 URL"; exit 1; }

# ★ 必须落盘：下面的下载块是另一个 shell，不写进去 $SHA 就是空的
echo "export SHA=$SHA" >> /tmp/upstream-sync.env

# 交叉核对（可选）：git 协议还通时，用 ls-remote 看官方分支真实指向，
# 跟上面的 SHA 对一下。不一致说明 API 缓存滞后，以 ls-remote 为准并重新落盘。
git ls-remote https://github.com/ClassIsland/ClassIsland.git "refs/heads/$UPSTREAM_BRANCH" || true
```

2）拼 URL 下载单个文件：

```
https://cdn.jsdelivr.net/gh/<owner>/<repo>@<COMMIT_SHA>/<path>
```

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失"; exit 1; }
: "${SHA:?FAIL: SHA 为空，先执行上一步}"
# ⚠️ 一律下载到 /tmp 快照目录，绝对不要 -o 到仓库里的同名文件：
# 用相对路径 -o fixxed.txt 会把本地真实的记录文件直接覆盖成 CDN 上的旧版本。
mkdir -p /tmp/upstream-snapshot
curl -fsSL --http1.1 --max-time 60 --retry 3 \
  "https://cdn.jsdelivr.net/gh/ClassIsland/ClassIsland@$SHA/fixxed.txt" \
  -o /tmp/upstream-snapshot/fixxed.txt \
  || { echo "FAIL: 下载失败（404 = 路径不对或 SHA 错；403 = 单文件超限）"; exit 1; }
```
- `-f`（`--fail`）不能省：不加的话 404 的 HTML 正文照样被写进文件，curl 还返回 0，
  你会得到一个内容为 `Couldn't find the requested file` 的"成功"文件。

3）批量下载：

```
curl -fsS --http1.1 --max-time 60 \
  "https://api.github.com/repos/<owner>/<repo>/git/trees/<SHA>?recursive=1" -o /tmp/upstream-tree.json \
  || { echo "FAIL: 取 tree 失败（403 是限流，404 是分支/SHA 不存在）——不要当成空仓库"; exit 1; }
```

然后用 Python 并发下载（要点，不必贴完整脚本）：
- 并发 8–16，每请求 `timeout=60`；
- 失败重试 3 次，间隔 1.5s / 3s / 4.5s；
- 路径用 `urllib.parse.quote()` 编码；
- 下载完用 tree 里的 `size` 校验，大小不符视为失败；
- 失败率超过 20% 立即停止，不要凑合。

**二、已知限制（会咬人的地方）**

| 限制 | 表现 | 应对 |
|---|---|---|
| 分支名不能带斜杠 | `@develop/v2/misha-alpha` 被解析成 ref=`develop`、路径=`v2/...` → 404 | **必须用 commit SHA** |
| 缓存滞后 | 用分支名 / tag 时可能拿到 12 小时前的旧版本 | 用 SHA（不可变，永久缓存） |
| 单文件大小上限 | 超过约 20 MB 返回 403 / 413 | 改用 `raw.githubusercontent.com` 或 `ghfast.top` 拉该文件 |
| 私有仓库不支持 | 一律 404 | 必须让用户提供访问方式 |
| 没有 git 历史 | 算不出 merge-base，也无法 commit / push | 只能产快照 + 补丁 |
| 请求量大被限流 | 429 或间歇性失败 | 降并发、加退避 |
| 文件名含特殊字符 | 404 | URL 编码 |

**三、什么时候必须找用户（不要自己硬扛）**

| 情况 | 为什么必须问 |
|---|---|
| 仓库是私有的 | jsDelivr 拿不到，任何镜像都不行 |
| 需要 merge-base 但没有历史 | 基线不可信，全量合并可能冲掉定制 |
| 单文件 > 20 MB，或连续 403 | 换方案也未必可行，需要你定 |
| 批量下载失败率 > 20% | 快照不完整，继续做只会污染工作区 |
| 需要 push | 快照目录本身没有 `.git`。可以 `git init` + commit 后推上去，但**丢失逐提交历史**，且算不出 merge-base（无法产出可 `git am` 的补丁） |
| 你明确要求保留提交历史 | 快照方案做不到 |

**四、找用户时的说法（必须这样讲，不许含糊）**

模板（下面这段是话术，照写，不要当命令执行）：

```text
【需要你决定】<一句话说清卡在哪>

我已尝试：<2-3 种方案 + 错误原文>

继续下去的影响：<具体后果>

请选一个（回复编号即可）：
A. <方案一 + 需要你做什么>（推荐）
B. <方案二>
C. <方案三>
```

**反例（禁止这样讲）**：
- ❌「网络好像有点问题，可能要继续，你看呢？」
- ❌「下载失败了，怎么办？」
- ❌「要不要换个方式试试？」

**正例**（同样是话术，不是命令）：

```text
【需要你决定】git clone 连续失败 3 次，错误：GnuTLS recv error (-110): The TLS connection was non-properly terminated.

我已尝试：
1. 直连 clone（3 次，均在 90 秒左右断开）
2. ghfast.top 镜像 clone（同样断开）
3. jsDelivr 单文件下载（可用，但只能拿快照）

继续下去的影响：没有 .git 历史，算不出 merge-base，只能按"全量覆盖"处理，
可能冲掉你 fixxed.txt 里记录的 6 项定制（水印 / 更新页 / 关于页 / 开源提示 / 隐私勾选 / 贡献者）。

请选一个（回复编号即可）：
A. 你手动打开 Watt Toolkit 的「GitHub 加速」，回复"好了"，我重试 clone（推荐，能保留完整历史）
B. 我用 jsDelivr 拉快照继续，最后给你一个补丁包，你在自己机器上 git am 后推送
C. 你在本地把仓库打成 zip 放到某个路径，告诉我路径，我直接用
```

（上面这一段是话术示例，照这个结构写，不要当命令执行。）

这条规则的要点：**说清卡在哪、试过什么、继续做的后果、以及需要你做的具体动作**。
不要只说「有问题」，也不要把选择权模糊地丢回来。

其余规则：
- 目标必须是第 0 步识别出来的 `MY_REMOTE`（URL 含 NLCLI 的那个）。**绝对不许推到官方 remote。**
- 只能用普通推送，**绝对不许 `--force` / `--force-with-lease`**。
- 推送被拒绝（非快进）时，**不要强行处理**，把远端状态告诉我，问我要不要先 `git pull --rebase "$MY_REMOTE" "$MY_BRANCH"`。
- **推送成功会触发 NLCLI 的 `build_release.yml`**，GitHub 自动编译打包。这是预期行为，**不要加 `[skip ci]`**。
  汇报里只在**确实推送成功**时写「已触发 GitHub Actions 构建」；走补丁降级（没真推）时写「未触发（本次未推送）」。
  不确定有没有触发就写「已推送，未确认 CI 状态」，不要编。
- **不要主动去查 CI 状态**（不调 GitHub API、不轮询 `gh run list`）：这一轮不负责等 CI。
  推送命令的原始输出里带了 Actions 链接就照抄「已触发」，没带就写「已推送，未确认 CI 状态」。
  等 CI 结果是下一轮或用户自己的事。
- `PUSH_EXIT = 0` 但 `ls-remote` 核对不一致 → 仍视为失败，如实报告，不许说「已同步」。

## 13. 第 11 步：写记录文件（三个文件，缺一不可；**必须在第 9 步提交之前完成**）

> **铁律 3 回扣**：`out.txt` 与 `$FIXLOG` 的新内容都写在**文件上方**，不是末尾。
> 写完用 `head` 检查（不是 `tail`），确认新段落在最上面、旧内容紧随其后且完好。

### 13.1 `action.txt` —— 同步进度锚点（用于下次同步）

先把两个统计量算出来（**必须现算，不要凭印象填**）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${PRE_MERGE_HEAD:?FAIL: 锚点丢失，回第 6 步}"
cd "$REPO_ROOT" || exit 1

# 三个来源都要算：
#   ① 工作区 vs 锚点（未暂存的改动）
#   ② 索引 vs 锚点（AI 可能已经 git add 过）
#   ③ 未跟踪的新文件（第 7 步新拷进来的上游新文件）
# 少算 ② 或 ③ 的话，UPDATED_FILES 会偏小甚至为 0，而这个值会写进 action.txt 长期留存。
{
  git -c core.quotePath=false diff --no-renames --name-only "$PRE_MERGE_HEAD" || exit 1
  git -c core.quotePath=false diff --no-renames --cached --name-only "$PRE_MERGE_HEAD" || exit 1
  git ls-files --others --exclude-standard || exit 1
} > /tmp/updated-raw.txt || { echo "FAIL: 统计改动文件失败，停下问我"; exit 1; }

# 记录文件名里的 . 要转义，否则 fixxedXtxt 这类名字也会被误排除（见 13.3 同款注释）
FIXLOG_RE=$(printf '%s' "${FIXLOG:-fixxed.txt}" | sed 's/\./\\./g')
UPDATED_FILES=$(LC_ALL=C sort -u /tmp/updated-raw.txt \
  | grep -vE "^(action\.txt|out\.txt|${FIXLOG_RE}|patches/.*)$" \
  | wc -l | tr -d ' ')

# CONFLICT_FILES 记的是"本次发生过冲突的文件数"（8.4 的 before 清单），
# 不是"修完之后还剩多少"（7.1 会用重扫结果覆盖 conflict-files.txt，成功时恒为 0）。
# 用 -f 判存在，不要写成 `$(wc -l < 文件 | tr -d ' ' || echo 0)`：
# `<` 打不开时 wc 根本不执行，但它在管道左侧，整条管道的退出码由右侧 tr 决定（0），
# || echo 0 永远不触发，捕获到的是空串 → 下面 case 报"冲突计数非法 []"。
CONFLICT_FILES=0
[ -f /tmp/conflict-files.before.txt ] && CONFLICT_FILES=$(wc -l < /tmp/conflict-files.before.txt | tr -d ' ')
case "$CONFLICT_FILES" in ''|*[!0-9]*) echo "FAIL: 冲突计数非法 [$CONFLICT_FILES]"; exit 1 ;; esac
case "$UPDATED_FILES"  in ''|*[!0-9]*) echo "FAIL: 文件计数非法 [$UPDATED_FILES]";  exit 1 ;; esac

echo "UPDATED_FILES=$UPDATED_FILES  CONFLICT_FILES=$CONFLICT_FILES"
if [ "$UPDATED_FILES" -eq 0 ]; then
  echo "FAIL: 本次改动文件数为 0，说明前面没真正改到东西，停下问我"
  exit 1
fi
{
  echo "export UPDATED_FILES=$UPDATED_FILES"
  echo "export CONFLICT_FILES=$CONFLICT_FILES"
} >> /tmp/upstream-sync.env
```

口径写明：`UPDATED_FILES` **不含** `action.txt`/`out.txt`/`$FIXLOG` 这三个记录文件，否则每轮都会多出 3 个。

**用下面的命令整文件覆写**（不要靠 AI 手写，字段多、容易漏；`heredoc` 不加引号，变量会展开）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1

[ -n "$UPSTREAM_HEAD" ] || { echo "FAIL: UPSTREAM_HEAD 为空，回第 3 步"; exit 1; }
[ -n "$BASE" ]          || { echo "FAIL: BASE 为空，回第 4 步"; exit 1; }
[ -n "$SYNCED_FROM_SHA" ] || { echo "FAIL: SYNCED_FROM_SHA 为空，回第 1 步"; exit 1; }
[ -n "$BUILD_RESULT" ]  || { echo "FAIL: BUILD_RESULT 为空，第 8 步没落盘"; exit 1; }
# MY_REMOTE / MY_BRANCH 也要断言：少一个就会写出 "SYNCED_FROM = my/ @ abc1234"
# 这种看着像正常、实际缺了分支名的值，而且不会有任何报错。
[ -n "$MY_REMOTE" ] && [ -n "$MY_BRANCH" ] \
  || { echo "FAIL: MY_REMOTE / MY_BRANCH 为空，回第 0 步"; exit 1; }

cat > action.txt <<EOF
# 上游同步进度记录
UPSTREAM_REPO   = https://github.com/ClassIsland/ClassIsland.git
UPSTREAM_BRANCH = $UPSTREAM_BRANCH
BRANCH_FALLBACK = ${BRANCH_FALLBACK:-0}
LAST_SYNCED_SHA   = $UPSTREAM_HEAD
LAST_SYNCED_SHORT = $UPSTREAM_SHORT
LAST_SYNCED_AT    = $(date +%Y-%m-%dT%H:%M:%S%z)
BASE_SHA          = $(git rev-parse --short "$BASE")
SYNCED_FROM       = $MY_REMOTE/$MY_BRANCH @ $SYNCED_FROM_SHA
MY_REMOTE_URL     = $MY_REMOTE_URL
LOCAL_BRANCH      = $LOCAL_BRANCH
UPDATED_FILES     = $UPDATED_FILES
CONFLICT_FILES    = $CONFLICT_FILES
BUILD_RESULT      = $BUILD_RESULT
EOF

# AI 在 Windows 上写文件可能带 CRLF。不转的话，下次 source 时
# `LAST_SYNCED_AT = ...\r` 的值会带一个 \r，比对永远不等，而且看不出来。
tr -d '\r' < action.txt > action.txt.lf && mv action.txt.lf action.txt \
  || { echo "FAIL: action.txt 转 LF 失败（文件被占用？关掉编辑器重试）"; exit 1; }

cat action.txt
```

字段含义（照这个自查，别写反）：

```
# 上游同步进度记录
UPSTREAM_REPO   = https://github.com/ClassIsland/ClassIsland.git
UPSTREAM_BRANCH = <本次实际使用的分支：develop/v2/misha-alpha 或回退后的 master>
BRANCH_FALLBACK = <0 正常 / 1 已回退到 master>
LAST_SYNCED_SHA   = <本次同步到的官方 commit 完整 SHA>
LAST_SYNCED_SHORT = <前 7 位>
LAST_SYNCED_AT    = <同步完成时间，ISO 8601 带时区>
BASE_SHA          = <本次使用的合并基线短 SHA>
SYNCED_FROM       = <MY_REMOTE 的实际名字>/<MY_BRANCH> @ <对齐后的短 SHA>
MY_REMOTE_URL     = <第 0 步识别出的我仓库 URL>
LOCAL_BRANCH      = <本地当前分支名>
UPDATED_FILES     = <本次同步的文件总数>
CONFLICT_FILES    = <冲突文件数量>
BUILD_RESULT      = <构建结果：通过 / 失败 / 环境无 SDK 未执行>
```

**写完必须回读校验（第二道）：**

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${UPSTREAM_HEAD:?FAIL: UPSTREAM_HEAD 为空，回第 3 步}"
cd "$REPO_ROOT" || exit 1

[ -f action.txt ] || { echo "FAIL: action.txt 不存在"; exit 1; }

# 真比较，不是打印出来让人肉眼看——40 位 SHA 肉眼比不出差异
get_field() { sed -n "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*//p" action.txt | head -1 | tr -d ' \r'; }

GOT=$(get_field LAST_SYNCED_SHA)
[ "$GOT" = "$UPSTREAM_HEAD" ] \
  || { echo "FAIL: LAST_SYNCED_SHA 不一致"; echo "  文件里: [$GOT]"; echo "  应该是: [$UPSTREAM_HEAD]"; exit 1; }

[ -n "$(get_field UPSTREAM_BRANCH)" ] || { echo "FAIL: UPSTREAM_BRANCH 为空"; exit 1; }
[ -n "$(get_field BUILD_RESULT)" ]    || { echo "FAIL: BUILD_RESULT 为空"; exit 1; }
[ -n "$(get_field UPDATED_FILES)" ]   || { echo "FAIL: UPDATED_FILES 为空"; exit 1; }
grep -q $'\r' action.txt && { echo "FAIL: action.txt 里还有 \r"; exit 1; }
echo "ACTION_OK: LAST_SYNCED_SHA=$GOT"
```

- `action.txt` 必须存在且已转 LF；
- `LAST_SYNCED_SHA` 必须与 `$UPSTREAM_HEAD` **逐字符相等**（脚本会真比对，不靠肉眼）；
- 其它必填字段（`UPSTREAM_BRANCH` / `BUILD_RESULT` / `UPDATED_FILES`）不能为空；
- 任何一项不过就重写，写完再验一次，仍不过则停下来告诉我，不许带着错值提交。
- **`out.txt` 和 `$FIXLOG` 在这一步还不需要检查**——它们分别在 13.2 / 13.3 才写。

### 13.2 `out.txt` —— 历次同步的 Action 记录（**新记录插在最上面，旧记录往下压**）

名称取 `.github/workflows/*.yml` 里 `name:` 字段的值；没有该字段就用文件名。

**写入方式：前插（prepend），不是追加。** 每次同步的新段落插在表头之后、**旧记录之前**；
旧记录整体下移，**内容一个字都不许改、不许重排、不许删除**。

首次创建时的表头（表头永远在最顶部，不要动它）：

```
# ClassIsland 上游同步 · Action 变更记录
（最新记录在最上方，越往下越旧）
```

**插入位置示意：**

```
# ClassIsland 上游同步 · Action 变更记录
（最新记录在最上方，越往下越旧）

================================================================
## 2026-10-17 21:00:00  同步 fd83102      ← 这次新的插在这里
================================================================
================================================================
## 2026-09-20 10:00:00  同步 a8af81ba      ← 上次的，被压到下面
================================================================
================================================================
## 2026-08-30 09:00:00  同步 045901ad      ← 更早的，继续往下
================================================================
```

**前插的写法（表头是前 2 行）：**

**第一步：先把本次段落写成文件**（不要用 shell 变量传整段文本——跨 shell 会丢，
`printf "$NEW_SECTION"` 输出两个空行照样会被当成"写成功"）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${UPSTREAM_SHORT:?FAIL: UPSTREAM_SHORT 为空}" \
  "${BASE:?FAIL: BASE 为空}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空}" "${LOCAL_BRANCH:?FAIL: LOCAL_BRANCH 为空}"
cd "$REPO_ROOT" || exit 1

# 非空 ≠ 能解析。下面 `git diff "$BASE" "$UPSTREAM_REF"` 是带 `|| :` 兜底的，
# 解析失败时它会把 wf-status.txt 清空 → out.txt 写成"新增/修改/删除 全部(无)"，
# 看起来像"本次没动 workflow"，实际是一个都没查。必须提前拦住。
git rev-parse --verify --quiet "$BASE^{commit}" >/dev/null \
  || { echo "FAIL: BASE 解析不到（$BASE），回第 4 步"; exit 1; }
git rev-parse --verify --quiet "$UPSTREAM_REF^{commit}" >/dev/null \
  || { echo "FAIL: UPSTREAM_REF 解析不到（$UPSTREAM_REF），回第 3 步"; exit 1; }

OUT_WF=".github/workflows"

# ---- ① 工作流名称：优先 name: 字段，回退文件名 ----
wf_name() {  # $1=ref  $2=path
  # name: 后面可能带引号（name: "Build"）或行内注释（name: Build # 临时），
  # 原样塞进 out.txt 会变成 build_release.yml ("Build") 这种脏格式，先剥干净。
  # 只认「空白 + #」才是注释：name: a#b 里的 # 属于值本身，不能一起切掉。
  _n=$(git show "$1:$2" 2>/dev/null | sed -n 's/^name:[[:space:]]*//p' | head -1 \
       | sed -e 's/[[:space:]]#.*$//' -e 's/[[:space:]]*$//')
  _n=${_n%\"}; _n=${_n#\"}
  _n=${_n%\'}; _n=${_n#\'}
  [ -n "$_n" ] || _n=$(basename "$2")
  printf '%s' "$_n"
}

# 这里不能写 `2>/dev/null || : > 文件`：diff 一失败（BASE/UPSTREAM_REF 无效）就被吞成
# "没有 workflow 变更"，out.txt 的「新增/修改/删除」全写 (无)，看不出任何异常。
git -c core.quotePath=false diff --no-renames --name-status \
  "$BASE" "$UPSTREAM_REF" -- "$OUT_WF" > /tmp/wf-status.txt 2>/tmp/wf-status.err \
  || { echo "FAIL: 取 workflow 变更失败（BASE/UPSTREAM_REF 无效？）"; cat /tmp/wf-status.err; exit 1; }

: > /tmp/wf-block.txt
for pair in "新增:A" "修改:M" "删除:D"; do
  label=${pair%%:*}; code=${pair##*:}
  echo "${label}:" >> /tmp/wf-block.txt
  cnt=0
  while IFS=$'\t' read -r st f; do
    [ "$st" = "$code" ] || continue
    if [ "$code" = "D" ]; then _r="$BASE"; else _r="$UPSTREAM_REF"; fi
    echo "  - $(basename "$f") ($(wf_name "$_r" "$f"))" >> /tmp/wf-block.txt
    cnt=$((cnt+1))
  done < /tmp/wf-status.txt
  [ "$cnt" -eq 0 ] && echo "  - (无)" >> /tmp/wf-block.txt
done

echo "未变更:" >> /tmp/wf-block.txt
# 同上：先落到 raw 再过滤，别让 ls-tree 的失败被 grep 的退出码掩盖
git -c core.quotePath=false ls-tree -r --name-only "$UPSTREAM_REF" -- "$OUT_WF" > /tmp/wf-all.raw 2>/tmp/wf-all.err \
  || { echo "FAIL: 列 workflow 文件失败（UPSTREAM_REF 无效？）"; cat /tmp/wf-all.err; exit 1; }
grep -E '\.ya?ml$' /tmp/wf-all.raw > /tmp/wf-all.txt || true
awk -F'\t' '{print $2}' /tmp/wf-status.txt | LC_ALL=C sort -u > /tmp/wf-changed.txt
LC_ALL=C comm -23 <(LC_ALL=C sort /tmp/wf-all.txt) /tmp/wf-changed.txt > /tmp/wf-same.txt
cnt=0
while IFS= read -r f; do
  [ -n "$f" ] || continue
  echo "  - $(basename "$f")" >> /tmp/wf-block.txt
  cnt=$((cnt+1))
done < /tmp/wf-same.txt
[ "$cnt" -eq 0 ] && echo "  - (无)" >> /tmp/wf-block.txt

# ---- ② 提交列表：每条上游提交后面附「改动文件数」 ----
#    手抄的话，提交标题里的反引号、$() 会在 heredoc 里被二次展开（C# 仓库标题里
#    带 `ProfileService` 这种很常见），轻则缺字，重则真的执行一段命令。
#    文件数按「相对第一父提交」算；merge commit 也能算出一个有意义的值。
git rev-list "$BASE".."$UPSTREAM_REF" > /tmp/commit-shas.txt \
  || { echo "FAIL: 取提交列表失败"; exit 1; }
: > /tmp/commit-list.txt
while IFS= read -r sha; do
  [ -n "$sha" ] || continue
  subj=$(git log -1 --format=%s "$sha") \
    || { echo "FAIL: 读不到提交 $sha 的标题"; exit 1; }
  if git rev-parse --verify --quiet "$sha^1" >/dev/null 2>&1; then
    nf=$(git -c core.quotePath=false diff --no-renames --name-only "$sha^1" "$sha" | wc -l | tr -d ' ')
  else
    nf=$(git -c core.quotePath=false show --format= --name-only --root "$sha" | sed '/^$/d' | wc -l | tr -d ' ')
  fi
  printf '  - %s %s（%s 个文件）\n' "$(git rev-parse --short "$sha")" "$subj" "$nf" >> /tmp/commit-list.txt
done < /tmp/commit-shas.txt
CN=$(wc -l < /tmp/commit-list.txt | tr -d ' ')

# 冲突修正统计：发生过多少、实际修正多少、残留多少（7.1 之后残留应为 0）
# 与 13.1 的 CONFLICT_FILES 同一套写法：先 -f 判存在，再 wc。
# 不要写 `cat 文件 | wc -l`：cat 打不开时 wc 照样输出 0，把
# "文件缺失 / 没权限" 伪装成 "没有冲突"——而下面正是拿 0 去判「冲突修正 : 无」。
CF_BEFORE=0
if [ -f /tmp/conflict-files.before.txt ]; then
  CF_BEFORE=$(wc -l < /tmp/conflict-files.before.txt | tr -d ' ')
fi
CF_AFTER=0
if [ -f /tmp/conflict-files.txt ]; then
  CF_AFTER=$(wc -l < /tmp/conflict-files.txt | tr -d ' ')
fi
case "$CF_BEFORE" in ''|*[!0-9]*) echo "FAIL: 冲突计数非法 [$CF_BEFORE]"; exit 1 ;; esac
case "$CF_AFTER"  in ''|*[!0-9]*) echo "FAIL: 残留计数非法 [$CF_AFTER]";  exit 1 ;; esac
if [ "$CF_BEFORE" -eq 0 ]; then
  CF_LINE="冲突修正 : 无"
else
  CF_LINE="冲突修正 : 发生过 $CF_BEFORE 个文件，已修正 $((CF_BEFORE - CF_AFTER)) 个，残留 $CF_AFTER 个"
fi

cat > /tmp/new-out.txt <<BLOCK
================================================================
## $(date '+%Y-%m-%d %H:%M:%S')  同步 $UPSTREAM_SHORT
上游目标 : ClassIsland/ClassIsland @ $UPSTREAM_BRANCH ($UPSTREAM_SHORT)
本次基线 : $(git rev-parse --short "$BASE")
本地分支 : $LOCAL_BRANCH  →  本地提交见本次 commit

$(cat /tmp/wf-block.txt)

本次同步的上游提交（$CN 个，全部列出，每条附改动文件数）:
$(cat /tmp/commit-list.txt)
$CF_LINE
================================================================
BLOCK

# ---- ③ 校验：空段落、占位符残留、提交条数对不上，一律不许往下走 ----
[ -s /tmp/new-out.txt ] || { echo "FAIL: 新段落为空"; exit 1; }
grep -q '^## ' /tmp/new-out.txt || { echo "FAIL: 段落缺少 ## 标题行"; exit 1; }

# 占位符统一按尖括号检：模板里出现过 <或写 (无)>、<短SHA> 等各种写法，
# 只查 '<填：' 是查不到的。
# ★ 不能简单写成 '<[^>]{1,40}>'：提交标题里出现 List<T> / Dictionary<string, object>
#   这类泛型是常事，那样会把正常内容当成占位符，直接卡死整轮。
#   这里只认两种"像占位符"的：尖括号里含非 ASCII（中文），或全大写标识符（TARGET_PROJ 这种）。
# ★ LC_ALL=C 不能省：UTF-8 locale 下 grep 会把 " -~" 这个范围当成三个字面字符 {空格,-,~}，
#   于是 [^ -~] 几乎匹配任何字母 → 满屏误报（连 List<T> 都命中）。C locale 下才是按字节判断。
if tr -d '\r' < /tmp/new-out.txt | LC_ALL=C grep -nE '<[^>]*[^ -~][^>]*>|<[A-Z][A-Z0-9_]{2,}>'; then
  echo "FAIL: 上面这些占位符没替换干净，改完再跑这一步"; exit 1
fi

GOT_CN=$(grep -c '^  - [0-9a-f]\{7,\} ' /tmp/new-out.txt || true)
[ "$GOT_CN" -eq "$CN" ] \
  || { echo "FAIL: 提交列表 $GOT_CN 条 != 实际 $CN 条"; exit 1; }

echo "OK: 新段落 $(wc -l < /tmp/new-out.txt) 行，提交 $CN 条"
```

**第二步：插入（表头 2 行不动，新段落插在第 3 行位置）**：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${UPSTREAM_SHORT:?FAIL: UPSTREAM_SHORT 为空}"
cd "$REPO_ROOT" || exit 1
[ -s /tmp/new-out.txt ] || { echo "FAIL: 新段落不存在，先做上一步"; exit 1; }
grep -q -- "$UPSTREAM_SHORT" /tmp/new-out.txt \
  || { echo "FAIL: 新段落里没有本次短 SHA，可能是上一轮的残留文件"; exit 1; }

# 末尾必须有换行，否则 cat 时新段最后一行会和旧文件第 3 行粘成一行，
# 行数守恒校验会失败，报的却是"旧记录被吞"——排查方向直接跑偏。
if [ -n "$(tail -c 1 /tmp/new-out.txt)" ]; then printf '\n' >> /tmp/new-out.txt; fi

# 只转新段落的 CR，旧 out.txt 逐字节保留：
# 旧文件若本来就是 CRLF，对整份文件 tr -d '\r' 会把旧记录每一行都改掉，
# 违反"旧内容一个字都不许改"。FIXLOG 那边也是只转新段。
tr -d '\r' < /tmp/new-out.txt > /tmp/new-out.lf \
  || { echo "FAIL: 新段落转 LF 失败"; exit 1; }
NEW_N=$(wc -l < /tmp/new-out.lf | tr -d ' ')

if [ -f out.txt ]; then
  # 表头必须是那两行。只查首行不够：如果文件是旧版（只有 1 行表头），
  # head -2 + tail -n +3 会把第 2 行的旧记录挤掉，历史就少一条。
  head -1 out.txt | grep -q '^# ClassIsland 上游同步' \
    || { echo "FAIL: out.txt 首行不是预期表头，停下问我"; exit 1; }
  sed -n '2p' out.txt | grep -q '最新记录在最上方' \
    || { echo "FAIL: out.txt 第 2 行不是说明行（可能是旧版格式），停下问我"; head -3 out.txt; exit 1; }

  cp out.txt out.txt.bak || { echo "FAIL: 备份 out.txt 失败"; exit 1; }
  OLD_N=$(wc -l < out.txt | tr -d ' ')

  { head -2 out.txt; echo; cat /tmp/new-out.lf; tail -n +3 out.txt; } > out.txt.tmp \
    || { echo "FAIL: 拼接失败"; rm -f out.txt.tmp; exit 1; }
  [ -s out.txt.tmp ] || { echo "FAIL: 拼接结果为空，旧记录丢了"; exit 1; }

  # 行数守恒：旧 + 新 + 1（中间那个空行）。少了就是旧记录被吞
  EXP=$((OLD_N + NEW_N + 1))
  GOT=$(wc -l < out.txt.tmp | tr -d ' ')
  [ "$GOT" -eq "$EXP" ] \
    || { echo "FAIL: 行数 $GOT != 预期 $EXP（旧 $OLD_N + 新 $NEW_N + 1）"; rm -f out.txt.tmp; exit 1; }

  # 新段落已在上一步转成 LF；这里不再对整份文件做 tr，旧内容保持原字节
  mv out.txt.tmp out.txt || { echo "FAIL: 覆盖 out.txt 失败（文件被占用？）"; exit 1; }
else
  # 首次：表头用两个参数要写两条格式串，否则 printf 会把格式串复用两遍变成 4 行
  printf '%s\n%s\n\n' '# ClassIsland 上游同步 · Action 变更记录' '（最新记录在最上方，越往下越旧）' > out.txt
  cat /tmp/new-out.lf >> out.txt
fi

# 结构断言：新段落必须紧跟在表头下方（表头 2 行 + 1 空行，第 4 行应是 ====）
head -4 out.txt | tail -1 | grep -q '^====' \
  || { echo "FAIL: 新段没插在表头下方（可能是追加到了末尾）"; head -8 out.txt; exit 1; }
echo "OUT_OK: 总行数 $(wc -l < out.txt)，新段 $(echo "$NEW_N") 行在最上方"
```

- 文件不存在时：先写表头 + 新段落；
- 文件存在时：前 2 行表头保持不动，新段落插在第 3 行位置，旧内容全部往后推。
- **不要用 `>>` 追加到末尾**：那会把新记录压到文件最后，旧记录反而跑到上面。

每次同步**插到最上方**的段落（照此格式，**标题、字段名、缩进都保持一致**）：

```
================================================================
## <YYYY-MM-DD HH:mm:ss>  同步 <短SHA>
上游目标 : ClassIsland/ClassIsland @ <UPSTREAM_BRANCH> (<短 SHA>)
本次基线 : <BASE 短SHA>
本地分支 : <LOCAL_BRANCH>  →  本地提交见本次 commit

新增:
  - (无)
修改:
  - build_release.yml (Build)
删除:
  - (无)
未变更:
  - close_stale_issues.yml (Close inactive issues)
  - issues_similarity_analysis.yml (Issues Similarity Analysis)
  - qodana_code_quality.yml (Qodana)

本次同步的上游提交（<N> 个，全部列出，每条附改动文件数）:
  - fd83102d feat(ui): 支持通过滑动消除界面 toast（1 个文件）
  - 9650fb0c feat(profile): 日程课表调课功能（3 个文件）
  - d8e5f012 feat(ui/profile-editor): 优化档案编辑器在日程模式和经典模式下的行为（2 个文件）
  - ……（本次共有多少个就继续列多少行，一条不省）
冲突修正 : 无
================================================================
```

格式要点（这几条决定文件好不好读）：
- `新增 / 修改 / 删除 / 未变更` 四节**固定顺序**，每节下的条目**一行一个**，不要用逗号挤在同一行；
- 名称统一写成 `文件名 (workflow 的 name 字段)`；`name` 缺失时只写文件名；
- 某节为空写 `(无)`，但**不要删掉这一节**；
- 提交列表标题里写明总条数，正文逐条列全，不写「其余 N 个见 git log」。
- 每条上游提交后面必须写 `（N 个文件）`，N 是该提交相对第一父提交改动的文件数（rename 按删除+新增计，和全流程 `--no-renames` 口径一致）。
- 段末必须有 `冲突修正 : …` 一行：无冲突写 `无`；有冲突写 `发生过 N 个文件，已修正 M 个，残留 K 个`。
- `本地提交` 一栏写「见本次 commit」——因为本文件在提交之前生成，此时还没有 commit SHA。

若本次完全没有涉及 workflow 变更，`新增/修改/删除` 三节都写 `(无)`，并在段落末尾加一行 `本次无 Action 变更`——
**不要自己编造名字**。提交列表必须真实，取自 `git rev-list "$BASE".."$UPSTREAM_REF"`；文件数用 `git diff --name-only <sha>^ <sha> | wc -l`（见上面的生成块），不要手估。

**提交列表必须逐条列全，不许省略。** 本次有多少个提交就写多少行（43 个就写 43 行），
禁止出现「其余 N 个见 git log」「...」「（略）」这类写法——这份文件是给人看的，不是索引。

**前插完成后回读校验**：`head -70 out.txt` 必须能看到刚写的那一段，**且它在表头正下方、所有旧记录之上**；
段头的短 SHA 与 `UPSTREAM_HEAD` 一致。若发现新段落跑到了文件末尾，说明用了追加，改回来重写。

### 13.3 改动账本（**新段落插在最前面，旧内容原样下移，不覆写**）

**直接用第 2 步探测出的 `$FIXLOG`，不要在这里重新判断一次**（重新判断有可能因为期间新增/删除文件而落到另一个文件上）。
若 `$FIXLOG` 为空，说明第 2 步漏执行了，停下来回头补。

- 两个相似文件同时存在时**只写 `fixxed.txt`**（即 `$FIXLOG` 指向的那个），并在汇报里告诉我「发现 fixed.txt 与 fixxed.txt 并存」，不要合并、不要删除任何一个。
- 日志文件原本不存在时，本步骤负责创建 `$FIXLOG`（默认 `fixxed.txt`），并在汇报里标注「首次建立改动日志」。

**文件已存在时**（绝大多数情况），里面是我此前改动的记录。你要：
1. 先**完整读一遍**，理解已有格式（标题层级、字段名、书写习惯）；
2. 把新段落**插到文件最开头**（旧内容整体下移），格式与已有内容保持一致；
3. 旧内容**一行都不许改、不许重排、不许删除**。

**第一步：先把本次段落写成文件**（理由同 13.2——跨 shell 传整段文本必丢）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${FIXLOG:?FAIL: FIXLOG 为空，回第 2 步探测}" \
  "${UPSTREAM_SHORT:?FAIL: UPSTREAM_SHORT 为空}" "${BASE:?FAIL: BASE 为空}" "${UPSTREAM_REF:?FAIL: UPSTREAM_REF 为空}" \
  "${UPSTREAM_REMOTE:?FAIL: UPSTREAM_REMOTE 为空}" "${UPSTREAM_BRANCH:?FAIL: UPSTREAM_BRANCH 为空}"
cd "$REPO_ROOT" || exit 1

# 变量非空不等于能解析：这里有两个值要真的参与计算，解析不到就会在账本里留下空白
# （"上游  个提交"、"基线 "），看起来像同步成功，实际什么都没算。
git rev-parse --verify --quiet "$UPSTREAM_REF^{commit}" >/dev/null \
  || { echo "FAIL: UPSTREAM_REF 解析不到（$UPSTREAM_REF），回第 3 步"; exit 1; }
git rev-parse --verify --quiet "$BASE^{commit}" >/dev/null \
  || { echo "FAIL: BASE 解析不到（$BASE），回第 4 步"; exit 1; }

# 构建结果也要断言：缺了的话模板会写出 "构建:  → "，占位符检查只会报"占位符没替换"，
# 排查方向跑偏（真正的问题是第 8 步没落盘）。
[ -n "$BUILD_RESULT" ] || { echo "FAIL: BUILD_RESULT 为空，第 8 步没落盘，回去补"; exit 1; }
[ -n "$TARGET_PROJ" ]  || { echo "FAIL: TARGET_PROJ 为空，回第 0 步"; exit 1; }

if [ -f "$FIXLOG" ]; then echo "FIXLOG=$FIXLOG（已存在，$(wc -l < "$FIXLOG") 行）"; else echo "FIXLOG=$FIXLOG（不存在，将新建）"; fi

# 几份"边做边记"的日志如果本轮没写过，就补一行占位，避免 heredoc 拼出空段落
[ -s /tmp/extra-log.md ]     || echo "- 本次无其他调整。" > /tmp/extra-log.md
[ -s /tmp/clean-merge-log.md ] || echo "- 本次无干净三方合并。" > /tmp/clean-merge-log.md
[ -s /tmp/conflict-log.md ]  || echo "- 本次无冲突需要修正。" > /tmp/conflict-log.md
[ -s /tmp/buildfix-log.md ]  || {
  case "${BUILD_RESULT%%（*}" in
    通过)   echo "- 无，构建一次通过。" > /tmp/buildfix-log.md ;;
    未执行) echo "- 构建未执行（${BUILD_RESULT}），本轮没有修复记录。" > /tmp/buildfix-log.md ;;
    *)      echo "- 构建未通过（${BUILD_RESULT}），详见 action.txt。" > /tmp/buildfix-log.md ;;
  esac
}

# 冲突标记残留数直接从 7.1 的重扫结果取，不靠 AI 数
# 同 13.1：用 -f 判存在，别让"< 打不开"被管道吞成空串
# （空串写进账本就是"上游  个文件"，看起来像统计坏了）
cnt() { if [ -f "$1" ]; then wc -l < "$1" | tr -d ' '; else echo 0; fi; }
CM=$(cnt /tmp/conflict-files.txt)
UPN=$(cnt /tmp/up-files.txt)
BN=$(cnt /tmp/both-files.txt)
LN=$(cnt /tmp/local-files.txt)

# heredoc 不加引号：下面 $(cat ...) 需要展开，把四份边做边记的日志直接拼进来，不靠回忆。
# ★ 账本只记「改了哪些文件、每个文件怎么处理的」，不写冲突细节、不贴代码。
#   用户要求过：文件必须列全，代码怎么改的不写。
#   处理方式只有四种：取上游 / 三方合并 / 冲突修正 / 保留本地。
git -c core.quotePath=false diff --no-renames --name-only "$PRE_MERGE_HEAD" > /tmp/fixlog-all.raw \
  || { echo "FAIL: 取改动清单失败（PRE_MERGE_HEAD 无效？）"; exit 1; }
git -c core.quotePath=false diff --no-renames --cached --name-only "$PRE_MERGE_HEAD" >> /tmp/fixlog-all.raw \
  || { echo "FAIL: 取暂存改动清单失败"; exit 1; }
LC_ALL=C sort -u /tmp/fixlog-all.raw \
  | grep -vE '^(action\.txt|out\.txt|patches/.*)$' \
  | grep -vxF -- "$FIXLOG" > /tmp/fixlog-changed.txt || true
# grep 无匹配时退出码是 1，不是错误；但清单为空要当错误处理（一个文件都没改却写账本，肯定是操作错了）
[ -s /tmp/fixlog-changed.txt ] || { echo "FAIL: 改动清单为空，检查 PRE_MERGE_HEAD 和暂存状态"; exit 1; }

: > /tmp/fixlog-files.md
while IFS= read -r f; do
  [ -n "$f" ] || continue
  if   [ -f /tmp/conflict-files.before.txt ] && grep -Fxq -- "$f" /tmp/conflict-files.before.txt; then how="冲突修正"
  elif [ -f /tmp/clean-merge-files.txt ]     && grep -Fxq -- "$f" /tmp/clean-merge-files.txt;     then how="三方合并"
  elif [ -f /tmp/custom-kept.txt ]           && grep -Fq  -- "$f" /tmp/custom-kept.txt;           then how="保留本地"
  else how="取上游"
  fi
  printf -- '- %s — %s\n' "$f" "$how" >> /tmp/fixlog-files.md
done < /tmp/fixlog-changed.txt

cat > /tmp/new-fixlog.txt <<BLOCK
================================================
[$(date '+%Y-%m-%d %H:%M')] 同步 $UPSTREAM_REMOTE/$UPSTREAM_BRANCH @ $UPSTREAM_SHORT
================================================

范围：基线 $(git rev-parse --short "$BASE") → $UPSTREAM_SHORT，上游 $(git rev-list --count "$BASE".."$UPSTREAM_REF") 个提交，改动 $(cnt /tmp/fixlog-changed.txt) 个文件。
处理：冲突 ${CONFLICT_FILES:-0} 个（残留 $CM 个）；本地定制 $LN 个文件保留未动；分支回退 ${BRANCH_FALLBACK:-0}。
构建：$TARGET_PROJ → $BUILD_RESULT

改动的文件
----------
$(cat /tmp/fixlog-files.md)
BLOCK

[ -s /tmp/new-fixlog.txt ] || { echo "FAIL: 新段落为空"; exit 1; }
# 必须精确到日期行。只写 '^\[' 的话，正文里随手一行 [改上游] / [issue #123] 就能骗过检查，
# 真漏了标题行也照样通过——而下一轮 13.3 的 awk 截段要靠 [YYYY- 开头，那时才发现账本坏了。
grep -qE '^\[[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] ' /tmp/new-fixlog.txt \
  || { echo "FAIL: 段落缺少 [YYYY-MM-DD HH:MM] 标题行"; exit 1; }
grep -q -- "$UPSTREAM_SHORT" /tmp/new-fixlog.txt \
  || { echo "FAIL: 段落里没有本次短 SHA"; exit 1; }

# 占位符统一按尖括号检。模板里可能残留 <文件:行号> / <0 或 数量> / <TARGET_PROJ> 等，
# 只查 '<填：' 是查不到的（本模板里根本没有 '<填：' 这种写法）。
# ★ 同样不能写成 '<[^>]{1,60}>'：账本里贴 C# 泛型（List<Profile>、Dictionary<string,int>）
#   太正常了，宽匹配会把真实内容判成占位符，逼着 AI 去"修"一段本来正确的内容。
# ★ LC_ALL=C 必须加：UTF-8 locale 下 " -~" 这个范围会被当成字面字符集，
#   [^ -~] 变成"不是空格/减号/波浪号"，几乎匹配所有字母，检查直接失效（全误报）。
if tr -d '\r' < /tmp/new-fixlog.txt | LC_ALL=C grep -nE '<[^>]*[^ -~][^>]*>|<[A-Z][A-Z0-9_]{2,}>'; then
  echo "FAIL: 上面这些尖括号占位符没替换，改完再跑这一步"; exit 1
fi

echo "OK: 新段落 $(wc -l < /tmp/new-fixlog.txt) 行"
```

**第二步：插到文件最开头（旧内容原样下移，一个字都不许改）**：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${FIXLOG:?FAIL: FIXLOG 为空}"
cd "$REPO_ROOT" || exit 1
[ -s /tmp/new-fixlog.txt ] || { echo "FAIL: 新段落不存在，先做上一步"; exit 1; }

# 新段落自己先转 LF（只动新段，绝不动旧内容——旧文件如果本来用 CRLF，
# 对它做 tr -d 会把每一行都改掉，正好违反"旧内容一个字都不许改"）
tr -d '\r' < /tmp/new-fixlog.txt > /tmp/new-fixlog.lf && mv /tmp/new-fixlog.lf /tmp/new-fixlog.txt \
  || { echo "FAIL: 新段落转 LF 失败"; exit 1; }

# ★ 末尾必须有换行。没有的话 cat 新段+旧文件时，新段最后一行会和旧文件第一行粘成一行：
#   行数校验会失败，但报的是"旧内容被改动了"，很容易被误判成"手滑改到旧内容"。
if [ -n "$(tail -c 1 /tmp/new-fixlog.txt)" ]; then printf '\n' >> /tmp/new-fixlog.txt; fi

NEW_N=$(wc -l < /tmp/new-fixlog.txt | tr -d ' ')

if [ -f "$FIXLOG" ]; then
  # 先留一份原件，用于逐字节比对（行数相等不代表内容没被换掉）
  cp "$FIXLOG" /tmp/fixlog.old || { echo "FAIL: 备份 FIXLOG 失败"; exit 1; }
  OLD_N=$(wc -l < "$FIXLOG" | tr -d ' ')
  cat /tmp/new-fixlog.txt "$FIXLOG" > "$FIXLOG.tmp" \
    || { echo "FAIL: 拼接失败"; rm -f "$FIXLOG.tmp"; exit 1; }

  # 行数守恒
  GOT=$(wc -l < "$FIXLOG.tmp" | tr -d ' ')
  EXP=$((NEW_N + OLD_N))
  [ "$GOT" -eq "$EXP" ] \
    || { echo "FAIL: 行数 $GOT != 旧 $OLD_N + 新 $NEW_N = $EXP"; rm -f "$FIXLOG.tmp"; exit 1; }

  # ★ 逐字节验证"旧内容原样下移"——比行数更可靠：
  # 把 tmp 跳过前 NEW_N 行后的部分，跟备份原件逐字节比较。
  tail -n +$((NEW_N + 1)) "$FIXLOG.tmp" | cmp -s - /tmp/fixlog.old \
    || { echo "FAIL: 旧内容被改动了（不只是行数问题）"; rm -f "$FIXLOG.tmp"; exit 1; }
else
  OLD_N=0
  cp /tmp/new-fixlog.txt "$FIXLOG.tmp" || { echo "FAIL: 建文件失败"; exit 1; }
fi

[ -s "$FIXLOG.tmp" ] || { echo "FAIL: 结果为空"; exit 1; }
mv "$FIXLOG.tmp" "$FIXLOG" || { echo "FAIL: 覆盖 $FIXLOG 失败（文件被占用？关掉编辑器重试）"; exit 1; }

head -5 "$FIXLOG"
echo "旧 $OLD_N 行 → 新 $(wc -l < "$FIXLOG") 行"
```

- **为什么删掉了"旧文件末尾补换行"**：新段落在前、旧内容在后，所以新旧之间会不会粘连，
  取决于**新段落**末尾有没有换行，跟旧文件末尾无关。旧文件末尾补换行反而改动了旧内容。
- **为什么用 `cmp` 而不是只比行数**：行数相等时，"旧内容被换成另一段等行数的内容"照样能过。
  `cmp` 是逐字节比对，能把这类静默损坏抓住。

**结构断言**（不要只看 `head -20`——新段落可能比它更长，看一半判断不了）：

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空，回第 0 步重建环境文件}"
cd "$REPO_ROOT" || exit 1
[ -n "$FIXLOG" ] || { echo "FAIL: FIXLOG 为空"; exit 1; }

[ -n "$UPSTREAM_SHORT" ] || { echo "FAIL: UPSTREAM_SHORT 为空"; exit 1; }

# 第一行应是本次新段的分隔线
head -1 "$FIXLOG" | grep -q '^====' \
  || { echo "FAIL: 第 1 行不是分隔线，新段没插在最前面"; head -3 "$FIXLOG"; exit 1; }

# 前 3 行必须含本次短 SHA（否则可能是上一轮的残留段落）
head -3 "$FIXLOG" | grep -q -- "$UPSTREAM_SHORT" \
  || { echo "FAIL: 前 3 行没有本次短 SHA（$UPSTREAM_SHORT），可能插错位置"; head -3 "$FIXLOG"; exit 1; }

echo "FIXLOG_OK: 新段在最顶部，含 $UPSTREAM_SHORT"
```

**关于格式**：新段落由上面第一步的 heredoc 统一生成，只有四块：

```
================================================
[2026-10-24 13:20] 同步 upstream/develop/v2/misha-alpha @ 329bf4b1
================================================

范围：基线 41fc7035 → 329bf4b1，上游 1 个提交，改动 50 个文件。
处理：冲突 0 个（残留 0 个）；本地定制 25 个文件保留未动；分支回退 0。
构建：ClassIsland.Desktop → 通过（0 errors / 1294 warnings）

改动的文件
----------
- ClassIsland/Views/MainView.axaml — 取上游
- ClassIsland/Views/MainView.axaml.cs — 取上游
- ClassIsland.Core/Controls/VerticalDrawer.axaml.cs — 冲突修正
```

规则只有三条：

- **文件必须列全**：`git diff` 里出现过的代码文件，一个都不能漏（三个记录文件自身和 `patches/` 除外）；
- **每个文件只写一句处理方式**，只允许四种：`取上游` / `三方合并` / `冲突修正` / `保留本地`；
- **不写冲突是怎么合的、不写代码是怎么改的、不贴片段、不写行号**。

为什么不写细节：账本是给"下一次同步"看的，它只需要知道"这个文件上次被动过、是怎么处理的"。
真要看清改了什么，`git log -p` 和文件本身比账本准得多。把细节塞进账本只会让它越来越长，
读到后面全是噪音，反而没人看。

- **凡是你动过的文件，一个都不能漏。** 冲突合并、构建修复、防御性补强、格式调整——只要在 `git diff` 里出现，就必须列进来。
- 每条写全五要素：**位置（文件:行号）、原状、改后、理由、影响面**。
- **能贴 diff 就贴 diff**：每处改动附 3–10 行前后对照或 `diff` 片段，让我不翻仓库就能看懂改了什么。
- 修改方式要写到「可复现」的程度。反例：「调整了错误处理」。正例：「把 `catch (Exception)` 改为 `catch (IOException ex)`，并在 catch 内 `logger.LogError(ex, ...)` 后 `throw`」。
- 禁止只写「已修复」「已处理」「已同步」这类没有信息量的话。
- 若本次没有任何 AI 改动，写一行 `本次无需 AI 修正的改动。`，段落仍然要插入。
- **收尾反查（必做）**：本节在 `git add`/`commit` **之前**执行，此时 `HEAD` 还等于 `PRE_MERGE_HEAD`，
  写 `git diff "$PRE_MERGE_HEAD" HEAD` 会恒为空、把每次同步都误判成"没改文件"。

  **口径要说清**：和 `fixxed.txt` 比对的，既不是"全部改动"也不是"工作区全部"，
  而是**只有 AI 亲自动过的文件**。因为 `git diff "$PRE_MERGE_HEAD"` 里必然还混着：
  ① 三个记录文件自己；② 8.3 用 `git checkout` 覆盖进来的全部「仅上游改动」文件。
  拿它们去和账本比，永远对不上，AI 只能靠编条目来凑数。

  ```
  source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
  : "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${PRE_MERGE_HEAD:?FAIL: 锚点丢失，回第 6 步}"
  cd "$REPO_ROOT" || exit 1

  # ① 本次全部改动（工作区 + 索引 + 未跟踪）
  # ★ 先写 raw 再排序，不要写成 `{ ...; } | sort -u > 目标`：管道左侧跑在子 shell 里，
  #   里面任何一步失败都传不出来，而 sort 永远返回 0 —— 清单会"看起来正常"，实际是空或残缺的。
  {
    git -c core.quotePath=false diff --no-renames --name-only "$PRE_MERGE_HEAD"
    git -c core.quotePath=false diff --no-renames --cached --name-only "$PRE_MERGE_HEAD"
    git ls-files --others --exclude-standard
  } > /tmp/all-changed.raw.txt || { echo "FAIL: 取改动清单失败，停下问我"; exit 1; }
  LC_ALL=C sort -u /tmp/all-changed.raw.txt > /tmp/all-changed.txt \
    || { echo "FAIL: 排序失败"; exit 1; }

  # ② 剔除三个记录文件。FIXLOG 里的 . 要转义，否则 fixxedXtxt 这种也会被误排除；
  #    不转义不会误伤当前两个文件名，但换个日志名就可能中招。
  FIXLOG_RE=$(printf '%s' "${FIXLOG:-fixxed.txt}" | sed 's/\./\\./g')
  grep -vE "^(action\.txt|out\.txt|${FIXLOG_RE}|patches/.*)$" /tmp/all-changed.txt > /tmp/code-changed.txt

  # ③ 再剔除「仅上游改动」的文件（那些是 8.3 批量覆盖的，不属于 AI 的手工改动）
  cut -f2 /tmp/up-only-status.txt 2>/dev/null | LC_ALL=C sort -u > /tmp/up-only-paths.txt
  LC_ALL=C comm -23 /tmp/code-changed.txt /tmp/up-only-paths.txt > /tmp/ai-touched.txt

  echo "全部代码改动 $(wc -l < /tmp/code-changed.txt) 个，其中 AI 手工动过的 $(wc -l < /tmp/ai-touched.txt) 个"
  ```

  **④ 拿 `/tmp/ai-touched.txt` 逐行比对账本——必须只比"顶部这一段"**：

  ```
  source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
  : "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${FIXLOG:?FAIL: FIXLOG 为空}"
  cd "$REPO_ROOT" || exit 1

  # 直接 grep -qF "$f" "$FIXLOG" 扫整个文件是无效检查：旧段落里提过的文件名会"匹配成功"，
  # 本轮漏记账照样能过（等于把硬性约束 16 做成摆设）。
  # 段头固定是「==== / [日期] / ====」，所以遇到等号线时往后看一行：
  # 下一行是 [YYYY- 才算下一段开始。账本内容里贴的分隔线（conflict-log.md 常贴 diff 片段）
  # 极难被误判成新段头——要两个巧合同时发生：贴进来的正好是一长串等号线，
  # 且它下一行正好是 [YYYY- 开头的日期行。这个概率可以接受。
  # ★ 这一段与 13.9 ⑤ 的截取逻辑相同，改一处必须同步改另一处。
  awk 'NR<=3 {print; next}
       /^=+$/ && length($0) >= 10 {
         hdr=$0
         if ((getline nxt) > 0) {
           if (nxt ~ /^\[[0-9][0-9][0-9][0-9]-/) exit
           print hdr; print nxt; next
         }
         print hdr; next
       }
       {print}' "$FIXLOG" > /tmp/fixlog-top.txt
  [ -s /tmp/fixlog-top.txt ] \
    || { echo "FAIL: 账本开头不是标准段头，无法确定本次段落；head -5 看一眼"; head -5 "$FIXLOG"; exit 1; }

  MISS=0
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    grep -qF -- "$f" /tmp/fixlog-top.txt || { echo "MISS: $f 没写进本次段落"; MISS=1; }
  done < /tmp/ai-touched.txt
  [ "$MISS" -eq 0 ] || { echo "FAIL: 有文件改了没记账，补完再提交"; exit 1; }
  echo "FIXLOG_COVERED（$(wc -l < /tmp/ai-touched.txt) 个文件都能在账本顶部找到）"
  ```

  **不要求数量相等**（一个文件可能有多条记录），只要求"AI 动过的文件 ⊆ 账本提到的文件"。
  如果补写发生在提交之后，开一个新 commit，**不要 `--amend`**。

三个文件全部写完后，回到第 9 步提交。**提交时 `git add` 必须排除提示词文件、`patches/`、`bin/`、`obj/`**
——直接 `git add -A` 会把它们一起提交进去，违反硬性约束 13。

## 13.9 第 13 步：定制开发（可选；`CUSTOM_TASK` 非空或我提了需求时必做）

**先确认该不该做**：参数区 `CUSTOM_TASK` 非空，或我在这轮对话里提了**要动仓库**的诉求
（新功能、改文案、换图标、调默认值、加配置项——判断口径见 1.5）。
两个都没有 → **整节跳过**，不要顺手加东西（那是"擅自扩大改动范围"，违反硬性约束 10）。

顺序和红线见 **1.5**：必须先同步、后定制；`action.txt` / `out.txt` 一个字都不许碰。下面只讲怎么做。

### ① 三道门（缺一条都不许动代码）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${FIXLOG:?FAIL: FIXLOG 为空，回第 2 步}"
cd "$REPO_ROOT" || exit 1

# 门 1：工作区必须干净——要么同步已经提交完，要么本轮官方本来就没更新
DIRTY=$(git status --porcelain)
if [ -n "$DIRTY" ]; then
  echo "FAIL: 工作区不干净，同步还没收尾，不许开始定制："
  echo "$DIRTY" | head -20
  echo "  这些若是你自己没提交的改动，先问我怎么处理（提交 / stash / 放弃），不要擅自 stash 或 checkout"
  exit 1
fi

# 门 2：同步必须已经处理过。三种合法状态：
#   a) 本轮判定 NO_UPDATE —— 官方没新提交，同步本来就不需要做
#   b) HEAD 附近有 chore(sync) 提交 —— 同步已提交（允许 pre-commit hook 补的追加提交）
#   c) 我明确说了"这次别同步" —— 只有这种情形才允许 CUSTOM_ONLY=1（见 1.5 情形 C）
if [ "${CUSTOM_ONLY:-0}" = "1" ]; then
  echo "NOTE: CUSTOM_ONLY=1，按我的要求跳过同步（汇报里必须写明）"
elif [ "${SYNC_VERDICT:-}" != "NO_UPDATE" ]; then
  git log -20 --format=%s | grep -q '^chore(sync):' || {
    echo "FAIL: 没看到同步提交（SYNC_VERDICT=${SYNC_VERDICT:-空}）"
    echo "      先回第 9 步把同步提交做完，再回来定制"
    exit 1; }
fi

# 段头要用的上游短 SHA：情形 C 或离线时可能为空，退化成 HEAD，别让后面的块断言失败
UPSTREAM_SHORT=${UPSTREAM_SHORT:-$(git rev-parse --short HEAD)}
echo "export UPSTREAM_SHORT=$UPSTREAM_SHORT" >> /tmp/upstream-sync.env

# 门 3：钉住定制基线。后面算改动、写账本、导补丁全靠它
# ★ 只在没设过的时候写。重跑本块（比如构建失败回来重来）时绝不能重置——
#   一旦重置，之前已经改过的文件就落在检查范围之外，可能"改了却没记账"还照样提交。
if [ -z "${CUSTOM_BASE:-}" ]; then
  # 第一次进本节：清掉上一轮留下的中间文件。②是"边改边记"追加写入，
  # 不清的话昨天的条目会被原样拼进今天 fixxed.txt 的 [定制] 段，看起来像本轮做的。
  # 注意这个清理也只能在第一次做——重跑时清掉，等于把本轮已经记下的改动全抹了。
  : > /tmp/custom-log.md \
    || { echo "FAIL: 清空 /tmp/custom-log.md 失败（/tmp 不可写？），停下问我"; exit 1; }
  rm -f /tmp/custom-risk.md /tmp/custom-title.txt 2>/dev/null || true
fi
CUSTOM_BASE=${CUSTOM_BASE:-$(git rev-parse HEAD)}
echo "export CUSTOM_BASE=$CUSTOM_BASE" >> /tmp/upstream-sync.env
echo "CUSTOM_BASE=$(git rev-parse --short "$CUSTOM_BASE")（定制从这里开始）  UPSTREAM_SHORT=$UPSTREAM_SHORT"
```

**为什么门 2 不看 `action.txt` 的 SHA 是不是 HEAD 的祖先**：本流程是**文件级同步**，不是 `git merge`，
`LAST_SYNCED_SHA` 通常不是本地 HEAD 的祖先，拿祖先关系判断必然误报。只看"这轮同步到底处理完没有"。

### ② 动手前先看三件事（顺序别换）

1. **上游是不是已经有了**。用功能关键词在仓库里搜一遍（界面文案、类名、命令名都要搜），
   命中就停下来问我「上游已用 XXX 实现，还要按你的方式再加一套吗」。**不许重复造。**
   纯文案 / 图标这类小改动跳过"是否已实现"，但要先确认**这个位置是不是只有一处**（同名文案散落在多个文件很常见）。
2. **列出要改的文件**，写到 `/tmp/custom-log.md`（格式见 ④）。优先"新增文件 / 新增方法 / 挂到已有扩展点"；
   必须改上游文件时，在清单里标 `[改上游]`——每一处都是下次同步的潜在冲突点。
3. **需求含糊就先问**，最多 3 个问题（放哪 / 什么行为 / 出错或空数据怎么办）。问完按最保守默认实现，
   把"我假设了什么"写进 `/tmp/custom-risk.md`（那是给下次同步看的，不要塞进文件清单里）。
   **不许**停下来不动，也**不许**脑补一大套交互。

顺手把提交标题写好——⑤ 提交时要用，现在不写，到那一步会被 FAIL 挡住白跑一轮：

```
printf '%s\n' '给设置页加自动重启开关' > /tmp/custom-title.txt
```

一行中文，**不要带引号、反引号、`$`**（它会被直接拼进 commit message）。标题别写成"完成定制"这种没信息量的。

### ③ 实现与构建

- **边改边记** `/tmp/custom-log.md`，不要等改完再回忆（回忆必漏）：
  `- 路径 — 新增` / `- 路径 — 修改` / `- 路径 — 删除`，一行一个文件，**不写行号、不写改法、不贴代码**。
  想补一句作用就写在破折号后面（`- src/A.cs — 新增，负责 X`），但不要展开成段落。
- 每个新增的公开方法/事件都要能回答"谁调用它"；只写不接的代码等于没做。
- 防御性要求与 10.3 完全一致：判空、边界、异常处理、资源释放（`using` / `Dispose`）。
  同样禁止"糊过去"的六种手段（注释报错代码 / 空 `catch` / 关 `TreatWarningsAsErrors` /
  `#nullable disable` / 改 `TargetFramework` 绕过 / 删测试）。
- **构建：把 10.2 的构建块原样再跑一次**（命令不要改，`FAST` 规则也不变），然后另存结果：

  > ⚠️ 10.2 那个块会**覆盖** `BUILD_RESULT`（它内部 `echo "export BUILD_RESULT=..." >> env`），
  > 这是预期的：同步阶段的结论早已写进 `action.txt` 并提交，不会被它改动；
  > 下面读到的 `BUILD_RESULT` **就是定制阶段这次构建的结果**。别去"恢复"它，也别回写 `action.txt`。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失"; exit 1; }
: "${BUILD_RESULT:?FAIL: BUILD_RESULT 为空，说明 10.2 的构建块没跑}"
echo "export CUSTOM_BUILD_RESULT='$BUILD_RESULT'" >> /tmp/upstream-sync.env
echo "定制阶段构建：$BUILD_RESULT"
```

- **构建失败时用下面这个定制专用循环，不要用 10.3 那个块**。两个原因：
  10.3 的 `FIX_ROUND` 是同步阶段共用的（同步可能已经用掉 3 轮，你一轮都开不了）；
  10.3 还按 `PRE_MERGE_HEAD` 把错误分成 A/B 类，而定制阶段的错误只有一类——**你刚写的代码**，
  不需要"这算不算官方自带"的判断。

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失"; exit 1; }
: "${REPO_ROOT:?}" "${TARGET_PROJ:?}" "${CUSTOM_BASE:?}"
cd "$REPO_ROOT" || exit 1

CUSTOM_FIX_ROUND=$(( ${CUSTOM_FIX_ROUND:-0} + 1 ))
echo "export CUSTOM_FIX_ROUND=$CUSTOM_FIX_ROUND" >> /tmp/upstream-sync.env
echo "=== 定制修复第 $CUSTOM_FIX_ROUND 轮（上限 3，与同步阶段分开计）==="
[ "$CUSTOM_FIX_ROUND" -le 3 ] || { echo "FAIL: 定制阶段已满 3 轮，停下把 /tmp/custom-build-errors.log 发给我"; exit 1; }

set -o pipefail
dotnet build "$TARGET_PROJ" -c Release --nologo -v minimal --no-incremental 2>&1 \
  | tee /tmp/custom-build-errors.log | grep -E 'error [A-Z]+[0-9]+|error :|错误 ?[A-Z]+[0-9]+|错误 ?:' | sort -u
BE=${PIPESTATUS[0]}
echo "BUILD_EXIT=$BE  错误行数 $(grep -cE 'error [A-Z]+[0-9]+|error :|错误 ?[A-Z]+[0-9]+|错误 ?:' /tmp/custom-build-errors.log || true)"
if [ "$BE" -eq 0 ]; then
  echo "export CUSTOM_BUILD_RESULT='通过（定制修复第 $CUSTOM_FIX_ROUND 轮后 --no-incremental 重验）'" >> /tmp/upstream-sync.env
else
  echo "export CUSTOM_BUILD_RESULT='失败（定制第 $CUSTOM_FIX_ROUND 轮，BUILD_EXIT=$BE）'" >> /tmp/upstream-sync.env
fi

# 这就是本轮的"嫌疑范围"：从定制起点到现在动过的文件
git -c core.quotePath=false diff --no-renames --name-only "$CUSTOM_BASE"
```

⚠️ `action.txt` **此时已经不是待写状态**（同步那轮已经提交了它）。**不要**回头去改它的 `BUILD_RESULT`，
否则"记录"和"commit 里的代码"对不上，下次同步会拿着一个假的构建结论当历史。

### ④ 写账本（前插，段头带 `[定制]`）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${FIXLOG:?FAIL: FIXLOG 为空}" \
  "${CUSTOM_BASE:?FAIL: CUSTOM_BASE 为空，先过 ①}" "${UPSTREAM_SHORT:?FAIL: UPSTREAM_SHORT 为空}"
cd "$REPO_ROOT" || exit 1
[ -s /tmp/custom-log.md ] || { echo "FAIL: /tmp/custom-log.md 不存在或为空，边改边记，不许事后补"; exit 1; }
[ -s /tmp/custom-title.txt ] || { echo "FAIL: /tmp/custom-title.txt 不存在或为空，回 ③ 补提交标题"; exit 1; }
[ -f /tmp/custom-risk.md ] || echo "- 未评估（本次没写风险清单，请人工确认是否碰到上游文件）" > /tmp/custom-risk.md

# 段头要写「改动 N 个文件」。先落 raw 再计数，别写成 `{ ...; } | sort -u | wc -l`：
# 大括号里两条 git 任意一条失败，退出码都由最后一条决定，sort 又恒返回 0，
# 结果就是「git 报了错、账本上写着改动 0 个文件」。⑤ 统计代码改动用的是同一套写法。
{
  git -c core.quotePath=false diff --no-renames --name-only "$CUSTOM_BASE"
  git ls-files --others --exclude-standard
} > /tmp/custom-count.raw.txt 2>/tmp/custom-count.err \
  || { echo "FAIL: 统计定制改动文件失败"; cat /tmp/custom-count.err; exit 1; }
CUSTOM_N=$(LC_ALL=C sort -u /tmp/custom-count.raw.txt | wc -l | tr -d ' ')
case "$CUSTOM_N" in ''|*[!0-9]*) echo "FAIL: 定制改动计数非法 [$CUSTOM_N]"; exit 1 ;; esac

cat > /tmp/new-fixlog.txt <<BLOCK
================================================
[$(date '+%Y-%m-%d %H:%M')] [定制] 基于同步点 $UPSTREAM_SHORT
================================================

需求：$(cat /tmp/custom-title.txt)
处理：改动 $CUSTOM_N 个文件；构建 $TARGET_PROJ → ${CUSTOM_BUILD_RESULT:-未执行}（修复轮次 ${CUSTOM_FIX_ROUND:-0}）

改动的文件
----------
$(cat /tmp/custom-log.md)

下次同步注意
------------
$(cat /tmp/custom-risk.md)
BLOCK

# 段头必须能被一眼认出是定制段
head -3 /tmp/new-fixlog.txt | grep -q '\[定制\]' || { echo "FAIL: 段头没有 [定制] 标记"; exit 1; }
head -3 /tmp/new-fixlog.txt | grep -q -- "$UPSTREAM_SHORT" || { echo "FAIL: 段头没有同步点短 SHA"; exit 1; }
echo "OK: 定制段 $(wc -l < /tmp/new-fixlog.txt) 行"
```

生成完 `/tmp/new-fixlog.txt` 后，**把 13.3 的「第二步：插到文件最开头」和「结构断言」原样再跑一遍**
（它们只认 `$FIXLOG` 和 `/tmp/new-fixlog.txt`，不关心里面是同步段还是定制段）。
结构断言要求前 3 行含 `$UPSTREAM_SHORT`，上面的段头第 2 行就是它，能过。

> 这是**第二次**往同一个文件前插。插完的效果是：`[定制]` 在最上面，`[同步]` 那一段被压到第二位，更早的继续往下。
> 顺序正确（最近的在上），旧内容仍然逐字节没动——13.3 的 `cmp` 会证明这一点。

### ⑤ 提交（**单独一个 commit**，不许和同步合并）

```
source /tmp/upstream-sync.env 2>/dev/null || { echo "FAIL: 环境文件缺失，回第 0 步重建"; exit 1; }
: "${REPO_ROOT:?FAIL: REPO_ROOT 为空}" "${FIXLOG:?FAIL: FIXLOG 为空}" "${CUSTOM_BASE:?FAIL: CUSTOM_BASE 为空}"
cd "$REPO_ROOT" || exit 1

# ---- 改了哪些文件（工作区 + 已暂存 + 未跟踪）----
# 与 10.3 ① / 13.3 收尾同一套写法：先写 raw 再排序。管道会把左侧的失败吞掉，
# sort 永远返回 0，清单可能"看起来正常"其实是空的。
{
  git -c core.quotePath=false diff --no-renames --name-only "$CUSTOM_BASE"
  git -c core.quotePath=false diff --no-renames --cached --name-only "$CUSTOM_BASE"
  git ls-files --others --exclude-standard
} > /tmp/custom-all.raw.txt || { echo "FAIL: 取改动清单失败"; exit 1; }
LC_ALL=C sort -u /tmp/custom-all.raw.txt > /tmp/custom-all.txt || { echo "FAIL: 排序失败"; exit 1; }

# 去掉记录文件与补丁产物，剩下的才是本次真正写的代码（FIXLOG 的 . 要转义）
FIXLOG_RE=$(printf '%s' "${FIXLOG:-fixxed.txt}" | sed 's/\./\\./g')
grep -vE "^(action\.txt|out\.txt|${FIXLOG_RE}|patches/.*)$" /tmp/custom-all.txt > /tmp/custom-code.txt
if [ ! -s /tmp/custom-code.txt ]; then
  # 不直接 FAIL：确实存在"只补一条历史记录"这类需求（定制文档/账本，没有代码）。
  # 但也不能默默放过去——多数情况下是改动没落盘、或路径算错（比如全被上面的 exclude 过滤了）。
  echo "NOTE: 本次定制没有代码改动，只有记录文件变化。"
  echo "      如果你要的就是补账本，继续；否则停下来问我，"
  echo "      不要自己判断「应该没问题」就提交一个只改了 $FIXLOG 的 commit"
fi

# ---- action.txt / out.txt 在定制阶段不许有改动 ----
if git diff --name-only "$CUSTOM_BASE" | grep -qxE 'action\.txt|out\.txt'; then
  echo "FAIL: 定制阶段动了 action.txt / out.txt，把这两处恢复原样再来"; exit 1
fi

# ---- 记账覆盖：只截取账本顶部这一段的正文，避免拿旧段落蒙混过关 ----
# 判定"下一段开始"用的是**结构**，不是"第 3 条等号线"：
#   段头固定是「===== / [YYYY-MM-DD ...] / =====」三行。
#   所以遇到等号线时往后看一行，下一行是 [日期] 才算新段开始。
# 这样 /tmp/custom-log.md 里贴进来的分隔线（哪怕正好是 ==========）极难造成误截断
# （需要它下一行恰好是 [YYYY- 日期行），不会出现"明明记了却报 MISS"的假失败。
# ★ 这一段与 13.3 收尾反查的截取逻辑相同，改一处必须同步改另一处。
# 顺带说明：这里不能用 ^={40,}$，mawk 默认不认区间量词，会一个都不匹配。
awk 'NR<=3 {print; next}
     /^=+$/ && length($0) >= 10 {
       hdr=$0
       if ((getline nxt) > 0) {
         if (nxt ~ /^\[[0-9][0-9][0-9][0-9]-/) exit
         print hdr; print nxt; next
       }
       print hdr; next
     }
     {print}' "$FIXLOG" > /tmp/fixlog-top.txt

# 截出来是空的，说明账本开头不是标准段头（3 行：==== / [日期] / ====）。
# 这时**不能退回去用整份文件**——那样旧段落里提过的文件名会顶替掉本轮的记账，
# 检查就白做了。停下让人看一眼。
[ -s /tmp/fixlog-top.txt ] \
  || { echo "FAIL: $FIXLOG 开头不是标准段头，无法确定本轮段落；先看 head -5 再决定"; head -5 "$FIXLOG"; exit 1; }
MISS=0
while IFS= read -r f; do
  [ -n "$f" ] || continue
  grep -qF -- "$f" /tmp/fixlog-top.txt || { echo "MISS: $f 没写进 $FIXLOG 的 [定制] 段"; MISS=1; }
done < /tmp/custom-code.txt
[ "$MISS" -eq 0 ] || { echo "FAIL: 有文件改了没记账，补完再提交"; exit 1; }

# ---- 暂存：规则与第 9 步完全一致 ----
# 同样先清掉写记录文件时可能残留的中间文件（否则会被算成"代码改动"，还报 MISS 误导人）
rm -f action.txt.lf out.txt.tmp out.txt.lf out.txt.bak "$FIXLOG.tmp" 2>/dev/null || true

git add -A -- . \
  ':(exclude)patches' \
  ':(exclude)bin' ':(exclude)*/bin' \
  ':(exclude)obj' ':(exclude)*/obj' \
  ':(exclude)*.tmp' \
  ':(exclude)action.txt.lf' ':(exclude)out.txt.lf' ':(exclude)out.txt.bak' \
  ':(exclude)*.prompt.md' ':(exclude)prompt.md'
git reset -q -- patches 2>/dev/null || true
git diff --cached --name-only | grep -qE '(^|/)(bin|obj)/' \
  && { echo "FAIL: bin/obj 进了暂存区，先修 .gitignore"; exit 1; }

# ---- 标题先写文件，避免引号/反引号被 shell 吃掉 ----
[ -s /tmp/custom-title.txt ] || { echo "FAIL: 先写 /tmp/custom-title.txt（一行标题，中文，别带引号）"; exit 1; }
TITLE=$(head -1 /tmp/custom-title.txt)

AUTHOR_OPTS=()
git config user.name  >/dev/null 2>&1 || AUTHOR_OPTS+=(-c user.name="AI Sync Bot")
git config user.email >/dev/null 2>&1 || AUTHOR_OPTS+=(-c user.email="sync@local")

git "${AUTHOR_OPTS[@]}" commit -F - <<MSGEOF
feat(custom): $TITLE

- 基于同步点 $UPSTREAM_SHORT（action.txt 的基线不变，定制不改同步进度）
- 改动 $(wc -l < /tmp/custom-code.txt | tr -d ' ') 个文件，明细见 $FIXLOG 顶部 [定制] 段
- 构建验证：$TARGET_PROJ → ${CUSTOM_BUILD_RESULT:-未执行}
MSGEOF

echo "=== 提交结果 ==="
git log --oneline -1
git show --name-only --format= HEAD | grep -qxF "$FIXLOG" \
  || { echo "FAIL: $FIXLOG 不在这个 commit 里"; exit 1; }

# 提交后工作区应该是干净的。只打印不判断的话，下一行照样说"应为空"，
# 读者会以为已经确认过——这里必须显式判断。
LEFT=$(git status --porcelain)
if [ -n "$LEFT" ]; then
  echo "FAIL: 提交后工作区还有未提交内容（多半是 hook 改了文件，或临时产物没清）："
  echo "$LEFT" | head -10
  echo "      先看清楚是什么再决定：hook 改的就再补一个 commit（不许 --amend），临时产物就删掉"
  exit 1
fi
echo "CUSTOM_COMMIT_OK（工作区已确认干净）"
```

- **有同步提交时必须是两个 commit**：同步一个（`chore(sync):`）、定制一个（`feat(custom):`）。
  合并成一个会让下次同步分不清"哪些代码是上游带来的"。**不许 `--amend` 同步那个 commit。**
  本轮是 `NO_UPDATE` / `CUSTOM_ONLY` 时本来就没有同步 commit，只有 `feat(custom):` 一个，这是合法的。
- 提交信息里的 body 沿用同样的思路（说清基于哪个同步点、改了什么、构建结果），不要空 body。

### ⑥ 推送（规则与第 10 步一模一样，不另起一套）

| 情况 | 怎么做 |
|---|---|
| 同步那轮**已经推成功** | 从第 1 道（重验 URL）到第 3 道（真推）**再完整走一遍**；别只跑最后那条 `git push` |
| 同步那轮**走了补丁模式** | **重新执行 12.1**。`PATCH_BASE` 是按远端真实状态算的，会把同步 + 定制两个 commit 一起打进 `patches/update.txt`；**不要**手工拼补丁，也不要新增 `update-2.txt`。⚠️ 12.1 有**两块**（`update.txt` 和 `apply.txt`），两块都要重跑——只跑前一块会让 `apply.txt` 里写的提交数还是旧的 |
| 同步那轮 `PUSH_MODE=direct` 且失败 | 定制做完后同样按 `direct` 再试一次；仍失败就如实报错，**不许偷偷降级成补丁模式** |

推送前仍要过"目标 URL 必须含 `NLCLI`"那道断言。推送成功后 `build_release.yml` 会再跑一遍，这是正常的。

⚠️ 第二次推送被拒（`non-fast-forward` / `fetch first`）时：**先 `git fetch` 看远端多了什么**，
再决定 rebase 还是停下问我。**绝不许 `--force`**——那会把别人（或你另一台机器）的提交冲掉。

### ⑦ 这一节的收尾清单

- [ ] 三个门都过（工作区干净、同步已处理、`CUSTOM_BASE` 已落盘）
- [ ] 上游是否已有同功能——查过，并在汇报里说明
- [ ] `/tmp/custom-log.md` 覆盖了**每个**改动文件（⑤ 的 while 循环会替你验）
- [ ] 构建跑过，结果在 `CUSTOM_BUILD_RESULT`；失败没提交
- [ ] `$FIXLOG` 顶部是 `[定制]` 段，`[同步]` 段被完整保留
- [ ] `git log --oneline -5` 能看到 `feat(custom):`；本轮有同步时还应看到 `chore(sync):`
- [ ] 推送或补丁二选一，且汇报里写明是哪个

**如果中途停下（需求要确认 / 3 轮还没修好）**：工作区是**脏的**，改动都还在。汇报最后必须写一句
「工作区有 N 个未提交文件，下次运行前先让我处理（提交或撤销）」——否则下一轮第 0 步会直接挡住你，
而用户不知道为什么。

## 14. 第 12 步：向我汇报（用中文，结构化）

> 编号说明：这一步编号是 12，但**实际最后执行**（13.9 的定制、以及定制后的推送都在它前面）。
> 编号按章节顺序排，不按时间排——看到「第 12 步」不要以为是定制之前就要汇报。

```text
（下面是汇报格式，不是命令，不要执行）

## 本次参数
- FAST=<1/0>  PUSH_MODE=<auto/direct/patch>  TARGET_PROJ=<路径>
- 与参数区是否一致: 是/否（你若在对话里临时指定过，这里写实际用到的值）

## remote 识别
- MY_REMOTE      = <名字> → <URL>
- UPSTREAM_REMOTE= <名字> → <URL>
- 是否检测到命名反转: 是/否

## 开工前对齐
- <MY_REMOTE>/<MY_BRANCH>: <对齐前状态> → <对齐后短SHA>
- 是否执行了 pull: 是/否（原因）
- 远端分支此前不存在: 否 / 是（新建 fork 或分支改名，本轮推送会创建它）

## 同步结果
- 同步是否跳过: 否 / 是（按你的要求 `CUSTOM_ONLY=1`；`action.txt` / `out.txt` 未改动）
- 官方: ClassIsland/ClassIsland @ <UPSTREAM_BRANCH>
- 分支回退: 否 / 是（develop/v2/misha-alpha → master），回退原因
- 离线模式: 否 / 是（`UPSTREAM_OFFLINE=1`：fetch 不到官方仓库，账本参照点用的是本地 HEAD <短SHA>）
- 基线: <BASE 短SHA>  →  目标: <UPSTREAM 短SHA>
- 基线是否重算: 否 / 是（`BASELINE_REBUILT=1`；写明原因：分支回退 / action.txt 基线失效 / 不在官方历史）
- 本地: <LOCAL_BRANCH>  →  提交 <本地 commit 短SHA>
- 提交次数: <N>（通常为 1；有定制则 2；hook 触发补提交再加 1，需说明原因）
- 推送: <MY_REMOTE>/<MY_BRANCH>（成功 / 失败原因 / 未执行-已导出补丁）
- CI: 已触发 build_release.yml / 未确认（**没有查，不要猜**）/ 未触发（本次没推送）

## 变更统计
- 新增 <n> 个文件 / 修改 <n> 个 / 删除 <n> 个
- 无冲突直接写入: <n> 个
- 冲突需 AI 修正: <n> 个（逐个列出文件名）

## 校验与构建
- 冲突标记残留检查: 通过（0 个文件）/ 失败（<n> 个文件）
- YAML 语法校验: <n>/<n> 通过 / **未执行（本次无 workflow 变更）/ 未执行（缺 python3+PyYAML）**
- 首次构建: 通过 / 失败（错误数 <n>，错误码：…）/ **未执行（环境无 dotnet / SDK 版本不足 x.x）**
- 修复轮次: <n> 轮（上限 3）
- 修复明细:
  | 文件 | 错误码 | 根因 | 修法 | 影响面 |
- 最终构建: **<直接引用 action.txt 的 BUILD_RESULT 原文>**
- 构建目标回退: 否 / 是（Desktop 项目不存在，已回退到 ClassIsland，构建面缩小）

## 防御性补强
- <本次新增的判空 / 边界检查 / 异常处理 / 资源释放，列文件与要点；没有就写「无」>

## 定制开发（仅当本轮有需求；没有就整段写「无」）
- 需求原文: <我原话 / CUSTOM_TASK 内容>
- 我们怎么理解: <一句话>；假设: <列出来，没有就写「无」>
- 上游是否已有同功能: 否 / 是（已停下来问你）/ 未查（说明原因）
- 改动: 新增 <n> 个 / 修改 <n> 个 / 删除 <n> 个（逐个列文件名，改上游的标 `[改上游]`）
- 提交: <短 SHA> `feat(custom): …`（与同步 commit 分开）
- 构建: **<引用 CUSTOM_BUILD_RESULT>**；定制阶段修复轮次 <n> 轮
- 账本: `$FIXLOG` 顶部 `[定制]` 段，覆盖了全部改动文件（自检已过）
- 下次同步风险: <改了哪些上游文件；只新增文件就写「无」>
- 推送: 与同步一起推成功 / 单独补推成功 / 已重新导出补丁（写明补丁里实际有几个提交：只含 `feat(custom):`，还是两个都在）

## 冲突处理明细
| 文件 | 冲突原因 | 我的定制 | 官方改动 | 处理结果 |
|---|---|---|---|---|

## 受影响的工作流
- <名称列表>

## 生成的文件
- action.txt / out.txt / <日志文件名>（说明各自写入的内容要点）
- 日志文件状态: 沿用已有 / 首次建立 / 发现多个疑似文件（列出）

## 需要我确认的事项
- <语义互斥的文件、构建失败、推送被拒、跳过的删除、本地领先未推送的提交等；没有就写「无」>

## 补丁（仅降级模式）
- 补丁: patches/update.txt（<N> 个提交，<大小>）
- 说明: patches/apply.txt
- 状态: 已导出 / **本次无待推送提交，未导出**（此时不要说"已导出补丁"）
- 有定制时: 补丁里必须是**所有未推送提交**——同步那轮也走了补丁模式，就是 `chore(sync):` + `feat(custom):` 两个；
  同步那轮已经推成功、只有定制没推，就只有 `feat(custom):` 一个。**说清实际是几个、分别是哪几个**，别只说"已导出"。
- 应用方式（在你自己机器上）:
  cd 你的仓库 && git am update.txt && git push <含 NLCLI 的 remote> HEAD:<分支名>
```

汇报前，逐条对照 15 节的硬性约束自检一遍；任何一条不满足，先修正再汇报。

## 15. 硬性约束（违反即任务失败）

1. **remote 身份以 URL 为准，不以名字为准**：URL 含 `NLCLI` 的一定是我自己的仓库（推送终点），含 `ClassIsland` 的一定是官方（只读）。名字叫反了要自动纠正，不许将错就错。
2. 绝不 push 到官方 remote；推送前必须再确认一次目标 URL 含 `NLCLI`。
3. 不许 `git push --force` / `--force-with-lease` / `--no-verify`。
4. 不许丢掉我在 `fixxed.txt` 中记录的定制——冲突时「保留我的改动」优先级高于「吸收官方」。
5. 不许自创 `fixxed.txt` 的格式；统一按 13.3 的**文件清单**写（一行一个文件，只写处理方式）。
   **新段落插在文件最前面**；旧内容默认原样下移，但**我明确要求改格式时，历史段也要一起改**——
   留着几段几十行的旧格式，只会拖长上下文、让后面的 AI 读不出重点。
6. 不许伪造记录文件内容；`out.txt` 里的工作流名称和提交列表必须真实来自仓库。
7. 不许在语义互斥、无法两全的冲突上擅自决定，必须停下来问我。
8. **构建验证不通过（或未执行而谎报通过）即视为任务失败**。构建失败必须走 10.3 的修复流程，最多修 3 轮；
   定制阶段的构建失败走 13.9 ③ 的专用循环（`CUSTOM_FIX_ROUND`，独立计数，同样 3 轮上限）。
   两种情况都不许带着失败状态提交或推送。
9. 修复编译错误时**禁止糊弄**：不许注释报错代码、不许空 `catch`、不许关 `TreatWarningsAsErrors`、不许 `#nullable disable`、不许删测试。新增代码必须符合 10.3 的防御性编程清单。
10. 除 13.9 明确要求实现的功能之外，不许顺手改动与本次任务无关的文件，不许做格式化 / 重排 / 删注释这类"顺手清理"。
11. **不许因为官方无更新就跳过与我自己的仓库对齐**，也不许因为我自己的仓库有更新就误判官方也有更新；两者必须分别判断、分别处理。
12. 每一步执行完都要在对话里简短说明做了什么，关键命令的原始输出要贴出来（尤其是 `git remote -v`、`git status`、`git rev-list --left-right --count`、`git diff --stat`、构建错误、冲突文件的差异）。
13. 工作区里若放着这份提示词文件、`patches/`、其它临时笔记，都不属于同步内容：
    `git add` 时必须用 pathspec 排除（`':(exclude)patches'` 等），**不许直接 `git add -A` 然后指望 .gitignore**。
    写记录文件时产生的中间产物（`action.txt.lf`、`out.txt.tmp`、`out.txt.lf`、`out.txt.bak`、`$FIXLOG.tmp`）
    同样不许入库：暂存前先 `rm -f`，并保留 `':(exclude)*.tmp'` 等排除项。
14. **1.3 节里标注为"必做"的双重校验，一项都不许省**；标注为"`FAST=1` 可省"的（第 7 项的
    `--no-incremental` 复验、YAML 语法校验）按 1.4 的规则执行，不算违反本条。
    任何一项不过都要停下来，不许"大概没问题"就继续。
15. 汇报里的每个结论都必须有对应的命令输出支撑。凡是没实际跑过的检查，**不许写成"通过"**；
    跳过就说"未执行（原因）"，不确定就说"未确认"。
16. **凡是 AI 亲自动过的文件，必须在 `$FIXLOG` 新增段落里逐行列出**，每行只写「路径 — 处理方式」，
    处理方式只有四种：取上游 / 三方合并 / 冲突修正 / 保留本地。**不写冲突细节、不贴代码、不写行号。**
    口径是「AI 动过的文件 ⊆ 账本列出的文件」，不是数量相等——用第 13.3 节的 `/tmp/ai-touched.txt` 逐个 grep。
    **三个记录文件本身、以及 `patches/` 下的产物，不计入这个要求。**
17. 写入的日志文件必须是第 2 步探测到的那个（`fixxed.txt` 优先，其次 `fixed.txt`，再其次递归找），
    不许凭记忆自建一个同名文件。
18. **同步那个 commit 之前，三个记录文件必须都已写完并通过回读校验**（`action.txt` 真比对 SHA、
    `out.txt` 新段在表头下方、`$FIXLOG` 旧内容 `cmp` 一致）。提交之后再改记录，要另开一个 commit，
    **不许 `--amend`**（唯一例外是提交信息里有占位符残留）。
    定制那个 commit 只要求 `$FIXLOG` 的 `[定制]` 段就位——`action.txt` / `out.txt` 在这轮**本来就不该变**，
    它们没进暂存区是正确状态，不是"漏写"。
19. 每次运行开始时，`/tmp/conflict-log.md`、`/tmp/buildfix-log.md`、`/tmp/extra-log.md` 必须清空，
    不许把上一轮的记录当成本次写进账本。
20. **顺序不许倒**：同步（含提交、推送/补丁、三个记录文件）全部完成之前，一行定制代码都不许写。
    我在对话里说"帮我加个功能"不等于"跳过同步"，除非我明确说"这次别同步"。
21. **定制必须是独立 commit**（`feat(custom):`），不许和 `chore(sync):` 合成一个，也不许 `--amend` 同步那个 commit。
    定制阶段**不许改** `action.txt` 和 `out.txt`；`action.txt` 的 `LAST_SYNCED_SHA` 永远只由同步那一步写。
22. **定制也要记账**：`$FIXLOG` 顶部必须有 `[定制]` 段，覆盖每个改动文件；标出哪些是 `[改上游]` 文件、
    下次同步会在哪冲突。没记账就提交＝任务失败（同第 16 条口径）。
23. **定制必须重新构建**，不许拿同步那轮的构建结论当定制的结果；失败同样 3 轮上限，未通过不许提交。
    构建结果写 `CUSTOM_BUILD_RESULT`，**不许回写 `action.txt`**。
24. **不许为了好实现就删/回退刚同步进来的上游代码**，也不许重复实现上游已经有的功能——
    命中就先问我。定制只做我要求的那一件事，不加"顺便优化"。
