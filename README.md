# Codex InputUsage

面向 Windows 原生 Microsoft Store 版 Codex Desktop 的轻量伴随用量监控浮层（**无 CDP、零外部依赖、单个 170 KB 可执行文件**）。直接从原生图标启动 Codex，浮层自动随行停靠在输入框工具栏，提供实时速度、Token、缓存命中率、额度余量、十通道上下文拆分、逐轮明细及近 7 日历史汇总。

## 核心特性

- **输入框工具栏无缝停靠**：通过 Windows UI Automation 定位 Codex 输入框底栏（Context 圆环左侧），以 `UpdateLayeredWindow` 32 位预乘 Alpha 透明层渲染，自动跟随 `~/.codex/config.toml` 明暗主题；通过进程外 Win32 `SetWinEventHook` 实现切窗 `<1ms` 隐藏/恢复与窗口拖动 60 FPS 零重绘跟随。
- **常驻状态条与右键槽位自定义**：默认展示 `⚡ tok/s · 本轮 · 累计 · 缓存 [本轮% / 累计%]`（多档窄窗自适应），支持在状态条或托盘右键菜单独立开关显示项（输出速度、本轮 Token、会话累计、缓存命中率、可选 `5h 配额余量` 微胶囊），偏好持久化至 `%LOCALAPPDATA%\CodexInputUsage\settings.json`。
- **零猜测会话绑定**：优先结合本机 IPC（若当前 Codex 版本开启）；针对 Codex `26.1117+` 移除 `\\.\pipe\codex-ipc` 的情况，自动过滤 `state_5.sqlite` 内部子代理线程并对本机全部真实会话做唯一标题或可见正文精确匹配，证据不足时拒绝猜测并显示 `--`。
- **三页签详情监控卡片**（左键点击常驻条展开/收起，支持点击外部/`Esc` 自动收起与标题栏「钉住」开关）：
  - **当前会话**：输出速度与会话平均速度并列卡、本轮/累计缓存命中率（两位小数）、最近一轮与会话累计卡、账号级实时 5h / 周额度余量与重置倒计时卡、十通道上下文窗口拆分（按 Hamilton 最大余数法精确对齐官方值）。
  - **逐轮明细**：独立表格页展示每轮模型、速度、首字延迟、上下文 / 思考 / 正文分解、Token 消耗与缓存率，支持滚轮与可视拖拽滚动条及底部精确明细条。
  - **今日与 7 日汇总**：直读 `state_5.sqlite` 毫秒级聚合今日与近 7 日消耗、可悬停/点击查精确值的 7 日柱状趋势图、按模型分布与近 7 日 Top 5 高消耗会话。
- **原生无工具 MCP 伴随启动**：不写 Windows 开机自启注册表，通过 `~/.codex/config.toml` 的轻量无工具 stdio MCP 入口（`scripts/codex-mcp-companion.mjs`，`tools/list` 返回空数组、零上下文开销）在启动原生 Codex 时自动拉起浮层，Codex 退出 3 秒后自动退出。

## 快速开始

### 1. 构建

仅需 Windows 自带的 .NET Framework 4.x `csc.exe`，无需额外 SDK 或 NuGet 依赖：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1
```

产物输出至 `outputs/Codex-InputUsage-Overlay.exe`。

### 2. 注册随原生 Codex 自动启动

需要本机已安装 Node.js（用于运行无工具 MCP 伴随脚本）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Install-AutoStart.ps1
```

卸载伴随启动（同时清理历史注册表项）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Uninstall-AutoStart.ps1
```

也可直接在常驻条或系统托盘右键菜单中切换“跟随原生 Codex 自动启动”。

### 3. 离线自检与离屏预览

```powershell
.\outputs\Codex-InputUsage-Overlay.exe --self-test
```

自检报告与明暗双主题各页签预览图输出至 `work/`（不干扰正在运行的浮层实例）。

## 目录结构

```text
src/overlay/                  # 伴随浮层全部 C# 源码（13 个模块）
scripts/
  Build-OverlayPrototype.ps1  # 零依赖 csc.exe 构建脚本
  Install-AutoStart.ps1       # 注册 ~/.codex/config.toml 无工具 MCP 伴随入口
  Uninstall-AutoStart.ps1     # 移除伴随入口
  codex-mcp-companion.mjs     # 无工具 stdio MCP 伴随拉起脚本
outputs/                      # 构建产物（Git 忽略）
work/                         # 诊断日志、自检报告与离屏预览图（Git 忽略）
legacy/cdp/                   # 早期 CDP 路线历史原型归档（仅作参考，不再维护）
```

## 项目文档

- [STATE.md](STATE.md)：当前架构、模块职责与运行约束
- [TASKS.md](TASKS.md)：迭代里程碑与任务清单
- [VALIDATION.md](VALIDATION.md)：实测验证记录与验证边界
