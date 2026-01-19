using System;
using System.Windows;

namespace DNFSyncBox;

public partial class MainWindow : Window
{
    private readonly SyncController _syncController = new();

    /// <summary>
    /// 主窗口：仅负责 UI 展示与用户操作转发。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 窗口加载后启动同步控制器并绑定事件。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _syncController.StatusChanged += HandleStatusChanged;
        _syncController.LogAdded += HandleLogAdded;
        _syncController.Start();
    }

    /// <summary>
    /// 窗口关闭时释放钩子与定时器。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _syncController.Dispose();
    }

    /// <summary>
    /// 刷新 UI 状态：同步/暂停、窗口数量、主控句柄等。
    /// </summary>
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

    /// <summary>
    /// 追加日志并控制列表长度，避免 UI 过度膨胀。
    /// </summary>
    private void HandleLogAdded(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogList.Items.Insert(0, message);
            // 保持固定数量的日志，避免内存持续增长。
            while (LogList.Items.Count > 200)
            {
                LogList.Items.RemoveAt(LogList.Items.Count - 1);
            }
        });
    }

    /// <summary>
    /// 手动切换暂停状态。
    /// </summary>
    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        _syncController.TogglePause();
    }

    /// <summary>
    /// 立即触发窗口扫描。
    /// </summary>
    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _syncController.RefreshWindows();
    }
}
