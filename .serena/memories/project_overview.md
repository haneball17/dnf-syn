# 项目概述（DNF Sync Box）

- 目标：将前台 DNF 窗口的方向键同步到后台 DNF 窗口。
- 技术栈：C# / .NET 8（net8.0-windows），WPF + Windows Forms，P/Invoke 调用 user32。
- 关键约束：
  - 必须管理员权限启动（app.manifest）。
  - 仅同步方向键（MVP），热键为 `Alt + .`。
  - 窗口识别：标题包含 `DNF Taiwan` 或进程名 `dnf.exe`。
  - 前台非 DNF 时必须自动暂停并清键，防止误操作与卡键。
  - 禁止引入鼠标同步或宏功能（除非明确需求）。
- 目录结构：
  - `src/DNFSyncBox/`：WPF 主项目。
  - `src/DNFSyncBox/Core/`：核心逻辑（窗口扫描、钩子、同步投递）。
  - `docs/dnf-syn_开发方案.md`：设计与方案文档。
- 经验教训：后台 WM_KEY/线程消息投递可能无效，疑似 DirectInput/RawInput 路径；需要保留完整日志（窗口句柄、类名、标题、线程 ID、投递通道与结果）。
