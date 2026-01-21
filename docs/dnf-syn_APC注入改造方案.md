# DNF Sync Box - APC 注入 DLL 改造方案（断链 & 抹头）

一句话概述：在保持 APC 注入器不变的前提下，提供一个原生 DLL 作为载荷，DLL 在 DllMain 中完成 LDR 断链 与 PE 抹头，并以最小副作用方式完成输入路径验证（GetAsyncKeyState / DirectInput）。

---

## 1. 背景与目标

背景：现有注入后游戏闪退，怀疑检测点包含模块枚举与加载行为。
目标：只改 DLL，使用 断链 + 抹头 降低被检测概率，并能稳定完成输入路径验证。

验证指标：
- 不闪退（能进入游戏并保持稳定）
- 日志能反映实际输入路径调用频率（GetAsyncKeyState / DirectInput）

---

## 2. 关键约束

1. APC 注入器已完成，不允许修改注入器逻辑。
2. DLL 必须实现 断链 与 抹头。
3. 仅验证输入路径，不引入复杂业务逻辑。
4. 仅方向键同步需求，不扩展功能范围。

---

## 3. 核心设计（DLL 侧）

### 3.1 DLL 类型选择

选择：原生 C++ DLL。
原因：APC 注入通常通过 LoadLibrary 触发 DllMain，原生 DLL 可直接执行断链和抹头；若使用纯托管 DLL，CLR 未加载时无法执行入口逻辑。
优势：稳定、可控，且与 断链/抹头 技术文档完全对齐。

### 3.2 断链（LDR Unlink）

目标：从 PEB 的三个模块链表中移除自身节点。
时机：DllMain 的 DLL_PROCESS_ATTACH 中最先执行。
原因：避免模块枚举（如 EnumProcessModules / Ldr 列表扫描）发现 DLL。
优势：减少被模块扫描器命中的概率。

示例说明：
- 注入完成后，反作弊遍历 InLoadOrderModuleList 时不再看到本 DLL。

### 3.3 抹头（Erase PE Header）

目标：将自身 PE 头部区域清零（常见为 4KB）。
时机：断链之后、初始化核心逻辑之前或之后，按依赖情况选择。
原因：降低内存扫描特征匹配。
优势：即便有人扫描内存，也难以通过 PE 头定位模块。

示例说明：
- 断链后仍可能被特征扫描检出，抹头可进一步降低风险。

注意点：
- 抹头可能影响依赖 PE 头的 API（如某些模块解析场景），因此需在必要 API 调用完成后再抹头。

### 3.4 线程模型（避免闪退）

原则：DllMain 只做最小动作，避免复杂逻辑。

建议策略：
1. DllMain 里执行：断链 -> 记录最小日志标记 -> 安排工作线程
2. 工作线程内完成：输入路径钩子安装、日志初始化、统计上报

风险提示：
- 若 CreateThread 被监控（文档提到的“CreateThread 陷阱”），可改用 APC/线程劫持模型启动工作逻辑。

---

## 4. 输入路径验证设计

### 4.1 统计目标

- GetAsyncKeyState 调用次数
- DirectInput8Create 调用次数
- IDirectInputDevice8::GetDeviceState 调用次数

判定规则：
- GetAsyncKeyState 频繁 -> 传统键盘状态路径
- GetDeviceState 频繁 -> DirectInput 路径
- 两者同时高频 -> 混合路径

### 4.2 实现方式

- Hook GetAsyncKeyState：仅统计次数，不影响原逻辑
- Hook DirectInput8Create：统计并进一步 Hook CreateDevice / GetDeviceState
- 统计结果写入日志文件（建议 %AppData%/DNFSyncBox/logs/）

示例：
- 如果日志显示 GetDeviceState 每秒上百次，说明 DNF 以 DirectInput 为主。

---

## 5. 关键流程（时序）

1. APC 注入器调用 LoadLibrary 加载 DLL。
2. DllMain 触发：执行断链，标记已加载。
3. （可选）抹头：按初始化依赖情况选择时机。
4. 启动工作线程：安装 Hook、输出统计日志。
5. 外部读取日志，判定输入路径。

---

## 6. 风险与回退

- 若断链后仍闪退：说明检测点更深（LdrLoadDll 或线程监控）。
- 若抹头后功能异常：调整抹头时机或缩小抹头范围。
- 若 Hook 触发崩溃：先关闭伪造，仅保留统计钩子。

---

## 7. 验证步骤

1. 构建 DLL（与 APC 注入器架构匹配：x86 或 x64）。
2. 运行 APC 注入器加载 DLL。
3. 观察是否闪退。
4. 查看日志统计，判断输入路径。

---

## 8. 结论与下一步

- 若验证通过，确认真实输入路径后再扩展伪造逻辑。
- 若仍闪退，需考虑 Manual Map 或线程劫持方案。

