# Codex InputUsage 验证记录

更新于 2026-11-22。

## 已实测通过

### 1. 构建与零警告检查
- 命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1`
- 结果：退出码 0，0 错误 0 警告，产物 `outputs/Codex-InputUsage-Overlay.exe`（169,984 字节）。

### 2. 路由与零猜测会话匹配（`--self-test`）
- 单测结果：`match_tests pool=True header=True bodyTieBreak=True refuseAmbig=True disconnectClear=True localHdr=True localSingle=True localRefuseAmbig=True`
  - 多活跃流同窗订阅池（3 候选）保留完整不覆盖；
  - 标题唯一匹配锁定正确会话（`IPC+标题`）；
  - 同名对话通过可见正文唯一消歧（`IPC+正文`）；
  - 多候选且前台无法唯一确认时拒绝猜测并清空（`待确认`）；
  - IPC 断线且无前台 UIA 证据时立即清空旧值显示 `--`（`IPC 断线`）；
  - Codex `26.1117+` 无 `\\.\pipe\codex-ipc` 场景：`localHdr=True`（截断标题前缀匹配）、`localSingle=True`（单段长独有正文匹配）、`localRefuseAmbig=True`（同名无正文拒绝猜测）。
- 实机数据库过滤验证：`db_real_threads total=92 guardianLeaked=0 withRollout=19`（原 519 条 `Guardian review` 等子代理记录已完全剔除，不再挤占真实对话候选池）。
- 实机进程认证回归验证：当官方 Store 版 Codex 以非 AppModel 方式启动（`GetApplicationUserModelId` 返回 `15703` / `APPMODEL_ERROR_NO_APPLICATION`）时，通过 `QueryFullProcessImageName` 核验 `Program Files\WindowsApps\OpenAI.Codex_*__2p2nqsd0c76g0\app\ChatGPT.exe` 成功识别（`codexRunning=True`）。

### 3. 数据口径核对（对比原版 TokenBar 与 SQLite）
- 测试样本：`rollout-2026-11-08T14-31-26-01a1b9da-c9ed-7163-a8be-ef01576cc0aa.jsonl`（52 轮，含压缩历史）
  - 最近一轮：`811,833`（原版 TokenBar：`811.83k`），本轮缓存：`99.49%`（原版：`99.49%`）
  - 会话累计：`68,647,453`（原版 TokenBar：`68,647.45k`），累计缓存：`97.41%`（原版：`97.41%`）
  - TPS：修正后最近 6 轮稳定在 `40.9–50.2 tok/s`（最后一轮 `47 tok/s`，模型耗时 `43.7s` = 整轮 `45.9s` - 工具 `2.2s`），修复了旧口径遗漏工具调用参数生成耗时导致的 `350 tok/s` 虚高
  - 十通道上下文和：`271,654 == 官方 271,654`（`用户消息 87,226 / 模型回复 76,740 / 工具结果 74,370 / 系统提示词 16,532 / 系统工具 7,798 / 压缩摘要 5,824 / MCP 工具 2,082 / Skill 696 / 用户规则 386 / 子代理通知 0`）
  - 会话均速与模型徽标：`avgTps = 32.3 tok/s`，`model = gpt-6.2-sol (medium)` -> 显示 `gpt-6.2-sol · 中`
  - 账号级实时额度覆盖：解析旧对话 `01a1b9da` 时，`AccountQuotaReader` 自动从最新落盘的 `01a1f999` 提取账号实时额度（`5h 余 22% · 1m 后重置`，`周余 48% · 21h 2m 后重置`）

### 4. MCP 原生伴随启动握手
- 执行 `scripts/Install-AutoStart.ps1`：已移除遗留 `HKCU\...\Run\CodexInputUsageOverlay`，写入 `~/.codex/config.toml` 的 `[mcp_servers.codex_input_usage]`。
- 执行 `node scripts/codex-mcp-companion.mjs` 握手测试：`initialize` 正常响应，`tools/list` 返回 `{"tools":[]}`，退出码 0。

### 5. 历史汇总与常驻条自定义槽位（`--self-test` + 实机验收）
- 历史汇总直读 SQLite（`HistoryStatsReader`）：`history todayTok=0 todayThreads=0 weekTok=248072443 weekThreads=13 avgDaily=35438920 allTok=2474582286 allThreads=92 models7d=2 top7d=5`（基于 `state_5.sqlite` 毫秒级聚合，已剔除子代理线程；今日 0 消耗空状态显示正常）。
- 常驻条槽位持久化与防空保护（`OverlaySettingsStore`）：`settings_test roundTrip=True guardLastSlot=True`；开启 `5h 配额余量` 胶囊渲染 `strip_quota5h_px=384x24`，窄窗自动降档生效。
- 离屏预览核对：`work/preview-detail-history-light.png`、`work/preview-detail-history-dark.png`、`work/preview-strip-quota5h-light.png` 明暗双主题排版清晰无截断。

### 6. 视觉与排版核对
- 离屏渲染预览（`work/preview-strip-light.png`、`work/preview-strip-dark.png`、`work/preview-detail-light.png`、`work/preview-detail-dark.png`）已逐张目视核对：
  - 常驻条完整档 `328×24px`（实机工具栏可用空位实测 `349px`），矢量琥珀金闪电、整数缓存柔色微胶囊、明暗主题均清晰无截断；
  - 详情面板标题栏模型徽标 + 钉住按钮、顶部「当前会话 / 今日与 7 日汇总」双 Tab、2×2 卡片（含均速与两位小数缓存率）、独立双列实时额度卡、十通道分段条与 2×5 清单、带拖拽滚动条及底部精确明细条的逐轮表排版对齐正常。
- 用户实机交互验收：用户已确认原生启动、停靠外观、对话切换、TPS 速度、本轮缓存率、十通道上下文、响应优化，以及新增的「今日与 7 日汇总」Tab 与常驻条右键显示项微调均体验正常。

### 7. 额度重置、DPI 与弹窗圆角

- 在仓库根目录运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1`，退出码 0，编译器 0 错误、0 警告。产物为 178,176 字节。
- `work/selftest-report.txt` 中，已有会话匹配与设置检查继续通过。新增 `quota_reset_tests future=True reset=True missing=True`。模拟原始用量为 14% 且重置时间到达时，常驻条输出 `[5h 100%]`，使用 `Good` 语义等级。
- 用同一份 25 轮日志对比基线 `9b1699e` 和新版。96 DPI 下明暗常驻条与紧凑条像素相同，完整档为 `332×24`，宽度随样本数字变化。三页弹窗尺寸相同，去掉 14px 外框后内部像素相同；新版四角像素透明。
- 目视核对四张 150% 预览，未见文字截断或重叠。
  - `work/preview-dpi150-strip.png` 为 `498×36`。
  - `work/preview-dpi150-session.png` 为 `810×987`。
  - `work/preview-dpi150-turns.png` 为 `810×810`。
  - `work/preview-dpi150-history.png` 为 `810×849`。
- 自检 `dpi_tests logicalHit=True workArea=True`。离屏模拟在 100%、125%、150%、200% 分别验证了窗口范围、圆角区域、页签点击、逐轮滚轮与拖动、受限视口滚动及末行命中。记录在 `work/dpi-qa-20261122-201132/dpi-interaction-check.txt`。
- 正式产物已启动，日志确认新版浮层进程识别了 Codex。只替换了 InputUsage 浮层进程。

## 未在本终端执行的验证边界

- 真实高 DPI 显示器上的停靠、异 DPI 双屏拖移与弹窗实机点击尚未复测。本轮的高 DPI 数据来自离屏渲染与模拟输入。
