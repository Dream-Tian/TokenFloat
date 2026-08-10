# TokenFloat

[![Build](https://github.com/Dream-Tian/TokenFloat/actions/workflows/build.yml/badge.svg)](https://github.com/Dream-Tian/TokenFloat/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Dream-Tian/TokenFloat?display_name=tag)](https://github.com/Dream-Tian/TokenFloat/releases)

TokenFloat 是一个 Windows 桌面悬浮用量面板，从 NewAPI 读取个人消费日志，汇总显示 Token、请求次数、速率和总消耗。

程序不再扫描本机 Codex、Claude Code、Gemini CLI 日志，也不按这些工具拆分展示消耗。

## 功能

- 今日、本周（周一至今）、本月用量统计
- 输入、输出、请求次数、平均 RPM/TPM
- NewAPI 总消耗展示，按日志 quota 和 `quota_per_unit` 换算
- Token / 消耗趋势切换，悬浮显示时段 Token、请求数和金额
- 今日、本周、本月与上一周期相同进度的用量比较
- 任意起止日期统计，并提供近 7 天和近 30 天快捷范围
- 模型排行榜，展示 Token、次数、消耗占比和输入/输出比例
- 双击标题切换迷你模式，普通与迷你窗口分别记住位置
- 迷你模式实时显示 Token 新增量的上浮渐隐动画
- 系统托盘和开机自启
- 10 秒至 5 分钟可配置刷新，可选择仅窗口可见时刷新
- 常规刷新只请求最近增量数据，并与本地压缩缓存合并；缓存缺失、过旧或自定义历史范围时自动完整读取
- NewAPI 设置页，集中管理服务地址、系统 Token、刷新和本地缓存/错误日志
- 设置页支持“测试连接”，并显示最近一次统计刷新的耗时
- 基于 HTTPS 清单和 SHA-256 校验的自动更新
- 单实例运行，重复启动时直接唤醒已有窗口
- 更新下载进度、实时速度和取消操作
- 本地错误日志与 14 天自动清理
- 浅色圆角仪表盘界面，普通模式与迷你模式适配不同使用场景

## 系统要求

- Windows 10/11 x64
- 源码构建需要 .NET 8 SDK
- Release 安装包为自包含版本，使用时无需另装 .NET Runtime

## NewAPI 配置

在“设置...”页面填写：

- NewAPI 服务地址，例如 `https://new-api.example.com/`
- 系统 Token，用于访问 `/api/data/self` 和 `/api/status`
- 可选用户 ID，需要兼容部分 NewAPI 部署的 `New-Api-User` 请求头时再填写

读取范围默认覆盖本月和上月。常规定时刷新会复用缓存，只从上次成功刷新时间附近拉取增量；缓存不存在、超过 7 天未刷新或自定义日期范围需要更早数据时，会按需执行完整读取。设置页的“测试连接”只请求 `/api/status`，不会保存输入内容。

## 数据来源

TokenFloat 调用 NewAPI 的个人消费数据接口 `/api/data/self` 读取消费记录，并使用返回的 Token、次数、模型和 `quota` 汇总展示。

压缩缓存保存在 `%LOCALAPPDATA%\TokenFloat\usage-index-v7.json.gz`，只包含统计周期、模型、Token、次数、quota 和最近一次成功抓取时间，不保存提示词或响应正文。

错误日志保存在 `%LOCALAPPDATA%\TokenFloat\logs`，只记录程序版本、异常类型、消息和堆栈，不主动写入提示词或认证信息。

## 消耗说明

NewAPI 的 quota 通常按“`额度 = 分组倍率 * 模型倍率 * 补全倍率 * token 数量`”计算，TokenFloat 优先使用日志中的 quota，再除以服务 `/api/status` 返回的 `quota_per_unit` 换算为消耗金额。

如果服务没有返回 `quota_per_unit`，程序使用 NewAPI 默认的 `500000` 作为换算值。实际账单仍以你的 NewAPI 部署为准。

## 从源码运行

```powershell
dotnet run --project TokenFloat\TokenFloat.csproj -c Release
```

验证 NewAPI 统计但不打开窗口：

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

右键菜单的“设置...”页面可以覆盖该地址。

## 项目结构

```text
TokenFloat/               WPF 应用源码与资源
TokenFloat.Tests/         用量、费用和更新服务自动测试
installer/                Inno Setup 安装脚本和更新清单示例
scripts/                  版本与图标维护脚本
.github/workflows/        GitHub 持续构建和自动发布
docs/                     发布文档
```
