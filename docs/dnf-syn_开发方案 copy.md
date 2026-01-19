# dnf-syn_开发方案

**版本：** 1.1
**技术栈：** C# / .NET 6 (WPF)
**目标：** 实现前台窗口输入操作对后台窗口的实时同步，专用于 2012版 DNF 客户端。

---

## 1. 项目概述 (Overview)

本项目旨在开发一个辅助工具，用于在“一拖多”场景下，将主控窗口（前台）的键盘按键指令，实时、准确地投递给其他被控窗口（后台）。

### 核心需求
- 自动识别 DNF 窗口并维护主控/从控关系。
- 后台同步：窗口非激活也能接收按键指令。
- 控制灵活：支持全局快捷键（默认 `Alt + .`）暂停/恢复同步。
- 同步模式：
  - 默认：仅方向键同步。
  - 增强：除 F1-F12 外的常用键 + 功能区键（PrintScreen/Delete 等）。
  - 允许白名单配置覆盖默认模式。

### 关键约束与决策
- 窗口标题关键字匹配：`DNF Taiwan`（优先）；进程名 `dnf.exe` 作为辅助校验。
- 必须管理员权限启动；非管理员直接退出并提示原因。
- 自动前台判断：前台非 DNF 时立即暂停同步。
- 前台从 DNF 窗口1切换到窗口2时，窗口2自动成为主控并切换同步目标。

---

## 2. 技术架构 (Architecture)

### 2.1 技术选型
- 语言：C#
- 框架：.NET 6
- UI：WPF
  - 理由：便于制作悬浮状态窗与主面板。
- 底层交互：Windows API (P/Invoke)
  - 核心库：`user32.dll`
  - 核心机制：Windows Message Queue (`PostMessage`)

### 2.2 模块划分
1. UI 层（View）
   - 主控制面板（状态、模式、热键、日志）。
   - 状态浮层（Overlay，二期可选）。
2. 窗口管理层（Window Manager）
   - `EnumWindows` 扫描、过滤与维护句柄列表。
   - 主控判定与失效回收。
3. 输入监听层（Input Listener）
   - 全局键盘钩子 `WH_KEYBOARD_LL` 捕获原始按键。
4. 核心分发层（Dispatcher）
   - 过滤器：暂停/模式/白名单/前台校验。
   - 注入器：构建 `lParam` 并投递。
5. 按键状态层（KeyStateTracker）
   - 记录按下/抬起状态，避免重复投递与卡键。
6. 配置层（AppConfig）
   - 热键、模式、白名单、窗口过滤规则、日志级别。
7. 日志与诊断层（LogService）
   - 事件记录、统计计数、故障信息。

### 2.3 关键流程
1. 启动：读取配置 → 校验管理员权限 → 启动扫描 → 安装钩子。
2. 窗口扫描：匹配 DNF 窗口 → 更新主控/从控集合。
3. 按键捕获：钩子回调 → 状态机去重 → 过滤器判断。
4. 投递：构建 ScanCode `lParam` → `PostMessage` 到从控窗口。
5. 暂停/恢复：切换状态 → 立即清键 → 更新 UI/日志。

---

## 3. 核心技术细节 (Deep Dive)

### 3.1 解决“后台不响应”的关键：ScanCode 构建

DNF 读取按键时，不仅看虚拟键码 (Virtual Key Code)，还会检查硬件扫描码 (Scan Code)。如果直接发送简单的 `WM_KEYDOWN` 而不构建正确的 `lParam`，游戏角色不会移动。

**lParam 二进制结构图 (32位整数):**

- 0-15 位：重复计数（通常为 1）。
- 16-23 位：扫描码（`MapVirtualKey` 获取）。
- 24 位：扩展键标志（方向键、Insert、Delete 等需置 1）。
- 29 位：Context Code（Alt 键是否按下）。
- 30 位：上一次按键状态（0=Up, 1=Down）。
- 31 位：转换状态（0=Press, 1=Release）。

### 3.2 窗口识别策略
- 标题关键字匹配：窗口标题包含 `DNF Taiwan`。
- 进程名校验：进程名等于 `dnf.exe` 时更可信。
- 可见性与有效性：`IsWindowVisible` + 句柄有效性检测。

### 3.3 前台切换与暂停策略
- 前台非 DNF：立即暂停同步，防止误操作。
- DNF 窗口间切换：前台窗口变为新的主控，旧主控转为从控。
- 窗口关闭：从列表剔除，并重选主控。

### 3.4 按键状态机与防卡键
- 记录每个键的 `Down/Up` 状态。
- 重复 Down 事件根据策略过滤或限频。
- 暂停/切模式瞬间：遍历已按下键集合，发送 `WM_KEYUP`。

### 3.5 按键过滤与模式
- 方向键模式：仅同步方向键（含小键盘方向键）。
- 增强模式（默认白名单）：
  - 字母：A-Z。
  - 数字：主键盘数字键 1-6（D1-D6，非小键盘）。
  - 排除：Escape、CapsLock、F1-F12、LWin/RWin、Apps、Ctrl/Shift/Alt、Pause/Break 等系统保留键。
- 自定义模式：第三套方案，用户显式指定可同步按键集合。

**按键配置模板（使用 `System.Windows.Forms.Keys` 枚举名称）：**

```json
{
  "syncMode": "Enhanced",
  "allowSystemKeys": false,
  "whitelists": {
    "direction": ["Up", "Down", "Left", "Right"],
    "enhanced": [
      "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
      "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
      "D1", "D2", "D3", "D4", "D5", "D6"
    ],
    "custom": ["A", "S", "D", "F", "J", "K", "L", "D1", "D2", "D3"]
  }
}
```

**键位名速查表（常见 Keys -> 物理键）：**
- 字母：`A`-`Z` -> 键盘字母区 A-Z。
- 数字（主键盘）：`D0`-`D9` -> 数字键 0-9（非小键盘）。
- 数字（小键盘）：`NumPad0`-`NumPad9` -> 小键盘数字 0-9。
- 方向键：`Up`/`Down`/`Left`/`Right` -> 方向键。
- 空格/制表/回车/退格：`Space`/`Tab`/`Enter`/`Back`。
- 逗号句点与符号：`Oemcomma`/`OemPeriod`/`OemMinus`/`Oemplus` 等 -> 主键盘符号键。
- 小键盘运算：`Add`/`Subtract`/`Multiply`/`Divide`/`Decimal`。
- 功能区：`PrintScreen`/`Insert`/`Delete`/`Home`/`End`/`PageUp`/`PageDown`。
- 系统键（默认排除）：`LWin`/`RWin`/`Apps`/`LControl`/`RControl`/`LShift`/`RShift`/`LMenu`/`RMenu`/`Escape`/`CapsLock`。

**Oem* 符号键映射（美式/台式差异说明）：**
- 美式 101/104 键位（Windows 常见默认）：
  - `OemMinus` -> `-` `_`
  - `Oemplus` -> `=` `+`
  - `OemOpenBrackets` -> `[` `{`
  - `OemCloseBrackets` -> `]` `}`
  - `OemBackslash`/`OemPipe` -> `\` `|`
  - `OemSemicolon` -> `;` `:`
  - `OemQuotes` -> `'` `"`
  - `Oemcomma` -> `,` `<`
  - `OemPeriod` -> `.` `>`
  - `OemQuestion` -> `/` `?`
  - `Oemtilde` -> `` ` `` `~`
- 台式键盘（台湾常见物理布局）：
  - 物理键位基本与美式 101/104 相同，差异主要来自输入法（注音/仓颉）而非硬件。
  - 若使用 102 键位（ISO，左 Shift 旁多一键），该键通常映射为 `Oem102`（`<` `>` `|`）。
- 说明：以上映射依赖系统当前键盘布局，实际键位应以 `MapVirtualKey` 与实测为准。

**OEM 符号键清单（Keys 名称与美式典型字符）：**
- `OemSemicolon`（`Oem1`）：`;` `:`
- `OemPlus`：`=` `+`
- `OemComma`：`,` `<`
- `OemMinus`：`-` `_`
- `OemPeriod`：`.` `>`
- `OemQuestion`（`Oem2`）：`/` `?`
- `Oemtilde`（`Oem3`）：`` ` `` `~`
- `OemOpenBrackets`（`Oem4`）：`[` `{`
- `OemPipe`（`Oem5`）：`\` `|`
- `OemCloseBrackets`（`Oem6`）：`]` `}`
- `OemQuotes`（`Oem7`）：`'` `"`
- `Oem8`：杂项键（布局相关）
- `Oem102`：ISO 102 键位 `<` `>` `|`

**如何快速识别当前系统键盘布局：**
- Windows 设置：进入“时间和语言 → 语言和区域 → 键盘”，查看当前输入语言与键盘布局名称。
- 控制面板：进入“区域 → 管理 → 更改系统区域设置”，确认系统默认区域与键盘布局。
- 输入法托盘：查看当前输入法（如“ENG-US”、“中文(繁体)-台湾”），以判断布局差异。
- 实测验证：用调试输出打印 `Keys` 与 `ScanCode`，按下 `Oem*` 相关物理键确认映射是否符合预期。

**白名单/黑名单最终常量清单（可直接落地为代码常量）：**
```csharp
// 方向键模式白名单
static readonly HashSet<Keys> DirectionWhitelist = new()
{
    Keys.Up, Keys.Down, Keys.Left, Keys.Right
};

// 增强模式默认白名单：字母 + 主键盘数字 1-6
static readonly HashSet<Keys> EnhancedWhitelist = new()
{
    Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G, Keys.H, Keys.I, Keys.J,
    Keys.K, Keys.L, Keys.M, Keys.N, Keys.O, Keys.P, Keys.Q, Keys.R, Keys.S, Keys.T,
    Keys.U, Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z,
    Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6
};

// 黑名单：系统保留键与边界键（无论白名单是否包含，最终都会剔除）
static readonly HashSet<Keys> Blacklist = new()
{
    Keys.Escape, Keys.CapsLock,
    Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6,
    Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12,
    Keys.LWin, Keys.RWin, Keys.Apps,
    Keys.LControlKey, Keys.RControlKey, Keys.ControlKey,
    Keys.LShiftKey, Keys.RShiftKey, Keys.ShiftKey,
    Keys.LMenu, Keys.RMenu, Keys.Menu,
    Keys.Pause
};
```

**配置加载与校验规则：**
- 配置缺失或字段不存在：使用内置默认值（方向键模式固定，增强模式使用默认白名单）。
- `syncMode` 仅允许：`Direction`、`Enhanced`、`Custom`，其余值回退为 `Direction` 并记录告警。
- 键名解析：使用 `Enum.TryParse<Keys>(ignoreCase: true)`；非法键名忽略并记录。
- 去重与排序：白名单去重后保持原顺序，不强制排序。
- 黑名单强制剔除：`Escape`、`CapsLock` 及系统保留键，即使出现在白名单也会移除。
- 空白名单策略：
  - `Enhanced`：若用户配置为空，使用内置默认白名单。
  - `Custom`：若为空或全部无效，自动回退为 `Direction` 并提示。
- 系统键开关：提供 `allowSystemKeys`（默认 `false`），仅在 `true` 时允许非黑名单系统键进入白名单；黑名单键始终强制剔除。

**配置格式与持久化设计：**
- 配置文件路径（默认）：`%AppData%/DNFSyncBox/config.json`。
- 便携模式路径（可选）：与程序同目录的 `config.json`，优先级高于 AppData。
- 加载顺序（优先级从低到高）：
  1. 内置默认配置（写死在代码中）。
  2. AppData 配置文件。
  3. 程序同目录配置文件（便携模式）。
  4. 启动参数/运行时覆盖（如未来支持）。
- 持久化写入：写入 `config.json.tmp` 后原子替换，避免写到一半导致配置损坏。
- 版本字段：建议增加 `configVersion`，用于后续字段迁移与兼容。
- 热更新策略：
  - 使用 `FileSystemWatcher` 监听配置文件变更，500ms 防抖处理。
  - 变更后先校验，失败则保留旧配置并记录告警。
  - 可热更新项：`syncMode`、`whitelists`、`allowSystemKeys`。
  - 需重启项：全局热键、窗口过滤策略等与钩子生命周期强相关的配置。

**配置加载器方案（采用方案 B：分层合并 + 热更新）：**
- 启动时分层加载并合并，运行中使用 `FileSystemWatcher` 监听配置变更。
- 变更后先做校验；校验失败则回滚到上次有效配置，并记录告警日志。
- 通过 500ms 防抖避免短时间内重复触发导致的抖动。

**配置加载器伪代码流程：**
```text
LoadConfig():
  defaults = BuildDefaults()
  appData = TryReadJson(appDataPath)
  portable = TryReadJson(portablePath)

  merged = Merge(defaults, appData)
  merged = Merge(merged, portable)

  if !Validate(merged):
      LogWarn("配置非法，回退上次有效配置")
      return lastKnownGood ?? defaults

  lastKnownGood = merged
  return merged

StartWatch():
  Watch(configPath, debounce=500ms)
  OnChange:
    candidate = LoadConfig()
    if candidate != lastKnownGood:
        ApplyHotReload(candidate)
```

**配置字段完整清单：**
- `configVersion`：整数，配置版本号，用于迁移（默认 `1`）。
- `syncMode`：字符串，`Direction`/`Enhanced`/`Custom`（默认 `Enhanced`）。
- `allowSystemKeys`：布尔，是否允许非黑名单系统键（默认 `false`）。
- `whitelists.direction`：数组，方向键白名单（默认 `["Up","Down","Left","Right"]`）。
- `whitelists.enhanced`：数组，增强模式白名单（默认 `A-Z` + `D1-D6`）。
- `whitelists.custom`：数组，自定义白名单（默认空数组）。
- `hotkey.pause`：数组，暂停快捷键组合（默认 `["Alt","."]`）。
- `windowMatch.titleKeyword`：字符串，窗口标题关键字（默认 `DNF Taiwan`）。
- `windowMatch.processName`：字符串，进程名（默认 `dnf.exe`）。
- `windowMatch.requireForeground`：布尔，前台必须是 DNF 才同步（默认 `true`）。
- `scan.intervalMs`：整数，窗口扫描间隔毫秒（默认 `1000`）。
- `logging.level`：字符串，`Info`/`Warn`/`Error`（默认 `Info`）。
- `logging.maxItems`：整数，日志环形缓冲上限（默认 `200`）。

**示例默认配置（config.json）：**
```json
{
  "configVersion": 1,
  "syncMode": "Enhanced",
  "allowSystemKeys": false,
  "whitelists": {
    "direction": ["Up", "Down", "Left", "Right"],
    "enhanced": [
      "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
      "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
      "D1", "D2", "D3", "D4", "D5", "D6"
    ],
    "custom": []
  },
  "hotkey": {
    "pause": ["Alt", "."]
  },
  "windowMatch": {
    "titleKeyword": "DNF Taiwan",
    "processName": "dnf.exe",
    "requireForeground": true
  },
  "scan": {
    "intervalMs": 1000
  },
  "logging": {
    "level": "Info",
    "maxItems": 200
  }
}
```

**AppConfig 类定义草案（与配置字段对齐）：**
```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class AppConfig
{
    [JsonPropertyName("configVersion")]
    public int ConfigVersion { get; set; } = 1;

    [JsonPropertyName("syncMode")]
    public string SyncMode { get; set; } = "Enhanced";

    [JsonPropertyName("allowSystemKeys")]
    public bool AllowSystemKeys { get; set; } = false;

    [JsonPropertyName("whitelists")]
    public WhitelistConfig Whitelists { get; set; } = new();

    [JsonPropertyName("hotkey")]
    public HotkeyConfig Hotkey { get; set; } = new();

    [JsonPropertyName("windowMatch")]
    public WindowMatchConfig WindowMatch { get; set; } = new();

    [JsonPropertyName("scan")]
    public ScanConfig Scan { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();
}

public sealed class WhitelistConfig
{
    [JsonPropertyName("direction")]
    public List<string> Direction { get; set; } = new() { "Up", "Down", "Left", "Right" };

    [JsonPropertyName("enhanced")]
    public List<string> Enhanced { get; set; } = new()
    {
        "A","B","C","D","E","F","G","H","I","J","K","L","M",
        "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
        "D1","D2","D3","D4","D5","D6"
    };

    [JsonPropertyName("custom")]
    public List<string> Custom { get; set; } = new();
}

public sealed class HotkeyConfig
{
    [JsonPropertyName("pause")]
    public List<string> Pause { get; set; } = new() { "Alt", "." };
}

public sealed class WindowMatchConfig
{
    [JsonPropertyName("titleKeyword")]
    public string TitleKeyword { get; set; } = "DNF Taiwan";

    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "dnf.exe";

    [JsonPropertyName("requireForeground")]
    public bool RequireForeground { get; set; } = true;
}

public sealed class ScanConfig
{
    [JsonPropertyName("intervalMs")]
    public int IntervalMs { get; set; } = 1000;
}

public sealed class LoggingConfig
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "Info";

    [JsonPropertyName("maxItems")]
    public int MaxItems { get; set; } = 200;
}
```

**序列化说明：**
- 建议使用 `System.Text.Json`，开启不区分大小写的反序列化（`PropertyNameCaseInsensitive = true`）。
- 采用默认值初始化，避免字段缺失导致空引用。
### 3.6 权限与降级策略
- 必须管理员权限启动；非管理员直接退出并提示。
- 投递失败时默认降级为“方向键模式”，并记录失败窗口与原因。
- 连续失败阈值：触发重新扫描或自动暂停。

---

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

### 第四阶段：消息投递
1. 实现 `KeySender.PostKey(IntPtr hWnd, Keys key, bool isDown)`。
2. 构建 ScanCode 与 `lParam` 并投递到从控窗口。

### 第五阶段：业务逻辑与 UI 整合
1. 暂停逻辑：检测 `Alt + .` 切换状态并清键。
2. 模式切换：方向键/增强模式/白名单。
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

    // 发送消息
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, UIntPtr lParam);

    // 扫描码转换
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

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
4. 失败恢复：投递失败触发降级与重新扫描。

---

## 7. MVP 范围 (Minimum Viable Product)

- DNF 窗口识别 + 自动主控切换  
- 全局键盘钩子 + ScanCode 投递  
- 方向键模式 + 暂停/清键  
- 基础 UI + 日志  
- 基础配置（热键、模式）

---

## 8. 测试与验证 (Testing)

- 单元测试：
  - `lParam` 构建正确性
  - 过滤器逻辑与状态机一致性
- 集成验证：
  - 多窗口切换与暂停行为
  - 非 DNF 前台立即暂停
