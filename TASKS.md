# Codex InputUsage 任务清单

更新于 2026-11-21。

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

## 待选增强（对照本机 Antigravity `usage-monitor`，待用户确认后推进）

- [ ] **账号级实时余额进度卡 + 重置倒计时**：独立双列进度卡展示 5h / 周余量与重置倒计时，并始终取本机最新 rollout 的实时 `rate_limits`（解决打开旧聊天显示过期余额的问题）
- [ ] **详情面板交互打磨**：点击外部区域 / 按 `Esc` 自动收起、标题栏「钉住」开关、逐轮明细可视化滚动条、悬停/点击行查看千分位精确数值与工具/模型耗时拆分
- [ ] **模型徽标与会话均速**：标题栏显示 `state_5.sqlite` 的当前模型与推理档位，速度卡补充本会话平均 TPS
- [ ] **历史汇总视图（可选）**：面板顶部 Tab 切换「当前会话 / 今日与 7 日汇总」（基于 `state_5.sqlite` 毫秒级聚合）
- [ ] **常驻条自定义槽位（可选）**：右键菜单可配置显示项（如加入 `5h 余量`）并持久化到本地设置
