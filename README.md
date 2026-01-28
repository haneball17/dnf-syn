# DNF Sync Box

## 1. 简介

DNF Sync Box 是面向 DNF（2012 客户端）多开场景的键盘同步工具：前台窗口的按键同步到后台窗口。当前实现以“共享内存 + 注入端伪造输入”为主线，同时保留 EasyHook 方向键伪造的技术验证分支。

## 2. 主要目录与项目

以下名称是你给出的结构清单，但在当前仓库中未发现同名目录或项目文件，请确认是否为规划名称或外部仓库名称：

- HelperStart：当前仓库未发现对应目录/项目。
- version-inject：当前仓库未发现对应目录/项目；若指原生注入模块，可对应 `dnfinput/`。
- GameHelperGUI：当前仓库未发现对应目录/项目；若指 GUI 主程序，可对应 `src/DNFSyncBox/`。

补充（当前仓库实际目录与项目）：

- `src/DNFSyncBox/`：WPF 主程序（控制端）。
- `src/DNFSyncBox/Core/`：核心逻辑（窗口扫描、钩子、共享内存、方案配置）。
- `src/DNFSyncBox.Agent/`：EasyHook 代理（技术验证）。
- `src/DNFSyncBox.Injector/`：EasyHook 注入器（技术验证）。
- `dnfinput/`：原生注入模块（MinHook + CMake）。
- `docs/`：方案与设计说明文档。

## 3. 关键配置文件

- `%AppData%\DNFSyncBox\profiles.json`：键位同步方案配置（All/Whitelist/Blacklist/Mapping），支持热重载。
- `src/DNFSyncBox/app.manifest`：要求管理员权限启动。

## 4. 技术概览

- WPF 控制端：全局键盘钩子 + 窗口扫描 + 自动暂停/恢复 + 日志展示。
- 共享内存：发布键盘快照（状态、边沿计数、方案掩码、心跳）。
- 原生注入端（dnfinput.dll）：Hook GetAsyncKeyState / GetKeyboardState / DirectInput / RawInput 并伪造输入语义。
- 技术验证分支：EasyHook 代理与注入器，用于方向键伪造路径验证。

## 5. 技术细节（可选了解）

- 共享内存使用 seq 无锁一致性与心跳；暂停或失联时强制清键避免卡键。
- RawInput/DirectInput 伪造按键状态，支持目标键掩码与黑名单拦截。
- 修正 `WM_INPUT` 的 `wParam` 语义以兼容后台处理差异。
- 必要时启用前台欺骗（GetForegroundWindow/GetFocus）以提升后台响应概率。

## 6. 构建与运行建议（简要）

控制端（.NET 8 / WPF）：

```bash
dotnet build "src/DNFSyncBox/DNFSyncBox.csproj"
dotnet run --project "src/DNFSyncBox/DNFSyncBox.csproj"
```

原生注入模块（x86/Win32）：

```bash
cmake -S "dnfinput" -B "dnfinput/build" -A Win32
cmake --build "dnfinput/build" --config Release
```

## 7. 运行流程（概览）

1. 以管理员权限启动 DNFSyncBox。
2. 安装全局键盘钩子、创建共享内存、扫描 DNF 窗口。
3. 前台为 DNF 时同步按键；前台非 DNF 时自动暂停并清键。
4. 注入端读取共享内存并在后台伪造输入（需外部注入流程）。

## 8. 运行时控制与热重载

- 热键：`Alt + .` 手动暂停/恢复。
- 自动暂停：前台非 DNF 自动暂停，并强制清键。
- 配置热重载：`profiles.json` 修改后自动生效。
- UI 日志与本地日志：`%AppData%\DNFSyncBox\logs\latest.log`。

## 9. 兼容性提示

- 仅支持 Windows；控制端要求 .NET 8。
- dnfinput 仅支持 x86/Win32。
- 后台窗口可能忽略 WM_KEY/线程消息，输入不生效时优先判断输入路径不匹配。
- 注入可能受杀软/安全策略影响，需自行评估风险。

## 10. 结尾说明（docs 扩展指引）

- 详细设计、按键方案与技术细节请参考 `docs/dnf-syn_开发方案.md`。
- 修改核心行为或按键范围时，必须同步更新该文档。

---

如需把 HelperStart / version-inject / GameHelperGUI 对应到实际目录，请告知具体路径或目标功能，我会继续对齐与补充。
