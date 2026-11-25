# Codex InputUsage 当前状态

更新于 2026-11-25。独立仓库维护（面向 Windows 原生 Microsoft Store 版 Codex Desktop 的无 CDP 伴随浮层）。

## 核心架构（无 CDP 路线）

产物 `outputs/Codex-InputUsage-Overlay.exe`（.NET Framework 4.x `csc.exe` 零外部依赖构建，约 245 KB）随 Windows 原生 Microsoft Store 版 Codex Desktop 伴随运行，源码全部位于 `src/overlay/`（19 个模块）：

| 模块 | 职责 |
|---|---|
| `src/overlay/CodexIpcMonitor.cs` | 可选只读连接 `\\.\pipe\codex-ipc`（先 `WaitNamedPipe` 快速探测，无管道时退避至 15s 且不触发无效唤醒）；旧版 Codex 有管道时维护完整活跃订阅池，新版 Codex（`26.1117+` 已移除该管道）零开销静默退让 |
| `src/overlay/SessionMatcher.cs` | 优先使用 IPC 订阅池 + 前台 UIA；IPC 不可用时全量匹配本机真实用户会话（自动缓存 `state_5.sqlite`、已剔除 `Guardian review` 等子代理行）：先精确/截断前缀匹配标题（`本地+标题`），再对全部现存 rollout（头 256KB + 尾 2MB 语料缓存，最多 80 个）做严格正文消歧（`本地+正文`）；证据不足时拒绝猜测并清空显示 `--` |
| `src/overlay/CodexStateDb.cs` | 通过系统内置 `winsqlite3.dll` 只读查询 `~/.codex/state_5.sqlite`，精确主键查 `rollout_path` / `name` / `title` / `model` / `reasoning_effort`，列表查询自动排除 `{"subagent"...}` 内部子代理线程 |
| `src/overlay/AccountQuotaReader.cs` | 扫描本机最近写入的 Codex rollout 尾部，提取账号级实时 `rate_limits`（5h / 周额度 + `resets_at` + `credits`），确保查看旧对话时也显示此刻实时余额与重置倒计时 |
| `src/overlay/HistoryStatsReader.cs` | 后台读取 `state_5.sqlite` 真实会话和可用 rollout。rollout 按文件长度与 UTC 写入时间缓存，按响应 ID 去重并用响应时间归桶。缺少明细的会话按 SQLite 活跃时间和总量回退，计入覆盖统计 |
| `src/overlay/OverlaySettings.cs` | 持久化常驻条显示项偏好至 `%LOCALAPPDATA%\CodexInputUsage\settings.json`（含至少保留 1 项的防空保护） |
| `src/overlay/OverlayDpi.cs` | 封装宿主窗口 DPI 缩放与 96 DIP 逻辑坐标 / 物理像素互转工具 |
| `src/overlay/RolloutMetrics.cs` | 增量字节偏移解析 rollout JSONL：解析 `turn_context`（每轮模型/推理档位）与去重统计每轮上下文输入、思考/正文输出、会话累计 Token、缓存命中率、会话加权均速（Avg TPS）、模型短名归一化与额度重置归一；TPS 分母采用 `整轮耗时 - (工具区间并集 - 与模型重叠)`；解析 `world_state`/`response_item`/`additional_tools`/`compacted` 生成十通道上下文拆分并按 Hamilton 最大余数法等比缩放至官方 `ContextUsedTokens` |
| `src/overlay/ComposerDock.cs` | 几何定位（`ComposerDock`）与会话指纹（`ChatFingerprints`）双独立 MTA 线程解耦：几何通道使用 `CacheRequest` 批量预读子节点与一级子节点（异常安全回退）、50ms 纯坐标直更快路径、连续降级 350ms 防抖（30ms 复测）、`ProseMirror` 失败时批量 `Edit` 备用查询及可恢复 UIA 异常重试；指纹通道按已绑定 800ms / 未绑定 300ms 扫描并带最多 3 节点轻量标题变更检测，回写前核验版本、句柄、尺寸与 DPI |
| `src/overlay/HostWindowEvents.cs` | UI 线程进程外 Win32 `SetWinEventHook`：全局监听切窗/最小化（`<1ms` 隐藏与首帧恢复），按 Codex PID 精确监听 `EVENT_OBJECT_LOCATIONCHANGE` 驱动拖动/缩放快路径 |
| `src/overlay/StripForm.cs` | `WS_EX_LAYERED` + `UpdateLayeredWindow` 32 位预乘 Alpha 透明渲染：Segoe UI Variable Text Semibold（带 Segoe UI 回退）、语义着色的 TPS 与矢量闪电、柔和主题蓝本轮/累计 Token 胶囊、语义缓存胶囊和可选 5h 额度胶囊。可见槽位以细竖线分隔，绘制与测量共用布局，支持右键配置与四档窄窗适配，纯平移走 `MoveFast`（`SWP_NOREDRAW` 零重绘） |
| `src/overlay/DetailPopoverForm.cs` | 点击状态条在上方展开监控卡片（三页签共用 `540×608` 逻辑尺寸、12 DIP 外圆角、`WS_EX_NOACTIVATE`）：顶部带矢量图标的「当前会话 / 调用明细 / 用量统计」三页签切换、标题栏模型徽标与「钉住」开关、点击外部/`Esc` 自动收起；会话页含 2×2 指标卡（输出速度与会话平均速度并列）、双列实时额度卡与十通道上下文卡 |
| `src/overlay/DetailPopoverTurns.cs` | 「调用明细」页签的「总汇总」视图绘制与交互：展示平滑会话走势、OLS/LOESS 趋势拟合与置信带、四段 Token 占比模型卡，多模型时支持单击卡片锁定/解除拟合 |
| `src/overlay/DetailPopoverUsage.cs` | 「用量统计」页签绘制与交互：支持今天、1 天、7 天、30 天、全部与自定义 6 档切换及纯 GDI+ 日期步进/滚轮调节，包含 2×3 用量总览、平滑面积趋势图与分项悬停卡片、模型排行及所选时段 Top 5 会话 |
| `src/overlay/OverlayCharts.cs` | 共用水平张力三次贝塞尔曲线、渐变面积和拟合置信带，控制点约束使曲线保持在相邻样本值之间 |
| `src/overlay/SessionAnalytics.cs` | 按模型聚合轮次、四段 Token、加权 TPS、平均首字和缓存命中率。趋势样本较少时使用 OLS，较多时使用高斯核局部线性拟合 |
| `src/overlay/CodexTheme.cs` | 读取 `~/.codex/config.toml` 的 `appearanceTheme`（回退系统主题），提供明暗双主题与十通道上下文色板 |
| `src/overlay/UsageStatsChecks.cs` | 用量统计日期预设、多源防重计归桶、平滑曲线与多 DPI 交互的离线自检逻辑 |
| `src/overlay/Program.cs` | 协调所有子系统、单实例互斥锁、系统托盘、`FileSystemWatcher` rollout 实时监听、`SyncNow` 先落位后匹配与切回异尺寸/异 DPI 暂隐防护、`AppDomain.UnhandledException` 诊断日志、`--exit-with-codex` 自动退出与 `--self-test` 全量自检 |

弹窗统一使用模型短名，例如 `6.2sol · 中`、`6Astra`。解析、会话匹配、历史聚合和诊断使用原始模型 ID。

## 额度显示与 DPI

- 常驻条与详情额度卡共用 `MetricFormatter` 的归一化规则。已有用量且重置时间到达时显示 `100%`，详情显示「已重置」。原始用量与重置时间继续保留，缺用量时显示 `--`。
- 常驻条、三页签弹窗和 UIA 定位边距按宿主 Codex 窗口 DPI 缩放。内部布局与命中检测使用 96 DPI 逻辑坐标，窗口、UIA 矩形和外部点击使用物理像素。宿主 DPI 变化时重新测量并重绘。
- 弹窗使用 12 DIP 外圆角。高度受限时，当前会话、用量统计和调用总汇总允许滚动正文，调用明细的子工具栏固定在页签下方。按轮表格根据可用高度减少可见行数。极窄工作区容不下完整宽度时，弹窗按可用宽度等比收缩。

## 原生伴随启动机制

- 不写 Windows 开机自启注册表（已清理历史 `HKCU\...\Run\CodexInputUsageOverlay` 项）。
- 通过 `scripts/Install-AutoStart.ps1` 在 `~/.codex/config.toml` 注册无工具 stdio MCP 伴随入口 `[mcp_servers.codex_input_usage]` -> `scripts/codex-mcp-companion.mjs`（`tools/list` 返回空数组，零模型上下文开销）。
- 原生图标打开 Codex 时自动拉起 `outputs/Codex-InputUsage-Overlay.exe --exit-with-codex`，Codex 退出 3 秒后浮层自动退出。

## 关键约束与已知限制

- 隔离桌面环境无法直接枚举真实交互桌面 `Default` 的窗口；编译、自检、IPC 抓包和离屏渲染可在命令行完成，真实桌面停靠/拖动/点击验收需在用户交互桌面执行。
- 早期 CDP 路线历史原型已归档至 `legacy/cdp/`（仅作口径参考，不再维护）。

## 构建与常用命令

- 构建：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1`
- 离线自检与预览渲染：`outputs/Codex-InputUsage-Overlay.exe --self-test [<rollout.jsonl>]`（报告与预览输出到 `work/`）
- 安装/卸载随原生 Codex 启动：`scripts/Install-AutoStart.ps1` / `scripts/Uninstall-AutoStart.ps1`
