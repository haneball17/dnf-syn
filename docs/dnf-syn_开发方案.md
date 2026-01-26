# dnf-syn_开发方案

**版本：** 1.5
**技术栈：** C# / .NET 8 (WPF)
**目标：** 实现前台窗口输入操作对后台窗口的实时同步，专用于 2012版 DNF 客户端。

---

## 1. 项目概述 (Overview)

本项目旨在开发一个辅助工具，用于在“一拖多”场景下，将主控窗口（前台）的键盘按键指令，实时、准确地投递给其他被控窗口（后台）。

### 核心需求
- 自动识别 DNF 窗口并维护主控/从控关系（用于前台旁路）。
- 后台同步：通过共享内存快照让后台进程伪造键盘状态读取。
- 控制灵活：支持全局快捷键（默认 `Alt + .`）暂停/恢复，同步暂停时强制清键。
- 同步方案：`All / Whitelist / Blacklist / Mapping`，支持配置文件热切换。

### 关键约束与决策
- 窗口标题关键字匹配：`DNF Taiwan`（优先）；进程名 `dnf.exe` 作为辅助校验。
- 必须管理员权限启动；非管理员直接退出并提示原因。
- 自动前台判断：前台非 DNF 时立即暂停同步。
- 前台从 DNF 窗口1切换到窗口2时，窗口2自动成为主控并切换同步目标。

---

## 2. 技术架构 (Architecture)

### 2.1 技术选型
- 语言：C#
- 框架：.NET 8
- UI：WPF
  - 理由：便于制作悬浮状态窗与主面板。
- 底层交互：Windows API (P/Invoke)
  - 核心库：`user32.dll`
  - 核心机制：共享内存 + Win32/DirectInput/RawInput 伪造（`GetAsyncKeyState` /
    `GetKeyboardState` / `GetDeviceState` / `GetRawInputData` / `GetRawInputBuffer`）

### 2.2 模块划分
1. UI 层（View）
   - 主控制面板（状态、模式、热键、日志）。
   - 状态浮层（Overlay，二期可选）。
2. 窗口管理层（Window Manager）
   - `EnumWindows` 扫描、过滤与维护句柄列表。
   - 主控判定与失效回收。
3. 输入监听层（Input Listener）
   - 全局键盘钩子 `WH_KEYBOARD_LL` 捕获原始按键。
4. 共享内存层（Shared Memory）
   - 前台维护键盘状态快照（含边沿计数与方案掩码）。
5. 被注入层（dnfinput.dll）
   - Hook `GetAsyncKeyState` / `GetKeyboardState` / `GetDeviceState` 并按方案伪造返回。
   - 通过 `DirectInput8Create`/`CreateDevice` 安装设备级 Hook。
   - 必要时 Hook `GetForegroundWindow`/`GetActiveWindow`/`GetFocus` 进行前台欺骗。
6. 配置层（Profiles）
   - 方案模式与键位映射配置（支持热切换）。
7. 日志与诊断层（LogService）
   - 事件记录、统计计数、故障信息。

### 2.3 关键流程
1. 启动：读取配置 → 校验管理员权限 → 创建共享内存 → 安装键盘钩子。
2. 窗口扫描：匹配 DNF 窗口 → 写入前台 PID → 自动暂停/恢复。
3. 按键捕获：钩子回调 → 更新键盘状态与边沿计数 → 套用方案掩码。
4. 共享内存：写入快照（seq 无锁一致性）并持续心跳。
5. 后台伪造：dnfinput Hook `GetAsyncKeyState` / `GetKeyboardState` /
   `GetDeviceState` / `GetRawInputData` / `GetRawInputBuffer` 并返回伪造状态。
   - 若检测到后台丢弃输入，则启用前台欺骗（仅后台、未暂停时生效）。
6. 暂停/恢复：触发清键与暂停标志 → 更新 UI/日志。

### 2.4 伪造输入模块（dnfinput，原生 DLL）

`dnfinput.dll` 作为被注入模块，负责 Hook `GetAsyncKeyState`/`GetKeyboardState`/
`GetDeviceState` / `GetRawInputData` / `GetRawInputBuffer`，
读取共享内存快照并按方案伪造返回，同时记录调用统计与伪造命中率。

关键要点：
- 目录：`dnfinput/`，输出 `dnfinput.dll`（x86）
- Hook 库：MinHook（已纳入 `dnfinput/third_party/`）
- 日志：`<dnfinput.dll 所在目录>\\logs\\dnfinput_<pid>.log`
- 成功标记：`<dnfinput.dll 所在目录>\\successfile_dnfinput_<pid>.txt`（进程退出自动删除）
- 伪造延迟：环境变量 `DNFSYNC_SPOOF_DELAY_MS`（默认 5000ms），注入后延迟期内仅统计不伪造

构建命令（示例）：
1. `cmake -S "dnfinput" -B "dnfinput/build" -A Win32`
2. `cmake --build "dnfinput/build" --config Release`

---

## 3. 核心技术细节 (Deep Dive)

### 3.1 伪造语义：GetAsyncKeyState / GetKeyboardState

- `GetAsyncKeyState` 高位表示“当前是否按下”，低位表示“自上次调用以来是否按下过”。  
- UI 侧维护 `keyboardState[256]` 与 `edgeCounter[256]`：  
  - `keyboardState` 的 `0x80` 表示按下状态。  
  - `edgeCounter` 用于生成低位语义（与注入端本地缓存比较）。  
- `GetKeyboardState` 先取真实状态，再仅覆盖目标键的 `0x80/0x01`，避免干扰非目标键。  
- **Blacklist 模式**：黑名单键在同步生效时强制抬起，避免真实输入穿透。  
- 失联或暂停时强制清键，防止后台卡键。  

### 3.1.1 DirectInput（GetDeviceState）伪造语义

- DirectInput 键盘状态固定 256 字节数组（DIK 扫描码索引）。
- 先透传原始状态，再对 `targetMask` 命中的键覆盖 `0x80` 按下位。  
- **BlockMask**：用于强制拦截黑名单键（例如 F12），即使上报为 Mapping 模式也生效。  
- vKey → DIK 使用 `MapVirtualKeyW(MAPVK_VK_TO_VSC_EX)` 映射，扩展键补 `0x80`。
- 失联/暂停时对目标键强制清零，避免后台卡键。

### 3.1.2 RawInput（GetRawInputData / GetRawInputBuffer）伪造语义

- 仅处理键盘 RawInput（`RIM_TYPEKEYBOARD`），避免影响鼠标/其他 HID。
- 读取共享内存快照，根据 `targetMask` 与 `keyboardState` 对目标键修正
- 读取 `blockMask`，对黑名单键强制抬起，防止真实输入穿透
  `Make/Break`（仅在期望状态与事件不一致时改写），减少吞键风险。
- 暂停或失联时对目标键输出抬起（Break），避免后台卡键或继续响应。
- Mapping 模式使用目标键序列重写事件，确保源键映射到目标键后仍能生效。

### 3.1.3 消息层修正（WM_INPUT wParam）

- 某些客户端在后台接收到 `WM_INPUT` 时，仅当 `wParam == RIM_INPUT` 才处理，
  而 `RIM_INPUTSINK` 会被忽略，导致后台 RawInput 不生效。
- Hook `GetMessageW/A` 与 `PeekMessageW/A`：
  - 仅当共享内存存活且未暂停时启用修正。
  - 仅对键盘 RawInput 生效（用 `GetRawInputData(RID_HEADER)` 判定）。
  - 将 `wParam` 从 `RIM_INPUTSINK` 改为 `RIM_INPUT`，模拟前台输入语义。
- 目的：让后台窗口也能处理 RawInput 键盘事件，避免特定键位失效。

### 3.1.4 前台欺骗（Focus Spoof）

- Hook `GetForegroundWindow` / `GetActiveWindow` / `GetFocus`，让后台进程在
  读取窗口焦点时返回自身主窗口句柄。
- 仅在共享内存存活、未暂停、且当前进程不是前台 DNF 时启用，避免干扰前台。

**目的**：某些客户端在后台会直接跳过输入处理，前台欺骗可解除该限制。

### 3.2 窗口识别策略
- 标题关键字匹配：窗口标题包含 `DNF Taiwan`。
- 进程名校验：进程名等于 `dnf.exe` 时更可信。
- 可见性与有效性：`IsWindowVisible` + 句柄有效性检测。

### 3.3 前台切换与暂停策略
- 前台非 DNF：立即暂停同步，防止误操作。
- DNF 窗口间切换：前台窗口变为新的主控，旧主控转为从控。
- 窗口关闭：从列表剔除，并重选主控。

### 3.4 按键状态机与防卡键
- 记录每个键的 `Down/Up` 状态并维护边沿计数。
- 重复 Down 事件根据策略过滤，减少无效状态抖动。
- 暂停/切模式瞬间：清空状态并设置 `FlagClear`，由注入端对目标键强制清零。

### 3.5 方案配置与键位规则
- 配置文件路径：%AppData%\DNFSyncBox\profiles.json
- activeProfile 指向当前方案 ID
- mode 支持：
  - All：全键伪造
  - Whitelist：仅伪造 keys 中列出的键
  - Blacklist：伪造除 keys 外的所有键（黑名单键在同步生效时强制抬起）
  - Mapping：将 mappings 中的源键映射到目标键
- mappingBehavior（可选）：
  - None：不启用覆盖式映射（默认）
  - Replace：在非 Mapping 模式下将源键替换为目标键输出
- 键名使用 System.Windows.Forms.Keys 枚举名称（大小写不敏感）

**配置示例：**

```json
{
  "activeProfile": "all_except_f12",
  "profiles": [
    {
      "id": "all_except_f12",
      "mode": "Blacklist",
      "keys": ["F12"],
      "mappings": {
        "Q": "Oem4",
        "D": "L",
        "F": "OemSemicolon",
        "G": "Oem7",
        "C": "Oem6"
      },
      "mappingBehavior": "Replace"
    },
    { "id": "full", "mode": "All" },
    { "id": "wasd", "mode": "Whitelist", "keys": ["W", "A", "S", "D", "Space"] },
    { "id": "arrows", "mode": "Blacklist", "keys": ["F1", "F2", "F3"] },
    { "id": "map1", "mode": "Mapping", "mappings": { "W": "Up", "A": "Left", "S": "Down", "D": "Right" } }
  ]
}
```

**加载规则：**
- 配置不存在时会自动生成默认配置（all_except_f12/Blacklist + Replace 映射）。
- 键名解析使用 Enum.TryParse<Keys>(ignoreCase:true)，非法键名会被忽略并记录日志。
- Mapping 模式仅伪造映射目标键，源键本身不会被透传。
- Replace 模式在非 Mapping 下生效：源键被替换为目标键输出，源键本身被屏蔽。
- **Replace + RawInput**：为保证映射在 RawInput 下可用，会向注入端上报 `profileMode=Mapping`，
  以触发“生成映射事件”的路径；日志中的 Mode 将显示为 3。

## 4. 详细开发步骤 (Step-by-Step)

### 第一阶段：基础设施
1. 创建 WPF 项目：命名为 `DNFSyncBox`。
2. 封装 Win32 API：创建 `NativeMethods.cs` 类。

### 第二阶段：窗口识别器
1. 编写 `WindowManager` 类。
2. 实现 `RefreshWindows()` 方法：
   - 标题关键字 `DNF Taiwan` 过滤。
   - 进程名 `dnf.exe` 校验。
   - 主控判定：`GetForegroundWindow()` 在 DNF 列表中。
3. 句柄维护与失效回收。

### 第三阶段：输入监听与状态机
1. 编写 `KeyboardHook` 类并安装全局钩子。
2. `KeyStateTracker` 记录按键状态与去重。

### 第四阶段：共享内存与伪造
1. 设计共享内存结构（seq、keyboardState、edgeCounter、targetMask、blockMask 等）。
2. UI 写入快照并维持心跳（GetTickCount64）。
3. dnfinput Hook `GetAsyncKeyState` / `GetKeyboardState` 读取快照并伪造。

### 第五阶段：业务逻辑与 UI 整合
1. 暂停逻辑：检测 `Alt + .` 切换状态并清键。
2. 方案切换：通过 `profiles.json` 热切换 activeProfile。
3. UI 绑定：显示状态、窗口数量、日志列表。

---

## 5. 关键代码参考 (NativeMethods.cs)

将以下代码放入你的项目中，解决 API 声明问题：

```csharp
using System;
using System.Runtime.InteropServices;

public static class NativeMethods
{
    // 消息常量
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // 键盘钩子
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // 键盘状态（用于获取切换态）
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKeyboardState(byte[] lpKeyState);

    // 窗口与前台
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
```

---

## 6. 风险与注意事项 (Safety & Optimization)

1. 管理员权限必需：非管理员启动直接退出并提示。
2. 前台非 DNF：必须立即暂停同步，避免误投。
3. 组合键冲突：默认避免系统级快捷键（Win、Alt+Tab 等）。
4. 失联恢复：共享内存心跳超时则强制清键并等待恢复。

---

## 7. MVP 范围 (Minimum Viable Product)

- DNF 窗口识别 + 自动主控切换  
- 全局键盘钩子 + 共享内存快照写入  
- 伪造 Hook（GetAsyncKeyState/GetKeyboardState）  
- 方案配置（profiles.json）+ 暂停/清键  
- 基础 UI + 日志  
- 基础配置（热键、方案）

---

## 8. 测试与验证 (Testing)

- 单元测试：
  - `lParam` 构建正确性
  - 过滤器逻辑与状态机一致性
- 集成验证：
  - 多窗口切换与暂停行为
  - 非 DNF 前台立即暂停

---

## 9. 消息欺骗技术验证（新增）

为确认 DNF 实际输入路径，新增最小验证组件（不改主流程，仅用于实验验证）：

- **DNFSyncBox.Agent**：进程内代理，钩 `GetAsyncKeyState` 与 DirectInput 关键调用，记录调用频次并可伪造方向键状态。
- **DNFSyncBox.Injector**：注入器，发现 `dnf.exe` 并执行注入，通过命名管道发送测试指令。

**验证步骤（Windows 环境，需 .NET Framework 4.8 目标包）：**

1. 构建：`dotnet build "src/DNFSyncBox.Agent/DNFSyncBox.Agent.csproj"`
2. 构建：`dotnet build "src/DNFSyncBox.Injector/DNFSyncBox.Injector.csproj"`
3. 运行：`dotnet run --project "src/DNFSyncBox.Injector/DNFSyncBox.Injector.csproj"`
   - 若提示未找到代理 DLL，可指定路径：`--agent "E:/code/dnf-syn/src/DNFSyncBox.Agent/bin/Debug/net48/DNFSyncBox.Agent.dll"`
4. 观察 `stats` 输出判断输入路径（GetAsyncKeyState vs DirectInput）。

**日志输出：** `%AppData%/DNFSyncBox/logs/agent-<pid>.log`。
