• # DNF Sync Box 会话总结（用于下次对接）

  🎯 项目背景

  - Windows WPF/.NET 8 控制端（DNFSyncBox） + 注入 DLL（dnfinput.dll）
  - 目标：同步前台 DNF 输入到后台 DNF；含手动暂停与自动暂停

  ———

  ✅ 已完成的实现/改动（未提交）

  - dnfinput/src/dnfinput.cpp
      - 增加 DirectInput 伪造：Hook GetDeviceState 并注入按键状态
      - 增加 vKey → DIK 映射
      - 增加统计字段：SpoofDI、Fail、NotAcquired
      - 可选强制返回 DI_OK（环境变量 DNFSYNC_FORCE_DI_OK=1）
      - 增加前台欺骗：Hook GetForegroundWindow / GetActiveWindow / GetFocus
          - 仅在共享内存就绪、未暂停、且进程非前台时生效
          - 目标：让后台窗口认为自己是前台
      - 新增 Focus 统计字段：Focus: Foreground/Active/Focus/Spoof
      - 窗口句柄缓存，EnumWindows 定位主窗口（1s 节流）
      - 日志缓冲区扩至 640
      - 日志目录与 success file 位置改为 DLL 加载目录
  - success file 规则已实现
      - 文件名：successfile_dnfinput_<pid>.txt
      - 内容：时间戳
      - 进程退出时清理
  - 文档更新
      - docs/dnf-syn_开发方案.md
      - docs/dnf-syn_键盘状态伪造实现方案.md

  ———

  🧪 当前测试结果与问题

  1. D/Q 同步失效（其他字母键、F1/F2 可触发）
  2. 同步了全部按键（非仅方向键）
  3. 手动暂停失效（热键与界面按钮均无效）

  ———

  ✅ 用户补充确认

  - 点击“暂停/恢复”后，UI 状态变为“已暂停”
  - 切换输入法为英文（US）后，D/Q 仍失效

  ———

  🔍 已有分析结论（初步）

  - D/Q 失效
      - 控制端日志已记录 Q/D 按下/抬起 → 捕获层正常
      - US 布局仍失效 → 排除输入法/布局问题
      - dnfinput 日志里 GetRawInputData 有计数 → DNF 可能对部分字母键走 RawInput
      - 目前未伪造 RawInput → 可能导致只有 D/Q 不生效
      - 另一可能：映射/过滤逻辑对 D/Q 被排除
  - 同步全键
      - 配置为 Mode=All 且 Keys=null → 默认全键同步（符合配置语义）
      - 需改配置回白名单才能只同步方向键
  - 手动暂停失效
      - UI 显示“已暂停”说明界面状态生效
      - 但 dnfinput 仍有 Spoof 统计 → 暂停未传到共享内存或未被注入端应用
      - 也可能只暂停 Win32 路径，RawInput/DI 仍继续

  ———

  📌 待验证/下一步（不改代码）

  1. 暂停后观察两侧 dnfinput：SpoofAsync/SpoofDI 是否仍增长
  2. 单独按 D/Q 与 E/R 对比：GetRawInputData 是否仅在 D/Q 时明显增长
  3. 提供控制端日志中“暂停/恢复”附近的几行（确认是否真触发暂停逻辑）

  ———

  ⚠️ 未执行事项

  - 未运行编译/测试
  - 未提交代码

  ———

  🗂️ 关键文件

  - dnfinput/src/dnfinput.cpp
  - docs/dnf-syn_开发方案.md
  - docs/dnf-syn_键盘状态伪造实现方案.md

  ———