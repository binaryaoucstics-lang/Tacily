using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenTranslator.App.Services.Tray;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Icon? _applicationIcon;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public TrayIconService()
        : this(System.Windows.Application.Current?.Dispatcher
               ?? Dispatcher.CurrentDispatcher)
    {
    }

    public TrayIconService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pauseItem = new Forms.ToolStripMenuItem("暂停快捷键");
        _pauseItem.Click += (_, _) =>
            PostToApplication(() =>
            {
                IsHotkeyPaused = !IsHotkeyPaused;
                _pauseItem.Text = IsHotkeyPaused ? "恢复快捷键" : "暂停快捷键";
                HotkeyPauseChanged?.Invoke(this, IsHotkeyPaused);
            });

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(
            "开始点击或拖框翻译",
            image: null,
            (_, _) => PostToApplication(
                () => CaptureRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(
            "连续框选翻译",
            image: null,
            (_, _) => PostToApplication(
                () => ContinuousCaptureRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(
            "显示或隐藏全部译文",
            image: null,
            (_, _) => PostToApplication(
                () => ToggleOverlaysRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(
            "设置",
            image: null,
            (_, _) => PostToApplication(
                () => ShowSettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(
            "退出",
            image: null,
            (_, _) => PostToApplication(
                () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _applicationIcon = Environment.ProcessPath is { } processPath
            ? Icon.ExtractAssociatedIcon(processPath)
            : null;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "全局 AI 翻译器",
            Icon = _applicationIcon ?? SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => PostToApplication(
            () => ShowSettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? CaptureRequested;
    public event EventHandler? ContinuousCaptureRequested;
    public event EventHandler? ToggleOverlaysRequested;
    public event EventHandler? ShowSettingsRequested;
    public event EventHandler<bool>? HotkeyPauseChanged;
    public event EventHandler? ExitRequested;

    public bool IsHotkeyPaused { get; private set; }

    public bool IsVisible => !_disposed && _notifyIcon.Visible;

    public void SetVisible(bool visible)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = visible;
    }

    public void ShowInformation(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(
            4000,
            title,
            message,
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _applicationIcon?.Dispose();
    }

    private void PostToApplication(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(action);
    }
}
