# Codex InputUsage 验证记录

更新于 2026-11-21。

## 已实测通过

### 1. 构建与零警告检查
- 命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1`
- 结果：退出码 0，0 错误 0 警告，产物 `outputs/Codex-InputUsage-Overlay.exe`（144,384 字节）。

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

### 5. 视觉与排版核对
- 离屏渲染预览（`work/preview-strip-light.png`、`work/preview-strip-dark.png`、`work/preview-detail-light.png`、`work/preview-detail-dark.png`）已逐张目视核对：
  - 常驻条完整档 `328×24px`（实机工具栏可用空位实测 `349px`），矢量琥珀金闪电、整数缓存柔色微胶囊、明暗主题均清晰无截断；
  - 详情面板标题栏模型徽标 + 钉住按钮、2×2 卡片（含均速与两位小数缓存率）、独立双列实时额度卡、十通道分段条与 2×5 清单、带拖拽滚动条及底部精确明细条的逐轮表排版对齐正常。
- 用户实机交互验收：此前版本已由用户确认原生启动、停靠外观、对话切换、TPS 速度、本轮缓存率、十通道上下文与响应优化整体满意。

## 未在本终端执行的验证边界
- 受限于代理终端隔离桌面 `exebox-...`，本次新增的点击面板外部 / `Esc` 自动收起、钉住开关与滚动条拖拽交互，以及高 DPI 多屏缩放尚未在真实交互桌面实测。
