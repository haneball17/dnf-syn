# DNF Sync Box - 消息欺骗改造方案（技术验证版）

## 1. 目标

在现有 WM_KEY* 投递无效的前提下，先确认 DNF 实际输入路径：

- 是否频繁调用 `GetAsyncKeyState`
- 是否使用 DirectInput（`DirectInput8Create` / `GetDeviceState`）

仅做**最小可验证**：能统计调用频率，必要时可伪造方向键以观察游戏响应。

---

## 2. 组件与职责

- **DNFSyncBox.Agent**（进程内代理）
  - 注入到 DNF 进程中，钩住输入相关 API。
  - 记录调用次数，并支持方向键伪造（可开关）。
  - 日志输出：`%AppData%/DNFSyncBox/logs/agent-<pid>.log`。

- **DNFSyncBox.Injector**（注入器）
  - 发现 `dnf.exe` 进程并执行注入。
  - 通过命名管道发送命令（按下/抬起/清键/统计）。

---

## 3. 技术要点（最小钩子集合）

1. **GetAsyncKeyState**
   - 目的：判断 DNF 是否走传统键盘状态 API。
   - 处理：统计调用次数；若启用伪造，叠加方向键“按下”状态。

2. **DirectInput**
   - `DirectInput8Create`：确认是否初始化 DirectInput。
   - `IDirectInput8::CreateDevice`：捕获键盘设备创建。
   - `IDirectInputDevice8::GetDeviceState`：统计调用并在需要时叠加方向键状态。

**判定原则：**
- `GetAsyncKeyState` 调用密集 → 传统键盘状态路径。
- `GetDeviceState` 调用密集 → DirectInput 路径。
- 两者同时密集 → 混合或兼容路径。

---

## 4. 验证流程

1. 构建项目（Windows 环境，需 .NET Framework 4.8 目标包）：
   - `dotnet build "src/DNFSyncBox.Agent/DNFSyncBox.Agent.csproj"`
   - `dotnet build "src/DNFSyncBox.Injector/DNFSyncBox.Injector.csproj"`

2. 以管理员启动注入器：
   - `dotnet run --project "src/DNFSyncBox.Injector/DNFSyncBox.Injector.csproj"`
   - 若提示未找到代理 DLL，可指定路径：`--agent "E:/code/dnf-syn/src/DNFSyncBox.Agent/bin/Debug/net48/DNFSyncBox.Agent.dll"`

3. 常用指令：
   - `stats`：查看输入路径调用统计
   - `spoof on/off`：开启/关闭伪造输入
   - `down up` / `up up`：模拟方向键按下/抬起（up/down/left/right）
   - `clear`：清空方向键状态
   - `stop`：让代理退出

---

## 5. 结果判断

- **仅 GetAsyncKeyState 明显增长**：优先走键盘状态 API。
- **GetDeviceState 明显增长**：优先走 DirectInput。
- **两者都增长**：可能混合使用或有兼容逻辑，后续需针对主路径继续欺骗。

---

## 6. 风险与说明

- **反作弊/安全软件**：注入与钩子可能被拦截，需具备降级方案。
- **架构差异**：目标进程位数不同需匹配对应运行时与依赖。
- **稳定性**：如发现崩溃，先关闭伪造输入，仅保留统计钩子。

---

## 7. 后续扩展方向（确认路径后再做）

- 针对真实路径完善输入伪造（RawInput 或 DirectInput）。
- 将代理纳入主程序自动注入与同步控制流程。
- 完善安全与异常恢复策略。
