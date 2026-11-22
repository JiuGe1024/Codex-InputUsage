# Codex InputUsage 任务清单

更新于 2026-11-22。

## 已完成

- [x] 排查并解决无 CDP 浮层实机可见性问题（隔离桌面边界识别、前台 HWND 直接反查、`SetWindowPos(HWND_TOPMOST)`、现场诊断日志与截图）
- [x] 重构为模块化 `src/overlay/` 架构，实现输入框底栏透明无缝停靠（参考 `dongyue-19/codex-statusbar`）— `acfd0d8`
- [x] 补齐常驻四指标（TPS + 本轮 Token + 会话累计 + 缓存率）与详情面板逐轮明细 — `acfd0d8`
- [x] 修复 Codex 多活跃流 `\\.\pipe\codex-ipc` 错配：保留完整订阅池 + 前台 UIA 标题/正文消歧，严禁猜测回退 — `ea4732a`
- [x] 修正 TPS 分母口径为整轮耗时扣除工具执行时间并保留模型重叠（与 `src/usage-state.mjs` 一致）— `673b2dd`
- [x] 常驻条增加本轮缓存率并升级为语义柔色胶囊 `[本轮% / 累计%]`（整数），展开面板所有缓存率保留两位小数 — `37d55e1`、`eb9c956`
- [x] 详情面板升级为十通道上下文窗口卡片（解析 `world_state`/`response_item`/`additional_tools`/`compacted`，Hamilton 精确缩放至官方值）— `eb9c956`
- [x] 改用 Codex 原生无工具 MCP 伴随启动替代 Windows 开机自启注册表，并升级矢量琥珀金闪电图标 — `f5515b3`
- [x] 引入 Win32 `SetWinEventHook`、拖动零重绘快路径、失焦锚点缓存、`AutoResetEvent` 唤醒与 rollout `FileSystemWatcher` — `592f1dc`
- [x] 账号级实时余额进度卡 + 重置倒计时（`AccountQuotaReader`，修复旧对话显示过期余额）
- [x] 详情面板交互打磨（点击外部 / `Esc` 自动收起、标题栏「钉住」开关、可视化拖拽滚动条、悬停/点击行精确明细条）
- [x] 标题栏模型与推理档位徽标 + 速度卡会话均速（Avg TPS）
- [x] 修复 Codex `26.1117+` 移除 `\\.\pipe\codex-ipc` 后的本地全会话匹配（SQLite 过滤 500+ `Guardian review` 子代理行、UIA 顶栏支持 `Button`/`Edit` 标题、放开本地全量 rollout 正文池与截断标题前缀匹配、静默退避 IPC 探测、修复详情页 `本地+标题`/`本地+正文` 绿色绑定徽标）— `d485f79`
- [x] 面板顶部 Tab 切换：「当前会话」 / 「今日与 7 日汇总」（`HistoryStatsReader` 直读 `state_5.sqlite`，含今日/7 日双卡、交互式 7 日柱状图、按模型分布与 Top 5 会话）— `8801c99`
- [x] 常驻条显示项右键微调（`OverlaySettingsStore` 持久化至 `%LOCALAPPDATA%\CodexInputUsage\settings.json`，支持 TPS / 本轮 / 累计 / 缓存 / `5h 配额余量` 微胶囊独立开关与恢复默认）— `8801c99`
- [x] 拆分独立「逐轮明细」页签、补齐页签矢量图标、速度卡并列显示会话平均速度及逐轮上下文/思考/正文与 Token 消耗列 — `a84919c`、`1be0472`
- [x] 仓库与文档整理：重写 `README.md` 聚焦无 CDP 伴随浮层、同步 `STATE.md`/`TASKS.md`、归档早期 CDP 原型至 `legacy/cdp/` 并清理过时 `HANDOFF.md`

- [x] 统一常驻条与详情页额度重置口径，保留原始日志字段。
- [x] 实现宿主 DPI 缩放、范围受限时的弹窗滚动和 12 DIP 外圆角，通过 96 DPI 对比、150% 离屏渲染和 125% / 150% / 200% 模拟交互验证。

## 待实机复核

- [ ] 在真实 125% / 150% / 200% 显示器和异 DPI 双屏上检查停靠、跨屏重绘与弹窗交互。
