using System;
using System.Windows;

namespace DNFSyncBox;

public partial class MainWindow : Window
{
    private readonly SyncController _syncController = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _syncController.StatusChanged += HandleStatusChanged;
        _syncController.LogAdded += HandleLogAdded;
        _syncController.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _syncController.Dispose();
    }

    private void HandleStatusChanged(SyncStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            var pauseLabel = status.IsPaused ? "已暂停" : "同步中";
            if (status.IsAutoPaused)
            {
                pauseLabel += "（自动暂停）";
            }

            PauseText.Text = $"状态：{pauseLabel}";
            WindowCountText.Text = $"窗口：{status.TotalCount}（从控 {status.SlaveCount}）";
            MasterText.Text = status.MasterHandle == IntPtr.Zero
                ? "主控：无"
                : $"主控：0x{status.MasterHandle.ToInt64():X}";
            ForegroundText.Text = $"前台 DNF：{(status.ForegroundIsDnf ? "是" : "否")}";
        });
    }

    private void HandleLogAdded(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogList.Items.Insert(0, message);
            while (LogList.Items.Count > 200)
            {
                LogList.Items.RemoveAt(LogList.Items.Count - 1);
            }
        });
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        _syncController.TogglePause();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _syncController.RefreshWindows();
    }
}
