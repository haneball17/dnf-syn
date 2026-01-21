# DNF Sync Box - DirectInput / RawInput 后台同步改造方案

一句话概述：在 APC/线程劫持 + x86 前提下，提供原生 DLL 载荷，完成断链与抹头，并以 DirectInput 与 RawInput 双路径确保后台输入同步稳定可用。

---

## 1. 设想验证（“结构完整”）

你的“结构完整”设想是正确的。
完整改造方案至少应覆盖：背景与目标、关键约束、总体架构、输入路径策略、注入与隐身策略、关键流程、风险与回退、验证步骤。

---

## 2. 重要前提与缺口说明

你指定参考的 `docs/DirectInput and RawInput 后台同步完整改造方案.md` 当前是空文件（0 字节），无法核对其具体内容。
如果你有该文档的内容，请补充后我可以对齐细节与命名。

---

## 3. 设计目标

1. 后台输入可用性优先：确保后台窗口接收方向键输入，尽量覆盖 DNF 的真实输入路径。
2. 稳定性优先：注入后不闪退，避免对主线程/关键线程造成干扰。
3. 可观测性：必须产生日志，能确认 DirectInput / RawInput 的调用路径与命中次数。

---

## 4. 关键约束

- 注入方式：APC / 线程劫持（避免 CreateThread 触发检测）。
- 架构：x86（与 DNF 2012 客户端一致）。
- 隐身要求：DLL 必须执行“断链”与“抹头”。
- 功能范围：仅方向键同步，不扩展到鼠标或宏功能。

---

## 5. 总体架构（设计）

### 5.1 组件划分

1. APC 注入器（外部）
   - 由你提供，负责将 DLL 注入到 DNF 进程内。
2. 原生 DLL 载荷（x86）
   - DllMain 执行断链/抹头。
   - 通过线程劫持或 APC 触发初始化逻辑。
   - 安装 DirectInput 与 RawInput 钩子。
3. 日志模块
   - 记录调用频次与关键路径判断结果。

### 5.2 为什么这样设计

- 分离注入与逻辑：注入器只负责注入，逻辑集中在 DLL，便于排错。
- 原生 DLL 更稳定：托管 DLL 依赖 CLR，容易在 APC 场景下失败。
- 双输入路径覆盖：DirectInput 与 RawInput 覆盖 DNF 的主流输入链路。

---

## 6. DirectInput 方案设计

### 6.1 目标钩子

- DirectInput8Create
- IDirectInput8::CreateDevice
- IDirectInputDevice8::GetDeviceState
- IDirectInputDevice8::GetDeviceData（用于统计）

### 6.2 核心逻辑

- 拦截 GetDeviceState，在键盘缓冲区叠加方向键状态。
  - 方向键扫描码：0xC8/0xD0/0xCB/0xCD（Up/Down/Left/Right）。

### 6.3 优势与例子

- 优势：直接作用于游戏真正读取输入的位置，后台窗口也能响应。
- 例子：如果日志显示 GetDeviceState 每秒高频调用，则判定 DirectInput 为主路径；此时叠加 0x80 状态可稳定驱动角色移动。

---

## 7. RawInput 方案设计

### 7.1 目标钩子

- RegisterRawInputDevices（观察是否注册 RIDEV_INPUTSINK）
- GetRawInputData / GetRawInputBuffer

### 7.2 核心逻辑

- 记录 RawInput 调用次数与设备类别。
- 必要时伪造 RAWINPUT 数据（方向键按下/抬起）。

### 7.3 优势与例子

- 优势：覆盖现代输入路径，支持后台输入（RIDEV_INPUTSINK）。
- 例子：若游戏注册了 RIDEV_INPUTSINK，说明它允许后台输入；此时 RawInput 伪造比窗口消息可靠。

---

## 8. 断链与抹头策略

### 8.1 断链（LDR Unlink）

- 目标：从 PEB 的三条链表移除自身。
- 时机：DllMain 的 DLL_PROCESS_ATTACH 首次执行。
- 原因：避免 EnumProcessModules / Ldr 枚举发现 DLL。

### 8.2 抹头（Erase PE Header）

- 目标：清除自身 PE 头（常见 4KB）。
- 时机：断链后、核心 API 解析完成后执行。
- 风险：抹头后部分模块解析 API 可能失效，需要控制时机。

---

## 9. 线程模型（APC/线程劫持）

- 原则：避免在 DllMain 直接 CreateThread。
- 推荐：
  1. DllMain 完成断链/抹头。
  2. 通过 APC 或线程劫持启动初始化逻辑。

优势：减少被线程创建监控检测到的概率。

---

## 10. 关键流程（时序）

1. 注入器 APC 加载 DLL。
2. DllMain 执行断链与抹头。
3. 通过 APC/线程劫持启动初始化。
4. 安装 DirectInput / RawInput 钩子。
5. 输出日志统计与路径判定。

---

## 11. 风险与回退

- 若断链后仍闪退：检测点可能在 LdrLoadDll 或线程行为。
- 若抹头导致异常：延迟抹头或缩小抹头范围。
- 若 Hook 崩溃：先关闭伪造，只保留统计钩子。

---

## 12. 验证步骤

1. 构建 x86 DLL。
2. 使用 APC 注入器加载 DLL。
3. 观察是否闪退。
4. 查看日志统计，判定真实输入路径。

---

## 13. 下一步

- 确认真实输入路径后，再扩展伪造输入逻辑。
- 若 APC 仍不稳定，评估 Manual Map 或线程劫持增强方案。

