# Codex InputUsage 当前状态

更新于 2026-11-22。独立仓库目录：`Codex-InputUsage`（与父目录原版 TokenBar 分开维护）。

## 核心架构（无 CDP 路线）

产物 `outputs/Codex-InputUsage-Overlay.exe`（.NET Framework 4.x `csc.exe` 零外部依赖构建）随 Windows 原生 Microsoft Store 版 Codex Desktop 伴随运行，源码全部位于 `src/overlay/`：

| 模块 | 职责 |
|---|---|
| `src/overlay/CodexIpcMonitor.cs` | 可选只读连接 `\\.\pipe\codex-ipc`（先 `WaitNamedPipe` 快速探测，无管道时退避至 15s 且不触发无效唤醒）；旧版 Codex 有管道时维护完整活跃订阅池，新版 Codex（`26.1117+` 已移除该管道）零开销静默退让 |
| `src/overlay/SessionMatcher.cs` | 优先使用 IPC 订阅池 + 前台 UIA；IPC 不可用时全量匹配本机真实用户会话（自动缓存 `state_5.sqlite`、已剔除 `Guardian review` 等子代理行）：先精确/截断前缀匹配标题（`本地+标题`），再对全部现存 rollout（头 256KB + 尾 2MB 语料缓存，最多 80 个）做严格正文消歧（`本地+正文`）；证据不足时拒绝猜测并清空显示 `--` |
| `src/overlay/CodexStateDb.cs` | 通过系统内置 `winsqlite3.dll` 只读查询 `~/.codex/state_5.sqlite`，精确主键查 `rollout_path` / `name` / `title` / `model` / `reasoning_effort`，列表查询自动排除 `{"subagent"...}` 内部子代理线程 |
| `src/overlay/AccountQuotaReader.cs` | 扫描本机最近写入的 Codex rollout 尾部，提取账号级实时 `rate_limits`（5h / 周额度 + `resets_at` + `credits`），确保查看旧对话时也显示此刻实时余额与重置倒计时 |
| `src/overlay/HistoryStatsReader.cs` | 直读 `state_5.sqlite` 真实会话表（按 DB/WAL 时间戳与本地日期缓存，毫秒级），聚合今日与近 7 日 Token、活跃会话数、7 日柱状趋势、按模型分布与 Top 5 高消耗会话 |
| `src/overlay/OverlaySettings.cs` | 持久化常驻条显示项偏好至 `%LOCALAPPDATA%\CodexInputUsage\settings.json`（含至少保留 1 项的防空保护） |
| `src/overlay/RolloutMetrics.cs` | 增量字节偏移解析 rollout JSONL：解析 `turn_context`（每轮模型/推理档位）与去重统计每轮上下文输入、思考/正文输出、会话累计 Token、缓存命中率、会话加权均速（Avg TPS）；TPS 分母采用 `整轮耗时 - (工具区间并集 - 与模型重叠)`；解析 `world_state`/`response_item`/`additional_tools`/`compacted` 生成十通道上下文拆分并按 Hamilton 最大余数法等比缩放至官方 `ContextUsedTokens` |
| `src/overlay/ComposerDock.cs` | 独立 MTA 线程通过 UIA 定位 `ProseMirror -> composer -> Context 圆环 / 模型选择器 / 左侧按钮簇`，支持单次快速重读、`AutoResetEvent` 即时唤醒与切窗锚点缓存 |
| `src/overlay/HostWindowEvents.cs` | UI 线程进程外 Win32 `SetWinEventHook`：全局监听切窗/最小化（`<1ms` 隐藏与首帧恢复），按 Codex PID 精确监听 `EVENT_OBJECT_LOCATIONCHANGE` 驱动拖动/缩放快路径 |
| `src/overlay/StripForm.cs` | `WS_EX_LAYERED` + `UpdateLayeredWindow` 32 位预乘 Alpha 透明渲染：矢量琥珀金闪电 + 可配置槽位（TPS / 本轮 / 累计 / 语义缓存微胶囊 / 可选 `5h XX%` 实时额度微胶囊），右键菜单微调并多档窄窗自适应，纯平移走 `MoveFast`（`SWP_NOREDRAW` 零重绘） |
| `src/overlay/DetailPopoverForm.cs` | 点击状态条在上方展开监控卡片（`540px` 宽、`WS_EX_NOACTIVATE`）：顶部带矢量图标的「当前会话 / 逐轮明细 / 今日与 7 日汇总」三页签切换、标题栏模型徽标与「钉住」开关、点击外部/`Esc` 自动收起；会话页含 2×2 指标卡（输出速度与会话平均速度并列）、双列实时额度卡与十通道上下文卡；逐轮明细页含模型/速度/首字/上下文-思考-正文/Token 消耗/缓存表格与可拖拽滚动条；汇总页含今日/7 日双卡、可悬停查精确值的 7 日柱状图、按模型分布与近 7 日 Top 5 会话 |
| `src/overlay/CodexTheme.cs` | 读取 `~/.codex/config.toml` 的 `appearanceTheme`（回退系统主题），提供明暗双主题与十通道上下文色板 |
| `src/overlay/Program.cs` | 协调所有子系统、单实例互斥锁、系统托盘、`FileSystemWatcher` rollout 实时监听、`--exit-with-codex` 自动退出与 `--self-test` 自检 |

## 额度显示与 DPI

- 常驻条与详情额度卡共用 `MetricFormatter` 的归一化规则。已有用量且重置时间到达时显示 `100%`，详情显示「已重置」。原始用量与重置时间继续保留，缺用量时显示 `--`。
- 常驻条、三页签弹窗和 UIA 定位边距按宿主 Codex 窗口 DPI 缩放。内部布局与命中检测使用 96 DPI 逻辑坐标，窗口、UIA 矩形和外部点击使用物理像素。宿主 DPI 变化时重新测量并重绘。
- 弹窗使用 12 DIP 外圆角。高度受限时，当前会话和汇总保留顶部页签并允许滚动正文，逐轮页按可用高度减少可见行数。极窄工作区容不下完整宽度时，弹窗按可用宽度等比收缩。

## 原生伴随启动机制

- 不写 Windows 开机自启注册表（已清理历史 `HKCU\...\Run\CodexInputUsageOverlay` 项）。
- 通过 `scripts/Install-AutoStart.ps1` 在 `~/.codex/config.toml` 注册无工具 stdio MCP 伴随入口 `[mcp_servers.codex_input_usage]` -> `scripts/codex-mcp-companion.mjs`（`tools/list` 返回空数组，零模型上下文开销）。
- 原生图标打开 Codex 时自动拉起 `outputs/Codex-InputUsage-Overlay.exe --exit-with-codex`，Codex 退出 3 秒后浮层自动退出。

## 关键约束与已知限制

- 代理工具终端运行在隔离桌面 `exebox-...`，无法直接枚举真实交互桌面 `Default` 的窗口；编译、自检、IPC 抓包和离屏渲染可在终端完成，真实桌面停靠/拖动/点击验收需在用户交互桌面执行。
- 早期 CDP 路线历史原型已归档至 `legacy/cdp/`（仅作口径参考，不再维护）；过时交接文档 `HANDOFF.md` 已清理。

## 构建与常用命令

- 构建：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1`
- 离线自检与预览渲染：`outputs/Codex-InputUsage-Overlay.exe --self-test [<rollout.jsonl>]`（报告与预览输出到 `work/`）
- 安装/卸载随原生 Codex 启动：`scripts/Install-AutoStart.ps1` / `scripts/Uninstall-AutoStart.ps1`
