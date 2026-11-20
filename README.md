# Codex InputUsage

独立的 Windows Codex 用量插件。输入框下方显示最近完成一轮的 Token、会话累计 Token 和累计缓存率。点击用量胶囊，向上展开最近一轮的输入、输出、缓存命中、平均速度及扣除工具后的时长。逐轮表格列出当前会话各已完成轮次的 Token 消耗和缓存率。缺少可核对的数据时显示 `--`。

## 启动

需要 Microsoft Store 版 Codex 和 Node.js 22 或更新版本。从构建产物双击 `outputs/Codex-InputUsage.exe`。保留项目目录结构。若 Codex 已通过官方入口运行，请先正常退出一次。

InputUsage 和原 TokenBar 各自使用独立源码、运行状态。两者共用单实例锁以避免同时挂载。切换时先退出 Codex，再打开想用的启动器。正常退出 Codex 后后台自动停止。

## 构建

在项目目录运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-InputUsage.ps1`。

统计沿用经过校验的本机会话记录，连接仅使用验证过的本机回环调试端口。输入区节点结构不匹配或当前会话身份不明确时，组件会隐藏。只支持已能识别会话 ID 的 Codex 对话页面。

已验证当前 Codex 窗口的实际挂载和真实会话数据。浏览器预览覆盖了 320 px 宽度、鼠标开关和键盘 Enter、Esc。全新环境冷启动尚待实机确认。

## 无 CDP 伴随浮层

运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1` 构建，生成 `outputs/Codex-InputUsage-Overlay.exe`（源码位于 `src/overlay/`）。

- **工具栏无缝停靠**：通过 Windows UI Automation 识别官方 Microsoft Store 版 Codex 的输入框工具栏（Context 圆环左侧），使用 `UpdateLayeredWindow` 32 位透明文字绘制并跟随 `~/.codex/config.toml` 的明暗主题。常驻显示输出速度（扣除工具耗时）、最近一轮 Token、会话累计 Token 与缓存命中率，窗口变窄时自动降级简写以免遮挡按钮。
- **严格会话绑定**：只读监听本机 `\\.\pipe\codex-ipc` 的 `thread-stream-following-changed` 广播，再按精确会话 ID 读取 `~/.codex/state_5.sqlite` 与增量 rollout JSONL。IPC 断线或多窗口同时跟随不同会话时不猜测，立即清空旧值显示 `--`。
- **详情弹窗与逐轮明细**：左键点击状态条展开/收起详情卡片，显示输出速度、首字延迟、缓存命中率、最近一轮与会话累计、上下文进度条、5h/周配额余量及可滚轮翻页的逐轮明细。
- **随原生 Codex 出现**：Codex 未运行时自动低功耗待机，原生图标启动 Codex 后自动唤醒停靠。可在右键或托盘菜单切换“开机自启”，或运行 `scripts/Install-AutoStart.ps1` / `scripts/Uninstall-AutoStart.ps1`。
