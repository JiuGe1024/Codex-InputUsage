# Codex InputUsage 当前状态

更新于 2026-11-21。独立仓库目录：`Codex-InputUsage`（与父目录原版 TokenBar 分开维护）。

## 核心架构（无 CDP 路线）

产物 `outputs/Codex-InputUsage-Overlay.exe`（.NET Framework 4.x `csc.exe` 零外部依赖构建，124 KB）随 Windows 原生 Microsoft Store 版 Codex Desktop 伴随运行，源码全部位于 `src/overlay/`：

| 模块 | 职责 |
|---|---|
| `src/overlay/CodexIpcMonitor.cs` | 只读连接 `\\.\pipe\codex-ipc`（4 字节 LE 帧 + UTF-8 JSON），维护完整活跃订阅池（按 `(sourceClientId, hostId, conversationId)` 去重），并在路由变更时即时触发回调 |
| `src/overlay/SessionMatcher.cs` | 优先使用 IPC 订阅池与前台 UIA 标题或正文；IPC 不可用时匹配本地会话的唯一精确标题或至少两段可见正文。仅有正文时最多检查最近 12 个会话。证据不足时显示 `--` |
| `src/overlay/CodexStateDb.cs` | 通过系统内置 `winsqlite3.dll` 只读查询 `~/.codex/state_5.sqlite`，精确主键查 `rollout_path` / `name` / `title` / `model` / `reasoning_effort`，不做最近聊天猜测 |
| `src/overlay/AccountQuotaReader.cs` | 扫描本机最近写入的 Codex rollout 尾部，提取账号级实时 `rate_limits`（5h / 周额度 + `resets_at` + `credits`），确保查看旧对话时也显示此刻实时余额与重置倒计时 |
| `src/overlay/RolloutMetrics.cs` | 增量字节偏移解析 rollout JSONL：去重统计每轮与会话累计 Token、缓存命中率、会话加权均速（Avg TPS）；TPS 分母采用 `整轮耗时 - (工具区间并集 - 与模型重叠)`；解析 `world_state`/`response_item`/`additional_tools`/`compacted` 生成十通道上下文拆分并按 Hamilton 最大余数法等比缩放至官方 `ContextUsedTokens` |
| `src/overlay/ComposerDock.cs` | 独立 MTA 线程通过 UIA 定位 `ProseMirror -> composer -> Context 圆环 / 模型选择器 / 左侧按钮簇`，支持单次快速重读、`AutoResetEvent` 即时唤醒与切窗锚点缓存 |
| `src/overlay/HostWindowEvents.cs` | UI 线程进程外 Win32 `SetWinEventHook`：全局监听切窗/最小化（`<1ms` 隐藏与首帧恢复），按 Codex PID 精确监听 `EVENT_OBJECT_LOCATIONCHANGE` 驱动拖动/缩放快路径 |
| `src/overlay/StripForm.cs` | `WS_EX_LAYERED` + `UpdateLayeredWindow` 32 位预乘 Alpha 透明渲染：矢量琥珀金闪电 + `tok/s` + 本轮/累计 Token + `缓存 [本轮% / 累计%]` 语义柔色微胶囊（整数），6 档窄窗自适应，纯平移走 `MoveFast`（`SWP_NOREDRAW` 零重绘） |
| `src/overlay/DetailPopoverForm.cs` | 点击状态条在上方展开监控卡片（`WS_EX_NOACTIVATE`）：标题栏含模型/推理档位徽标与「钉住」开关；支持点击外部/`Esc` 自动收起；含 2×2 指标卡（含会话均速与两位小数缓存率）、账号级双列实时额度卡（5h/周进度条与倒计时）、十通道上下文窗口卡片、带可视拖拽滚动条及行精确明细条的逐轮表 |
| `src/overlay/CodexTheme.cs` | 读取 `~/.codex/config.toml` 的 `appearanceTheme`（回退系统主题），提供明暗双主题与十通道上下文色板 |
| `src/overlay/Program.cs` | 协调所有子系统、单实例互斥锁、系统托盘、`FileSystemWatcher` rollout 实时监听、`--exit-with-codex` 自动退出与 `--self-test` 自检 |

## 原生伴随启动机制

- 不写 Windows 开机自启注册表（已清理历史 `HKCU\...\Run\CodexInputUsageOverlay` 项）。
- 通过 `scripts/Install-AutoStart.ps1` 在 `~/.codex/config.toml` 注册无工具 stdio MCP 伴随入口 `[mcp_servers.codex_input_usage]` -> `scripts/codex-mcp-companion.mjs`（`tools/list` 返回空数组，零模型上下文开销）。
- 原生图标打开 Codex 时自动拉起 `outputs/Codex-InputUsage-Overlay.exe --exit-with-codex`，Codex 退出 3 秒后浮层自动退出。

## 关键约束与已知限制

- 代理工具终端运行在隔离桌面 `exebox-...`，无法直接枚举真实交互桌面 `Default` 的窗口；编译、自检、IPC 抓包和离屏渲染可在终端完成，真实桌面停靠/拖动/点击验收需在用户交互桌面执行。
- 仓库根目录遗留的未跟踪 `HANDOFF.md` 已过时，当前事实以本文件与 Git 提交记录为准。

## 构建与常用命令

- 构建：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1`
- 离线自检与预览渲染：`outputs/Codex-InputUsage-Overlay.exe --self-test [<rollout.jsonl>]`（报告与预览输出到 `work/`）
- 安装/卸载随原生 Codex 启动：`scripts/Install-AutoStart.ps1` / `scripts/Uninstall-AutoStart.ps1`
