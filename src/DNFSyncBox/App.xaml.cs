using System.Security.Principal;
using System.Windows;

namespace DNFSyncBox;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 应用启动入口：先校验管理员权限，再启动主窗口。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // DNF 以管理员权限运行时，非管理员进程无法发送消息到目标窗口。
        if (!IsAdministrator())
        {
            System.Windows.MessageBox.Show("请以管理员权限启动本程序。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    /// <summary>
    /// 判断当前进程是否具备管理员权限。
    /// </summary>
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
