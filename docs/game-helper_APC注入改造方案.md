# APC 注入改造方案（仅 version-inject）

一句话概述：本方案以 APC 注入替换旧 IME 注入，注入器采用控制台形态+配置文件+详细日志+失败重试；代码改动仅限 `version-inject` 目录。

---

## 1. 背景与目标

- 背景：旧 IME 注入在 Win11 环境下稳定性差，且与当前项目结构不匹配。
- 目标：以 APC 注入作为最终路线，确保在 DNF 2012（32位）环境中稳定加载 `version-inject` DLL。
- 原则：KISS、可读、低延迟；注入过程可观测（详细日志）。

---

## 2. 范围与非目标

### 范围（本次改动）
- 修改 `version-inject` 目录的 DLL 载荷逻辑与初始化方式。
- 在 `HelperStart` 中替换为 APC 控制台注入器实现。
- 不修改 `InjectDll`（MFC 注入器）。

### 非目标（明确不做）
- 不实现或改造 MFC 注入器。
- 不引入回退注入方式（如远程线程注入）。
- 不引入复杂框架或第三方依赖。

---

## 3. 关键约束与假设

- 游戏为 32 位程序，所有产物必须为 x86。
- 当前版本基址/偏移固定：人物基址 `0x1AC790C`，地图偏移 `0xB8`，类型偏移 `0x94`，坐标偏移 `0x18C/0x190/0x194`。
- `DNFHelper` 旧工程数据仅作参考，不可直接复用。
- APC 需线程进入 alertable 状态，不保证立即执行。

---

## 4. MVP（最小可用）

1. 控制台注入器（外部实现）支持 APC 注入，输出详细日志。
2. 注入器支持配置文件读取（进程名、DLL 路径、重试参数、日志路径）。
3. `version-inject` DLL 作为普通载荷加载，`DllMain` 仅创建工作线程。
4. 注入成功可验证（日志与 DLL 内部落地文件/日志）。
5. 注入失败有明确原因输出与重试机制。

---

## 5. 总体架构（文字描述）

- 控制台注入器：
  - 读取配置 -> 发现目标进程 -> 申请远程内存 -> 写入 DLL 路径 -> QueueUserAPC -> 记录日志 -> 失败重试。
- `version-inject` DLL：
  - `DllMain` 禁止线程回调 -> 启动独立线程 -> 初始化核心逻辑。

---

## 6. 详细设计

### 6.1 `version-inject` DLL 载荷

- `DllMain` 只做轻量操作：
  - `DisableThreadLibraryCalls`
  - `CreateThread` 启动工作线程
- 初始化线程执行核心逻辑与资源准备，避免阻塞游戏主线程。
- 关键流程需中文日志（文件落地或调试输出），用于验证加载结果。

#### 6.1.1 DLL 配置文件（game_helper.ini）

- 放置位置：优先与 `GameHelper.dll` 同目录；若不存在则尝试游戏目录。
- 作用：
  - 控制 DLL 启动延迟
  - 安全模式：仅加载 DLL，不写任何标记文件、不启动线程、不打补丁
  - 控制是否在启动时应用全屏攻击补丁
  - 线程级开关：禁用输入轮询/吸怪线程
  - 控制日志与 test_success.txt 输出目录
  - 默认关闭启动时补丁（防止版本不匹配导致闪退）

**示例：**
```ini
[startup]
startup_delay_ms=8000
safe_mode=false

[patch]
apply_fullscreen_attack_patch=false

[feature]
disable_input_thread=false
disable_attract_thread=false

[output]
output_dir=E:/tools/injector
```

### 6.2 控制台注入器（仅设计约束）

- 进程探测：循环检测 `dnf.exe`，间隔可配置。
- APC 注入：对目标进程所有线程执行 `QueueUserAPC`，并统计成功次数。
- 成功判断：
  - 逻辑成功条件：至少一个线程成功入队；
  - 业务成功条件：检测目标进程已加载 DLL（可选，需外部实现）。
- 日志要求：必须输出每一步关键状态与错误码。

### 6.3 配置文件（建议 INI）

**示例：**
```ini
[target]
process_name=dnf.exe
dll_path=C:/Users/User/Desktop/GameHelper.dll
scan_interval_ms=1000
inject_delay_ms=3000
watch_mode=true
exit_when_no_processes=false

[apc]
max_retries=5
retry_interval_ms=2000
module_check_timeout_ms=8000
module_check_interval_ms=200
module_check_extend_ms=5000

[log]
log_path=./logs/injector.log
log_level=INFO
log_format=json
log_pid_list=false
console_output=true
file_output=true
```

> 说明：字段名称统一为英文；路径优先使用英文与绝对路径，若 DLL 与注入器同目录可使用 `./GameHelper.dll`（注入器会自动转为绝对路径）。

**放置策略：**
- 默认读取：`injector.ini` 放在 `HelperStart.exe` 同目录（即 `$(TargetDir)`）。
- 构建建议：Release|Win32 后通过 PostBuild 自动拷贝到输出目录。
- 临时覆盖：可使用 `--config <path>` 指定配置文件路径。

### 6.4 失败重试策略（无回退）

- 重试触发条件：
  - 进程句柄获取失败（权限不足）
  - `VirtualAllocEx`/`WriteProcessMemory` 失败
  - 无任何线程 APC 入队成功
- 重试策略：
  - 固定次数与固定间隔（配置文件控制）
  - 失败达到上限后退出并给出明确原因

### 6.5 日志设计（注入过程）

- 日志格式：JSON Lines（每行一个 JSON 对象，便于解析与检索）
- 日志级别：`INFO / WARN / ERROR`
- 日志字段：时间戳、步骤、结果、错误码、重试计数、线程数量、成功入队数
- 关键输出点：
  - 进程发现
  - 多进程扫描与去重
  - 句柄获取
  - 远程内存分配
  - 路径写入
  - 线程枚举数量
  - APC 入队成功数
  - 模块加载校验（含延长等待）
  - 重试次数与最终结果

### 6.6 注入器伪代码（APC）

```
读取配置文件(英文键名)
初始化日志(JSON Lines)

校验配置(dll_path 必须存在, process_name 必填)
循环扫描目标进程列表
维护已注入 PID 集合
新进程出现即注入
按配置决定何时退出

for each pid in new_pids:
  for attempt in 1..max_retries:
    打开目标进程
    远程申请内存写入 DLL 路径
    解析 LoadLibraryW 地址
    枚举目标线程并 QueueUserAPC
    记录 thread_count 与 queued_count
    等待模块加载校验
    if module_loaded:
      记录成功日志并标记已注入
      break
    else:
      记录失败日志并 sleep(retry_interval)

若全部失败:
  记录最终失败日志并继续监控
```

### 6.7 JSON 日志规范

- 格式：JSON Lines（每行一个 JSON 对象）
- 编码：UTF-8
- 字段约束：
  - 必填：`ts`、`level`、`event`、`message`
  - 推荐：`pid`、`attempt`、`target_process`、`dll_path`
  - 可选：`tid`、`thread_count`、`queued_count`、`error_code`、`result`
- 字段含义：
  - `ts`：时间戳（ISO8601，含毫秒）
  - `level`：`INFO|WARN|ERROR`
  - `event`：事件名称（建议 `snake_case`）
  - `message`：简短描述
  - `result`：`ok|fail`

**示例：**
```json
{"ts":"2025-01-01T12:00:00.000+08:00","level":"INFO","event":"inject_start","message":"start","pid":1234,"attempt":1,"target_process":"dnf.exe","dll_path":"C:\\Path\\GameHelper.dll"}
{"ts":"2025-01-01T12:00:01.234+08:00","level":"INFO","event":"apc_queued","message":"queued","pid":1234,"attempt":1,"thread_count":42,"queued_count":37}
{"ts":"2025-01-01T12:00:02.345+08:00","level":"ERROR","event":"inject_failed","message":"open_process_failed","pid":0,"attempt":3,"error_code":5,"result":"fail"}
```

---

## 7. 测试与验证

- 手动验证：
  - 启动注入器 -> 启动游戏 -> 观察日志 -> 进入游戏后触发 alertable 状态。
- 载荷验证：
  - DLL 内部写入落地文件或日志，确认被加载。
- 兼容性验证：
  - x86 构建，路径与权限检查通过。

---

## 8. 风险与边界

- APC 依赖线程进入 alertable 状态，可能出现“注入成功但未执行”。
- 进程权限不足或被安全软件拦截。
- DLL 路径包含中文或特殊字符导致加载失败。
- 游戏版本变更导致基址偏移失效。

---

## 9. 决策记录（可追溯）

- 最终路线：APC 注入。
- 修改范围：`version-inject` + `HelperStart`。
- 注入器形态：控制台，详细日志。
- 配置方式：配置文件，字段统一英文键名。
- 日志格式：JSON Lines。
- 失败处理：重试，无回退方案。

---

## 10. 下一步工作建议

1. 在 `version-inject` 中补齐线程安全初始化与日志落地。
2. 定义并实现配置文件解析模块（建议 INI）。
3. 与外部注入器约定日志与配置格式，保证行为一致。
