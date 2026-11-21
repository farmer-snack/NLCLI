<!--markdownlint-disable MD001 MD033 MD041 MD051-->

# ClassIsland（farmer-snack fork）

> 这是 [farmer-snack/NLCLI](https://github.com/farmer-snack/NLCLI) 维护的 **ClassIsland 个人 fork**，基于上游 [ClassIsland/ClassIsland](https://github.com/ClassIsland/ClassIsland) 的 `develop/v2/misha-alpha` 分支，带有一系列本地定制、Bug 修复和构建/调试增强。
>
> **它不是官方版本**，问题与功能以本仓库为准；上游原版的介绍、截图、社区与文档入口见 **[README.upstream.md](./README.upstream.md)**（原 README 已改名保留，内容未改动）。

<div align="center">

[![Stars](https://img.shields.io/github/stars/farmer-snack/NLCLI?label=Stars)](https://github.com/farmer-snack/NLCLI)
[![测试版 Release](https://img.shields.io/github/v/release/farmer-snack/NLCLI?include_prereleases&style=flat-square&label=Release)](https://github.com/farmer-snack/NLCLI/releases)
![.NET](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square)](./LICENSE.txt)

</div>

## 这个 fork 和上游有什么不同

本分支持续跟随上游 `develop/v2/misha-alpha`，但**上游同步时一律保留下列本地定制**（完整清单与合并注意事项记录在 [`fixxed.txt`](./fixxed.txt)）。按大类概览：

| 类别 | 说明 |
|---|---|
| **主界面宽度与提醒排版稳定** | 提醒播放期间主界面只增不减、不被轮播内容撑宽；修复亚像素抖动导致的宽度反复重启，以及异常穿透后提醒队列永久锁死的问题。 |
| **插件兼容层** | 让旧版 Avalonia 插件能在新宿主加载，含插件加载上下文、可选依赖按需下载、旧版本插件与备份数据迁移。 |
| **天气详情** | 接入 Open-Meteo 详情页、本地缓存与请求额度控制，补充天气编码、预报通知与组件。 |
| **提醒语音与音效** | 可开关的提醒朗读与音效（默认关闭），含 EdgeTTS 不可用时的静音处理。 |
| **液态玻璃主题** | 新增可选的 LiquidGlass 主题。 |
| **界面精简** | 移除更新页、关于页、开源水印与反诈提示等上游推广/提示元素。 |
| **托盘图标开关与爱心手势** | 可切换托盘图标，新增爱心手势交互。 |
| **原生库依赖自动修复** | 启动时检查并在缺失时自动下载安装原生库依赖。 |
| **变量调试页增强** | 见下节「开发相关」。 |
| **上游 Issue 修复** | 若干来自上游的缺陷修复（组件、定时任务、课程、诊断等）。 |

## 开发相关：变量调试页

在「设置 → 调试」的**变量调试页**里，这个 fork 额外提供了一组面向源码开发与打包的工具：

- **拉取更新并编译运行**：从写死的本仓库地址拉取最新提交，自动补齐 Git、.NET 10 SDK 与子模块，编译后重启到新版本。找不到本地仓库时，会在 `%LOCALAPPDATA%\ClassIsland\dev-update-source`（Unix 为 `~/.local/share/ClassIsland/dev-update-source`）自动克隆一份，下次复用。
- **打包编译产物**：调用 `dotnet publish`（参数沿用 [`build-publish.bat`](./build-publish.bat)），可选**自包含 / 框架依赖**，打包前清理旧 `out` 目录、打包后删除 `.pdb`，最后生成并打开 `out.zip`。
- **网络加速（优先镜像）**：勾选后 clone / fetch / 探测分支都先走加速镜像，镜像全部失败再回退官方直连，适合直连 GitHub 不畅的网络环境。
- **编译失败自动修复**：两处编译在自身重试耗尽后，会自动尝试安装 BUN 与命令行版 OpenCode，用其免费模型尝试修复代码；失败也不掩盖原始编译错误。
- 分支可切换，也提供「刷新分支」从远端自动探测。

> [!NOTE]
> 上述工具仅在**从源码目录运行**或能自动克隆到固定目录时可用；编译产物落在系统临时目录，`out` 产物落在源码目录，均不影响当前运行的程序。

## 分支与同步

- 本仓库跟踪的分支：`develop/v2/misha-alpha`，上游基线记录在 [`action.txt`](./action.txt) 的 `LAST_SYNCED_SHA`。
- 上游同步流程见仓库内的同步脚本与 `action.txt`；同步时遇到与本地定制冲突的文件，按 [`fixxed.txt`](./fixxed.txt) 对应段落的「下次同步注意」合并，拿不准就停下来人工确认。
- 本 fork 单独发布 Release（含预览版），版本号独立维护，与上游版本不一一对应。

## 下载与安装

- **Release 下载**：[farmer-snack/NLCLI Releases](https://github.com/farmer-snack/NLCLI/releases)（提供 Windows / Linux / Android 等平台产物，具体以 Release 页面为准）。
- 运行环境要求、系统兼容性与安装步骤，参见 **[上游 README](./README.upstream.md)** 与 [ClassIsland 官方文档](https://docs.classisland.tech/app/setup)。
- 从源码编译需要 **.NET 10 SDK**（见 [`global.json`](./global.json)），编译方式见 [`build.sh`](./build.sh) / [`build.ps1`](./build.ps1)，本地编译说明见 [上游开发文档](https://docs.classisland.tech/dev/get-started/development.html)。

## 致谢与许可

- 本项目基于 [ClassIsland/ClassIsland](https://github.com/ClassIsland/ClassIsland)，感谢上游开发者与贡献者；完整的上游致谢、赞助商与第三方依赖清单见 **[README.upstream.md](./README.upstream.md)** 与 [`doc/Dependencies.md`](./doc/Dependencies.md)。
- 各子项目许可见 [`LICENSE.txt`](./LICENSE.txt)：`ClassIsland.Core`、`ClassIsland.PluginSdk`、`ClassIsland.Shared` 等基于 LGPL-3.0，其余部分基于 GPL-3.0。
