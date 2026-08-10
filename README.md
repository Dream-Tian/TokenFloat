# TokenFloat

[![Build](https://github.com/Dream-Tian/TokenFloat/actions/workflows/build.yml/badge.svg)](https://github.com/Dream-Tian/TokenFloat/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Dream-Tian/TokenFloat?display_name=tag)](https://github.com/Dream-Tian/TokenFloat/releases/latest)

TokenFloat 是一个 Windows 桌面悬浮用量面板。它从 NewAPI 读取个人消费汇总，在一个轻量的圆角仪表盘中展示 Token、请求次数、速率、模型排行和估算消耗。

## 界面预览

主界面提供总览、趋势和模型三个标签页；设置页集中管理 NewAPI、刷新策略和本地数据。

<p align="center">
  <img src="docs/images/dashboard.png" alt="TokenFloat 主界面" width="600" />
</p>

<p align="center">
  <img src="docs/images/settings.png" alt="TokenFloat 设置页" width="430" />
</p>

## 功能

### 用量分析

- 今日、本周、本月统计，周统计从周一开始计算。
- 任意起止日期统计，并提供近 7 天、近 30 天快捷范围。
- 输入 Token、输出 Token、请求次数、平均 RPM/TPM。
- Token 和消耗趋势切换，悬停图表可查看具体时段数据。
- 今日、本周、本月与上一周期相同进度的用量比较。
- 模型排行榜，展示 Token、请求次数、消耗占比和输入/输出比例。
- NewAPI `quota` 与 `/api/status` 的 `quota_per_unit` 换算估算消耗。

### 刷新与缓存

- 刷新间隔可设置为 10 秒、30 秒、1 分钟或 5 分钟。
- 可选择仅窗口可见时刷新。
- 首次刷新、缓存缺失或缓存超过 7 天未刷新时读取完整历史。
- 常规刷新以最近一次成功刷新时间为基准，只读取附近的增量数据，并保留 2 小时重叠区间，避免当前小时汇总更新造成遗漏。
- 设置页显示本次刷新耗时，可手动清除统计缓存并重建。

### 桌面体验

- 普通模式和迷你模式分别保存窗口位置。
- 双击标题栏切换迷你模式；标题栏整行空白区域也可以拖动窗口。
- 迷你模式显示今日 Token 和今日消耗，并对 Token 新增量播放上浮渐隐动画。
- 系统托盘、单实例运行和可选开机自启。
- 圆角浅色仪表盘界面，设置页使用自定义滚动条。

### 更新与诊断

- 设置页可以测试 NewAPI 连接，显示 HTTP 结果和请求耗时；测试不会保存当前输入内容。
- 支持 HTTPS 更新清单和 SHA-256 安装包校验。
- 下载更新时显示进度、速度，并支持取消。
- 本地错误日志自动清理 14 天前的记录。

## 安装

从 [Releases](https://github.com/Dream-Tian/TokenFloat/releases/latest) 下载 `TokenFloat-Setup-x64.exe`，运行安装器并选择安装目录即可。Release 安装包是自包含版本，不需要另外安装 .NET Runtime。

首次启动后，点击主界面右上角的设置图标，在 NewAPI 区域填写服务地址和系统 Token。建议先点击“测试连接”，确认成功后再保存。

## NewAPI 配置

设置页的 NewAPI 区域包含以下字段：

| 字段 | 说明 |
| --- | --- |
| 服务地址 | NewAPI 根地址，例如 `https://new-api.example.com/`。程序会请求 `/api/status` 和 `/api/data/self`。 |
| 系统 Token | 用于访问个人消费数据的 Bearer Token。 |
| 用户 ID | 可选；只有服务端要求 `New-Api-User` 请求头时才填写。 |

“测试连接”只请求 `/api/status`，不会写入设置文件。点击“保存”后，程序会清除旧的来源缓存并立即刷新统计。

默认完整读取范围覆盖本月和上月。自定义日期早于默认范围时，程序会从指定日期执行完整读取；普通定时刷新仍使用增量策略。

## 数据与隐私

TokenFloat 使用以下接口：

- `GET /api/status`：读取 `quota_per_unit`。
- `GET /api/data/self`：读取个人消费记录，并使用返回的模型、Token、次数和 `quota` 汇总。

本地数据目录默认为 `%LOCALAPPDATA%\TokenFloat`：

| 文件或目录 | 用途 |
| --- | --- |
| `app-settings.json` | NewAPI 地址、刷新设置和更新源。系统 Token 使用 Windows DPAPI 按当前用户范围加密，旧版明文配置会在首次读取时自动迁移。 |
| `usage-index-v7.json.gz` | 压缩统计缓存，包含事件索引、汇总和最近成功抓取时间，不保存提示词或响应正文。 |
| `logs` | 程序错误日志，记录上下文、异常类型、消息和堆栈。 |

统计金额是基于 NewAPI quota 的估算值。若服务没有返回 `quota_per_unit`，程序使用 `500000` 作为默认换算值；最终账单以你的 NewAPI 部署为准。

## 从源码运行

环境要求：

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

在仓库根目录执行：

```powershell
dotnet restore
dotnet build TokenFloat\TokenFloat.csproj -c Release -warnaserror
dotnet run --project TokenFloat\TokenFloat.csproj -c Release
```

只验证统计输出、不打开窗口：

```powershell
dotnet run --project TokenFloat\TokenFloat.csproj -c Release -- --verify-usage
```

运行自动测试：

```powershell
dotnet test TokenFloat.Tests\TokenFloat.Tests.csproj -c Release -warnaserror
```

## 本地构建安装包

仓库提供了本地 Inno Setup 工具目录；`.tools` 不会提交到 GitHub。

```powershell
dotnet publish TokenFloat\TokenFloat.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o artifacts\publish\win-x64

.tools\InnoSetup6\ISCC.exe installer\TokenFloat.iss
```

安装包输出到 `artifacts\installer\TokenFloat-Setup-x64.exe`。要安装到指定目录，可以使用 Inno Setup 的 `/DIR` 参数，例如：

```powershell
Start-Process artifacts\installer\TokenFloat-Setup-x64.exe `
  -ArgumentList '/DIR=D:\TokenFloat'
```

## 发布流程

版本号同时维护在项目文件、Inno Setup 脚本和示例更新清单中。发布前还需要把 `CHANGELOG.md` 的“未发布”内容改为带日期的版本章节，例如 `## [2.0.6] - 2026-08-10`；标签对应的版本章节缺失或为空时，发布工作流会直接失败。

确认更新日志后，同步版本并执行验证：

```powershell
.\scripts\set-version.ps1 -Version 2.0.6
dotnet build TokenFloat\TokenFloat.csproj -c Release -warnaserror
dotnet test TokenFloat.Tests\TokenFloat.Tests.csproj -c Release -warnaserror

git add -A
git commit -m "Release 2.0.6"
git push origin main
git tag -a v2.0.6 -m "TokenFloat 2.0.6"
git push origin v2.0.6
```

推送 `v*` 标签后，GitHub Actions 会自动执行测试、从 `CHANGELOG.md` 提取对应版本说明、构建自包含安装包、生成 SHA-256 清单并创建 GitHub Release。更新清单使用适合客户端显示的单行变更摘要，GitHub Release 保留完整 Markdown 结构。相关工作流位于：

- `.github/workflows/build.yml`：推送和 Pull Request 的构建与测试。
- `.github/workflows/release.yml`：标签发布、安装包和更新清单。
- `scripts/set-version.ps1`：同步项目版本、安装器版本和示例清单。
- `scripts/get-release-notes.ps1`：提取指定版本的发布说明，并校验版本章节存在且非空。

## 常见问题

### 测试连接失败

确认服务地址是完整的 `http://` 或 `https://` 地址，Token 没有多余空格，并检查服务端是否要求填写用户 ID。设置页状态会显示 HTTP 状态或请求失败原因。

### 有数据但刷新后仍显示旧统计

打开设置页的本地数据区域，点击“清除统计缓存”后重新读取。缓存超过 7 天或更改 NewAPI 来源后也会自动执行完整读取。

### 金额和账单不一致

TokenFloat 使用 NewAPI 返回的 `quota` 和 `quota_per_unit` 计算估算金额。请检查服务端的分组倍率、模型倍率和补全倍率，最终账单以 NewAPI 为准。

## 项目结构

```text
TokenFloat/               WPF 应用源码、窗口和资源
TokenFloat.Tests/         用量、费用、连接和更新服务测试
installer/                Inno Setup 脚本和更新清单示例
scripts/                  版本与图标维护脚本
docs/                     发布文档和 README 截图
.github/workflows/        GitHub Actions 构建与发布流程
```
