# TokenFloat

[![Build](https://github.com/Dream-Tian/TokenFloat/actions/workflows/build.yml/badge.svg)](https://github.com/Dream-Tian/TokenFloat/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Dream-Tian/TokenFloat?display_name=tag)](https://github.com/Dream-Tian/TokenFloat/releases)

TokenFloat 是一个 Windows 桌面悬浮用量面板，从本机日志统计 Codex、Claude Code 和 Gemini CLI 的 Token、请求次数、速率与官方 API 估算费用。

所有日志只在本机读取和汇总，不读取认证文件，不上传对话内容。

## 功能

- 今日、本周（周一至今）、本月用量统计
- 输入、输出、请求次数、平均 RPM/TPM
- Codex、Claude Code、Gemini CLI 分提供商与模型明细
- Token / 费用趋势切换，悬浮显示时段 Token、请求数和金额
- 官方 API 付费层价格估算，区分普通输入、缓存读写和输出
- 双击标题切换迷你模式，普通与迷你窗口分别记住位置
- 系统托盘、开机自启、Codex 桌面应用快捷启动
- 每 30 秒后台刷新，持久索引只重读本月发生变化的日志
- 基于 HTTPS 清单和 SHA-256 校验的自动更新
- 水墨风设置页，集中管理自动检查、更新源和手动更新
- 单实例运行，重复启动时直接唤醒已有窗口
- 更新下载进度、实时速度和取消操作
- 本地错误日志与 14 天自动清理
- 宣纸、水墨、朱印和青绿山水风格界面

## 系统要求

- Windows 10/11 x64
- 源码构建需要 .NET 8 SDK
- Release 安装包为自包含版本，使用时无需另装 .NET Runtime

## 数据来源

- Codex：`%USERPROFILE%\.codex\sessions\**\*.jsonl`
- Claude Code：`%USERPROFILE%\.claude\projects\**\*.jsonl`
- Gemini CLI：`%USERPROFILE%\.gemini\tmp` 和 `%USERPROFILE%\.gemini\history` 中的 `usageMetadata`

索引保存在 `%LOCALAPPDATA%\TokenFloat\usage-index-v5.json.gz`，只包含时间、模型和 Token 数字。

错误日志保存在 `%LOCALAPPDATA%\TokenFloat\logs`，只记录程序版本、异常类型、消息和堆栈，不主动写入日志原文、提示词或认证信息。

## 费用说明

费用按三家官方 API 付费层标准价估算：

- [OpenAI Pricing](https://platform.openai.com/docs/pricing)
- [Anthropic Pricing](https://platform.claude.com/docs/en/about-claude/pricing)
- [Gemini API Pricing](https://ai.google.dev/gemini-api/docs/pricing)

估算值不等同于 NewAPI、订阅、免费额度或云平台实际账单。无法匹配官方价格的第三方模型不会被错误计价，界面使用 `≥` 表示当前金额仍有未计价部分。

## 从源码运行

```powershell
dotnet run --project TokenFloat\TokenFloat.csproj -c Release
```

验证本机统计但不打开窗口：

```powershell
dotnet run --project TokenFloat\TokenFloat.csproj -c Release -- --verify-usage
```

运行自动测试：

```powershell
dotnet test TokenFloat.Tests\TokenFloat.Tests.csproj -c Release -warnaserror
```

## 本地构建安装包

```powershell
dotnet publish TokenFloat\TokenFloat.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\publish\win-x64
.tools\InnoSetup6\ISCC.exe installer\TokenFloat.iss
```

`.tools` 是本地工具目录，不会提交到 GitHub。GitHub Release 工作流会自动安装 Inno Setup。

## GitHub 发布与自动更新

仓库已包含：

- `.github/workflows/build.yml`：每次推送和 Pull Request 自动执行零警告构建与测试
- `.github/workflows/release.yml`：推送 `v*` 标签时自动创建安装包、SHA-256、更新清单和 GitHub Release
- `scripts/set-version.ps1`：同步更新项目与安装程序版本

首次提交和发布步骤见 [GitHub 发布指南](docs/GITHUB_RELEASE.md)。

程序默认更新源为：

```text
https://github.com/Dream-Tian/TokenFloat/releases/latest/download/update-manifest.json
```

托盘菜单仍可覆盖该地址。

## 项目结构

```text
TokenFloat/               WPF 应用源码与资源
TokenFloat.Tests/         用量、费用和更新服务自动测试
installer/                Inno Setup 安装脚本和更新清单示例
scripts/                  版本与图标维护脚本
.github/workflows/        GitHub 持续构建和自动发布
docs/                     发布文档
```
