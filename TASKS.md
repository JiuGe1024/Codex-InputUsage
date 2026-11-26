# Codex InputUsage 任务清单

更新于 2026-11-26。

## 已完成

- [x] 加固诊断隐私、伴随启动前置检测与原子 TOML 写回，并限制会话正文缓存。十组自检及临时配置脚本验证通过。

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
- [x] 面板顶部 Tab 切换与常驻条显示项右键微调（`OverlaySettingsStore` 持久化至 `%LOCALAPPDATA%\CodexInputUsage\settings.json`，支持 TPS / 本轮 / 累计 / 缓存 / `5h 配额余量` 微胶囊独立开关与恢复默认）— `8801c99`
- [x] 拆分独立「调用明细」页签、补齐页签矢量图标、速度卡并列显示会话平均速度及逐轮上下文/思考/正文与 Token 消耗列 — `a84919c`、`1be0472`
- [x] 仓库与文档整理：重写 `README.md` 聚焦无 CDP 伴随浮层、同步 `STATE.md`/`TASKS.md`、归档早期 CDP 原型至 `legacy/cdp/` 并清理过时 `HANDOFF.md` — `9b1699e`
- [x] 统一常驻条与详情页额度重置口径，保留原始日志字段；实现宿主 DPI 缩放、范围受限时的弹窗滚动、12 DIP 外圆角与三页签统一 `540×608` 逻辑尺寸 — `b0d01e7`、`a2a3b2b`
- [x] 重排常驻条胶囊：采用 `Segoe UI Variable Text Semibold`、按速度四档语义着色、柔和主题蓝本轮/累计胶囊与矢量细竖线分隔 — `72ca3f6`、`4def59d`
- [x] 调用明细新增「总汇总」子视图（会话走势、OLS/LOESS 趋势拟合与置信带、四段 Token 占比模型卡及点击锁定拟合）并统一全 UI 模型短名 — `8adaf87`
- [x] 重构「用量统计」为今天 / 1 天 / 7 天 / 30 天 / 全部 / 自定义 6 档统计，统一全弹窗平滑贝塞尔曲线并新增用量趋势分项悬停卡片 — `4ab1e1c`、`4363cf2`
- [x] 优化原生 Codex 浮层定位响应与稳定性：几何与指纹双 MTA 线程解耦、UIA `CacheRequest` 批量预读、50ms 纯坐标快路径、350ms 连续降级防抖、切回异尺寸暂隐、先落位后匹配、恢复 `ProseMirror` 失败时的批量 `Edit` 备用查询及后台线程 UIA 异常隔离 — `6c92964`、`97b3598`、`0c31cc8`
- [x] 推送前仓库收口：同步文档、清理本地路径、补充自述文件高清界面配图并移除早期 CDP 归档目录，仅保留无 CDP 主线 — `092b83d`、`0f560dd`

## 待实机复核

- [ ] 在真实 125% / 150% / 200% 显示器和异 DPI 双屏上检查停靠、跨屏重绘与弹窗交互。
- [ ] 在持续日常使用中观察同一 Codex 窗口连续切换多条对话与切回已改尺寸窗口时的定位表现。
