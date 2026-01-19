using System.Security.Principal;
using System.Windows;

namespace DNFSyncBox;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsAdministrator())
        {
            MessageBox.Show("请以管理员权限启动本程序。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
