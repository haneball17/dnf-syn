# 代码风格与约定

- 语言：C#，启用 Nullable 与 ImplicitUsings；文件级命名空间（file-scoped namespace）。
- 注释：核心函数必须有中文注释，关键逻辑需解释“为什么这样做”。
- 命名：清晰直观的英文标识符，日志与注释使用中文。
- 结构：核心逻辑集中于 `Core/`，UI 在 WPF 主项目。
- 关键文件：
  - `src/DNFSyncBox/Core/SyncController.cs`
  - `src/DNFSyncBox/Core/WindowManager.cs`
  - `src/DNFSyncBox/Core/KeyboardHook.cs`
  - `src/DNFSyncBox/Core/KeySender.cs`
  - `src/DNFSyncBox/Core/NativeMethods.cs`
  - `src/DNFSyncBox/App.xaml.cs`
  - `src/DNFSyncBox/MainWindow.xaml.cs`
