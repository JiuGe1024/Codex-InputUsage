# Codex InputUsage

独立的 Windows Codex 用量插件。输入框下方显示最近完成一轮的 Token、会话累计 Token 和累计缓存率。点击用量胶囊，向上展开最近一轮的输入、输出、缓存命中、平均速度及扣除工具后的时长。逐轮表格列出当前会话各已完成轮次的 Token 消耗和缓存率。缺少可核对的数据时显示 `--`。

## 启动

需要 Microsoft Store 版 Codex 和 Node.js 22 或更新版本。从构建产物双击 `outputs/Codex-InputUsage.exe`。保留项目目录结构。若 Codex 已通过官方入口运行，请先正常退出一次。

InputUsage 和原 TokenBar 各自使用独立源码、运行状态。两者共用单实例锁以避免同时挂载。切换时先退出 Codex，再打开想用的启动器。正常退出 Codex 后后台自动停止。

## 构建

在项目目录运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-InputUsage.ps1`。

统计沿用经过校验的本机会话记录，连接仅使用验证过的本机回环调试端口。输入区节点结构不匹配或当前会话身份不明确时，组件会隐藏。只支持已能识别会话 ID 的 Codex 对话页面。

已验证当前 Codex 窗口的实际挂载和真实会话数据。浏览器预览覆盖了 320 px 宽度、鼠标开关和键盘 Enter、Esc。全新环境冷启动尚待实机确认。

## 无 CDP 浮层原型

运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-OverlayPrototype.ps1` 构建原型，生成 `outputs/Codex-InputUsage-Overlay.exe`。原型从正在运行的官方 Codex 读取 Windows 无障碍控件坐标，在前台窗口内寻找输入区下方的空位。窗口内部空间不足时，改贴到窗口下边缘，前提是屏幕工作区有空位。仍无空位、输入框识别不明确或切到其他应用时隐藏。左键展开，右键退出。

当前原型用于验证定位和交互，用量显示 `--`。本机已确认输入框与原生工具栏下方有可用空间，模拟空间不足时也通过了隐藏检查。无障碍树中的可见元素未提供会话 UUID，钩子自动启动和会话绑定尚未接入。
