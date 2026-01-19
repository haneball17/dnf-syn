# 常用命令

- 查看 SDK：`dotnet --version`
- 构建：`dotnet build "src/DNFSyncBox/DNFSyncBox.csproj"`
- 运行：`dotnet run --project "src/DNFSyncBox/DNFSyncBox.csproj"`
- 清理：`dotnet clean "src/DNFSyncBox/DNFSyncBox.csproj"`
- 发布：`dotnet publish "src/DNFSyncBox/DNFSyncBox.csproj" -c Release`

# 测试/验证

- 当前未配置自动化测试或 lint/format。
- MVP 手工验证：
  1. 打开多个 DNF 窗口。
  2. 切换前台窗口，观察自动暂停/恢复。
  3. 前台方向键应同步到后台窗口。
  4. `Alt + .` 触发暂停时，后台不再响应且清除卡键。
