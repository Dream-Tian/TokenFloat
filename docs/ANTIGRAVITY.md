# Antigravity 用量与配额

TokenFloat 从正在运行的 Antigravity IDE 本地服务读取模型调用计数和当前配额。它可以独立使用，不需要配置 NewAPI。当前接入基于已验证的 IDE 本地接口，接口及可读取字段可能随版本变化。

## 开始使用

1. 启动并登录 Antigravity IDE，打开需要统计的会话。较早的会话可能需要先在 IDE 中打开、加载。
2. 在 TokenFloat 设置的“本地 Antigravity”区域点击“测试配额”，检查连接。测试不会改变已保存的启用开关。
3. 勾选“读取 Antigravity IDE 用量和配额”，点击“保存并刷新”。
4. 在总览、趋势、模型页和迷你窗口查看已读取的实际 Token，在“配额”页查看模型剩余比例和重置时间。

有配额不一定有完整 Token 数据：配额和模型调用记录来自不同接口。遇到缺字段或读取不完整时，查看来源提示，不要将缺失记录当成零消耗。

## 统计范围与口径

范围是本机服务**当前可读取、已加载的会话**，不是账号全部历史。采集会合并可发现的本地服务返回的记录，并对带相同响应标识的模型调用去重。未打开的历史会话、其他设备的记录以及服务没有返回的数据，不会被自动补齐。

一次模型调用计为一次请求，并按调用的真实时间归入本地日期。一次用户提问可能触发多次模型调用，因此请求数不等于聊天消息数。

| 项目 | 统计方式 |
| --- | --- |
| 原始输入 | 接口的 `usage.inputTokens`，保留在事件的 `ReportedInputTokens` 中。 |
| 总输入 | 原始输入 + `cacheReadTokens` + `cacheWriteTokens`，用于面板输入及总量统计。 |
| 输出 | 接口的 `usage.outputTokens`，已包含思考部分，不再加上 `thinkingOutputTokens` 等分项。 |
| 总 Token | 总输入 + 输出。缓存读写只计入总输入一次。 |
| 模型配额 | 独立展示剩余比例、重置时间及接口提供的套餐积分，不从这些数值推算 Token。 |
| 金额 | Antigravity 保留为“未计价”，不按公开模型单价推算订阅费用；混合来源以 `≥` 标明金额只覆盖有计费信息的请求。 |

缺少有效 `usage`、真实调用时间或出现无效计数时，对应记录不会进入已验证总量，界面会提示不完整。`IsComplete = true` 只表示本轮返回的可见集合完成了读取，不表示覆盖官方账户账单或所有历史。

## 刷新与缓存

Antigravity 与 NewAPI 分别维护缓存，保存来源开关不会清空另一来源的数据。Antigravity 空闲且未变化的会话会复用本次运行内的扫描结果，减少重复请求。

| 本轮结果 | 显示与保存行为 |
| --- | --- |
| 完整读取 | 显示当前可读取的会话集合，替换独立的成功缓存；完整空集合也会替换旧结果。 |
| 部分读取或字段未提供 | 只显示本轮已验证记录并标注不完整，不覆盖上次成功缓存，也不将旧记录混入本轮结果。 |
| 本地服务或会话列表不可用 | 有成功缓存时显示明确标注的本机历史快照及读取时间；没有缓存时显示不可用状态。 |

成功缓存位于 `%LOCALAPPDATA%\TokenFloat\antigravity-usage-v1.json.gz`。它是可见会话的快照，不是永久累计台账；结果可能随 IDE 已加载的会话集合变化。选择更早的日期只筛选已取得的记录，不能强制 IDE 提供尚未加载的历史。

设置页的缓存占用包含两种来源。“清除统计缓存”会清除并重新读取两者；若正在刷新，会等当前读取结束再清理。重新读取仍受本地服务的可见范围限制。

## 命令行诊断

在 Windows 上安装 .NET 8 SDK 后，于仓库根目录执行：

```powershell
dotnet run --project TokenFloat/TokenFloat.csproj -c Release -- --verify-antigravity
```

此命令直接查询本地 IDE，不改变来源启用开关，不写入统计缓存，也不打开 TokenFloat 窗口。输出 JSON 只包含配额、状态和聚合计数：

| 字段 | 含义 |
| --- | --- |
| `Quota` / `QuotaMessage` | 当前配额和配额接口状态。 |
| `UsageMessage` / `IsComplete` | 本轮可读取范围、异常及读取是否完整。 |
| `IsUnavailable` | 是否无法取得本地会话列表；与“列表可用但缺少有效计数”区分。 |
| `Records` | 去重后的模型调用记录数。 |
| `ReportedInputTokens` / `InputTokens` | 原始输入合计 / 包含缓存读写的总输入合计。 |
| `OutputTokens` / `CacheReadTokens` / `CacheWriteTokens` | 输出及缓存分项合计；不能再把缓存分项加到 `InputTokens` 上。 |

按已经保存的来源配置验证整个面板统计：

```powershell
dotnet run --project TokenFloat/TokenFloat.csproj -c Release -- --verify-usage
```

此命令会刷新成功缓存，并输出周期总量、趋势、模型、费用以及 `AntigravityUsageMessage`、`AntigravityUsageIsComplete`、配额等信息。

## 本地接口与隐私

采集仅访问匹配 Antigravity Language Server 进程的回环监听端口，使用以下本地 RPC：

- `GetUserStatus`：读取套餐和模型配额。
- `GetAllCascadeTrajectories`：枚举服务当前返回的会话。
- `GetCascadeTrajectoryGeneratorMetadata`：分页读取模型调用元数据，设置 `includeMessages: false`。

TokenFloat 不请求消息正文来估算 Token，也不重复累计步骤级别的用量。CSRF 凭证仅在内存中用于本地请求；统计缓存保存哈希事件标识、模型、时间、原始输入和规范化后的计数，不保存账号、提示词、响应正文或原始采样响应。该接入不依赖官方云端账单 API。

## 实测记录

2026-09-14 在 **Antigravity IDE 2.5.5** 上验证了本地配额和真实用量读取。以下是当时可读取集合的脱敏聚合，不代表测试账号全部历史：

| 项目 | 数值 |
| --- | ---: |
| 返回模型配额 | 14 个 |
| 模型调用记录 | 79 条 |
| 原始输入 | 741,759 |
| 缓存读取 | 8,236,870 |
| 缓存写入 | 0 |
| 总输入 | 8,978,629 |
| 输出 | 107,765 |
| 总 Token | 9,086,394 |

文档仅保留聚合结果，不包含账号、实际目录、会话内容或原始采样响应。其他 IDE 版本需以实际返回的字段和完整性状态为准。

[返回 README](../README.md)
