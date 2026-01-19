# AGENTS.MD - DNF Sync Box

## 🎯 项目概述
- 项目类型：Windows 桌面工具（WPF / .NET 8）
- 目标：将前台 DNF 窗口的方向键同步到后台 DNF 窗口
- MVP 范围：窗口识别 + 方向键同步 + Alt + . 暂停热键

## 🧭 目录结构
- `src/DNFSyncBox/`：WPF 主项目
- `src/DNFSyncBox/Core/`：核心逻辑（窗口扫描、钩子、同步投递）
- `docs/dnf-syn_开发方案.md`：设计与方案文档

## ✅ 关键约束（务必遵守）
- 必须管理员权限启动（`app.manifest` 已配置）。
- 仅同步方向键（MVP），热键为 `Alt + .`。
- 窗口识别：标题包含 `DNF Taiwan` 或进程名 `dnf.exe`。
- 前台非 DNF 时必须自动暂停并清键，防止误操作与卡键。

## 🧩 核心代码与注释规范
- **所有核心函数必须有中文注释**，说明该函数的职责与关键步骤。
- 重点文件（新增或修改时必须补充注释）：
  - `src/DNFSyncBox/Core/SyncController.cs`
  - `src/DNFSyncBox/Core/WindowManager.cs`
  - `src/DNFSyncBox/Core/KeyboardHook.cs`
  - `src/DNFSyncBox/Core/KeySender.cs`
  - `src/DNFSyncBox/Core/NativeMethods.cs`
  - `src/DNFSyncBox/App.xaml.cs`
  - `src/DNFSyncBox/MainWindow.xaml.cs`
- 关键逻辑节点（例如：热键触发、自动暂停、清键、投递过滤）必须有“为什么这样做”的注释。

## 🛠️ 开发流程
1. 确认 .NET SDK 版本：`dotnet --version`（要求 .NET 8）。
2. 构建项目：`dotnet build "src/DNFSyncBox/DNFSyncBox.csproj"`。
3. 运行项目：`dotnet run --project "src/DNFSyncBox/DNFSyncBox.csproj"` 或使用 VS/VS Code 启动。
4. 以管理员身份运行，并开启多个 DNF 窗口验证同步。

**常用命令：**
- 清理构建：`dotnet clean "src/DNFSyncBox/DNFSyncBox.csproj"`
- 发布（可选）：`dotnet publish "src/DNFSyncBox/DNFSyncBox.csproj" -c Release`

**常见排错：**
- 问题：编译提示 .NET 版本不受支持；处理：安装 .NET 8 SDK。
- 问题：程序启动无权限；处理：右键管理员运行，确认 `app.manifest` 生效。
- 问题：无任何窗口被识别；处理：确认窗口标题包含 `DNF Taiwan` 或进程名 `dnf.exe`。
- 问题：前台非 DNF 不同步；处理：这是设计行为（自动暂停），切回 DNF 即可恢复。

## 🧪 测试流程
- 当前未配置自动化测试。
- MVP 验证步骤：
  1. 打开多个 DNF 窗口。
  2. 切换前台窗口，观察自动暂停/恢复。
  3. 前台方向键应同步到后台窗口。
  4. `Alt + .` 触发暂停时，后台不再响应且清除卡键。

## 🧭 变更流程
- 修改核心行为前，必须同步更新 `docs/dnf-syn_开发方案.md`。
- 扩展按键范围或规则时，必须同步更新：
  - 代码白名单/黑名单
  - 设计文档中的键位清单与配置模板
- 核心逻辑调整必须补充中文注释，解释“为什么这样做”。
- 禁止引入鼠标同步或宏功能，除非明确需求。

## 📝 经验教训（来自实际日志验证）
- 窗口消息与线程消息投递“成功”≠ 游戏一定响应；DNF 2012 在后台可能**不处理 WM_KEY/线程消息**。
- 该现象高度符合 **DirectInput/RawInput** 的输入路径：后台窗口消息不生效，但前台键盘有效。
- 当出现“投递成功但无响应”时，应优先判定为输入路径不匹配，而不是窗口识别错误。
- 需要保留完整调试日志（窗口句柄、类名、标题、线程 ID、投递通道与结果）以支撑判断。
- 若必须支持该类输入路径，需考虑**前台注入或客户端协作**方案；后台消息投递仅作为低成本尝试。
