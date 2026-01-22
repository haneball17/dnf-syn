# DNF Sync Box - 键盘状态伪造同步实现方案

一句话概述：通过共享内存（seq 无锁快照）把前台 UI 的全键盘状态同步到后台
DNF 进程，在被注入的 `dnfinput.dll` 中 Hook Win32/DirectInput
读取路径（`GetAsyncKeyState`/`GetKeyboardState`/`GetDeviceState`）进行
伪造返回，支持多种配置方案切换。

---

## 1. 需求确认与设想验证

- 你提出的判断：当前阶段属于“给出方案”而非直接实现 → **正确**。
- 日志显示 DirectInput 调用频繁，需补齐 `GetDeviceState` 伪造。
- 目标调整：从“仅方向键”扩展为“全按键可伪造 + 方案可配置”。

> 说明：该变更超出 MVP 方向键范围，需同步更新设计文档与 UI 配置能力。

---

## 2. 总体架构

### 2.1 组件划分

- **WPF 主程序（控制端）**
  - 捕获前台键盘输入，维护键盘状态快照。
  - 写入共享内存（包含心跳、配置方案、前台 PID）。
  - 负责自动暂停、前台切换与清键策略。

- **`dnfinput.dll`（被注入端）**
  - Hook `GetAsyncKeyState` / `GetKeyboardState` / `GetDeviceState`。
  - 通过 `DirectInput8Create`/`CreateDevice` 安装设备级 Hook。
  - 读取共享内存快照，按方案伪造返回。
  - 对前台 PID 做旁路：前台进程优先透传，后台进程伪造。

- **注入器**
  - 负责把 `dnfinput.dll` 注入目标 `dnf.exe` 进程。

### 2.2 输出产物

- `dnfinput.dll`（x86 原生 DLL）
- 现有 C# WPF UI（新增共享内存与方案配置）
- 现有注入器（不变）

> 结论：最终产物是 **一个 DLL + 现有 C# UI 界面**（不需要新增 UI 工程）。

---

## 3. IPC 设计（共享内存 + seq 无锁快照）

### 3.1 设计原因与优势

- **低延迟**：共享内存读写纳秒级，适合高频键盘轮询。
- **低干扰**：不用频繁跨进程调用，避免卡顿与抢占。
- **可控一致性**：使用 `seq` 实现无锁快照，确保读到完整状态。

### 3.2 数据结构建议（V1）

> 说明：这里是设计建议，实际结构可根据实现细化。

- `version`：结构版本，便于兼容升级
- `seq`：写入序号，奇数表示写入中，偶数表示稳定态
- `flags`：暂停/清键/旁路等控制位
- `activePid`：当前前台 DNF 进程 PID，用于旁路控制
- `profileId`：当前方案 ID
- `profileMode`：当前方案模式（All/Whitelist/Blacklist/Mapping）
- `lastTick`：心跳（GetTickCount64）
- `keyboardState[256]`：对齐 `GetKeyboardState` 语义（按下=0x80）
- `edgeCounter[256]`：按键按下事件计数（用于 `GetAsyncKeyState` 低位）
- `targetMask[256]`：目标键覆盖掩码（1 表示覆盖）

### 3.3 快照读取示例（读端）

1. 读 `seq`（必须是偶数）
2. 拷贝所有字段
3. 再读 `seq`，若不一致则重试

**优势举例**：
- UI 正在更新键盘状态时，后台读到的依然是完整一致的旧快照。

---

## 4. 伪造策略与键盘语义

### 4.1 Hook 目标

- `GetAsyncKeyState(int vKey)`
- `GetKeyboardState(BYTE* lpKeyState)`
- `IDirectInputDevice8::GetDeviceState(DWORD, LPVOID)`
- `GetForegroundWindow / GetActiveWindow / GetFocus`（前台欺骗）

### 4.2 伪造逻辑

- **GetKeyboardState**
  - 先调用原函数得到真实状态
  - 再按方案覆盖目标键的 `0x80`（按下）与 `0x01`（切换态）
  - **优势**：非目标键保持真实，不影响系统快捷键

- **GetAsyncKeyState**
  - 返回值高位表示“当前是否按下”
  - 低位表示“自上次调用以来是否按下过”
  - 设计：
    - 高位：来自共享内存 `keyboardState[vk]`
    - 低位：比较 `edgeCounter[vk]` 与本地缓存，变化则置 1

**示例**：
- UI 报告 `VK_LEFT` 按下 → 返回 `0x8001`
- UI 报告 `VK_LEFT` 释放 → 返回 `0x0000`

### 4.3 DirectInput 伪造要点

- `GetDeviceState` 先透传原始结果，再按方案覆盖键盘状态数组。
- DirectInput 键盘状态固定 256 字节，索引为 DIK 扫描码。
- 通过 `MapVirtualKeyW(MAPVK_VK_TO_VSC_EX)` 将 vKey 映射为 DIK
  （扩展键补 `0x80`），只覆盖 `targetMask` 命中的键。

**原因**：DNF 在后台主要通过 DirectInput 轮询键盘，单靠 Win32
伪造无法同步到后台读取路径。

### 4.4 前台欺骗（Focus Spoof）

- Hook `GetForegroundWindow` / `GetActiveWindow` / `GetFocus`，当进程处于后台时
  伪造返回自身主窗口句柄。
- 仅在共享内存存活、未暂停且当前进程不是前台 DNF 时生效，避免干扰前台行为。

**原因**：部分客户端会在后台直接丢弃输入或跳过逻辑判断，通过前台欺骗可
让后台进程继续应用伪造的键盘状态。
---

## 5. 方案配置设计（全键伪造 + 可配置）

### 5.1 方案类型（建议）

- **All**：全键伪造（默认方案）
- **Whitelist**：仅伪造白名单键
- **Blacklist**：伪造除黑名单以外的所有键
- **Mapping**：按键映射（例如把 `W` 映射到 `↑`）

### 5.2 配置格式（建议 JSON）

配置文件：`%AppData%\\DNFSyncBox\\profiles.json`

```json
{
  "activeProfile": "full",
  "profiles": [
    {
      "id": "full",
      "mode": "All"
    },
    {
      "id": "wasd",
      "mode": "Whitelist",
      "keys": ["W", "A", "S", "D", "Space"]
    },
    {
      "id": "arrows",
      "mode": "Blacklist",
      "keys": ["F1", "F2", "F3"]
    },
    {
      "id": "map1",
      "mode": "Mapping",
      "mappings": {
        "W": "Up",
        "A": "Left",
        "S": "Down",
        "D": "Right"
      }
    }
  ]
}
```

**优势**：
- 易扩展、可热切换，便于快速回滚策略。

---

## 6. 前后台旁路与安全策略

### 6.1 前台旁路

- UI 写入 `activePid`，标记当前前台 DNF 进程
- 被注入进程若 PID == activePid → 仅透传不伪造

**原因**：避免前台窗口出现“双重输入”或干扰真实键盘。

### 6.2 自动清键与超时

- `lastTick` 超时（如 > 200ms）视为失联
- 失联或暂停时强制清键，避免卡键

---

## 7. 日志与诊断

- 每秒输出一次统计（总调用数、伪造命中数、方案 ID）
- Debug 扩展：记录少量键位样本用于回放定位
- Release 模式保守输出，避免性能波动

---

## 8. 风险与回退

- **风险**：某些系统组件依赖真实键盘状态
  - 回退策略：可切到 `Whitelist` 只伪造指定键
- **风险**：低位语义不完全一致
  - 回退策略：只返回高位；或在 UI 端强化按下事件计数
- **风险**：DirectInput 设备类型复杂
  - 回退策略：只在状态数组为 256 字节时覆盖，避免非键盘设备
- **风险**：前台欺骗可能影响窗口焦点判定
  - 回退策略：仅在后台/未暂停时启用，必要时关闭该 Hook

---

## 9. 里程碑计划（实现顺序）

1. 扩展共享内存结构与 UI 写入逻辑
2. Hook `GetAsyncKeyState` / `GetKeyboardState` 并注入验证
3. 加入方案配置与热切换
4. 验证前台旁路与超时清键
5. 文档与日志格式固化

---

## 10. 验证用例（示例）

1. 前台 DNF：不伪造，键盘正常
2. 后台 DNF：方向键/WASD 同步生效
3. 切换前台 → 后台清键、不卡键
4. 切换方案（All → Whitelist）观察行为变化

---

> 已落地约定：配置文件位于 `%AppData%\\DNFSyncBox\\profiles.json`，默认方案为 `full/All`，文件更新会自动热切换（约 1 秒内生效）。
