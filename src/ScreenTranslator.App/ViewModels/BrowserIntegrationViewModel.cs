using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.App.ViewModels;

public enum BrowserSetupState
{
    Detecting,
    NotDetected,
    ExtensionNotConnected,
    WaitingForConnection,
    Connected,
    BridgeError,
}

public sealed class BrowserIntegrationViewModel : ObservableObject
{
    private BrowserSetupState _chromeState = BrowserSetupState.Detecting;
    private BrowserSetupState _edgeState = BrowserSetupState.Detecting;
    private string _detailText = "安装配套扩展后，普通网页中的原位译文可随滚动移动。";
    private bool _isEnabled = true;

    public BrowserIntegrationViewModel()
    {
        OpenChromeExtensionsCommand = new RelayCommand(
            () => OpenBrowserExtensionsRequested?.Invoke(this, BrowserKind.Chrome));
        OpenEdgeExtensionsCommand = new RelayCommand(
            () => OpenBrowserExtensionsRequested?.Invoke(this, BrowserKind.Edge));
        OpenExtensionFolderCommand = new RelayCommand(
            () => OpenExtensionFolderRequested?.Invoke(this, EventArgs.Empty));
        InstallChromeExtensionCommand = new RelayCommand(
            () => InstallExtensionRequested?.Invoke(
                this,
                BrowserKind.Chrome));
        InstallEdgeExtensionCommand = new RelayCommand(
            () => InstallExtensionRequested?.Invoke(
                this,
                BrowserKind.Edge));
        RepairBridgeCommand = new AsyncRelayCommand(
            () => RepairBridgeRequested?.Invoke(
                      this,
                      EventArgs.Empty)
                  ?? Task.CompletedTask);
    }

    public BrowserSetupState ChromeState =>
        _chromeState;

    public BrowserSetupState EdgeState =>
        _edgeState;

    public string ChromeStatus => StateLabel(_chromeState);

    public string EdgeStatus => StateLabel(_edgeState);

    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsBrowserFollowingAvailable));
            }
        }
    }

    public bool IsBrowserFollowingAvailable =>
        IsEnabled &&
        (_chromeState == BrowserSetupState.Connected
         || _edgeState == BrowserSetupState.Connected);

    public IRelayCommand OpenChromeExtensionsCommand { get; }

    public IRelayCommand OpenEdgeExtensionsCommand { get; }

    public IRelayCommand OpenExtensionFolderCommand { get; }

    public IRelayCommand InstallChromeExtensionCommand { get; }

    public IRelayCommand InstallEdgeExtensionCommand { get; }

    public IAsyncRelayCommand RepairBridgeCommand { get; }

    public event EventHandler<BrowserKind>? OpenBrowserExtensionsRequested;

    public event EventHandler? OpenExtensionFolderRequested;

    public event EventHandler<BrowserKind>? InstallExtensionRequested;

    public event Func<object?, EventArgs, Task>? RepairBridgeRequested;

    public void UpdateDetected(BrowserKind browser, bool installed)
    {
        var current = GetState(browser);
        SetState(
            browser,
            installed
                ? current == BrowserSetupState.Connected
                    ? current
                    : BrowserSetupState.ExtensionNotConnected
                : BrowserSetupState.NotDetected);
    }

    public void SetWaitingForConnection(BrowserKind browser) =>
        SetState(browser, BrowserSetupState.WaitingForConnection);

    public void SetBridgeError(string message)
    {
        if (_chromeState != BrowserSetupState.NotDetected)
        {
            SetState(BrowserKind.Chrome, BrowserSetupState.BridgeError);
        }

        if (_edgeState != BrowserSetupState.NotDetected)
        {
            SetState(BrowserKind.Edge, BrowserSetupState.BridgeError);
        }

        DetailText = message;
    }

    public void UpdateConnection(BrowserKind browser, bool connected)
    {
        var current = GetState(browser);
        if (current == BrowserSetupState.NotDetected && !connected)
        {
            return;
        }

        SetState(
            browser,
            connected
                ? BrowserSetupState.Connected
                : BrowserSetupState.ExtensionNotConnected);
    }

    private BrowserSetupState GetState(BrowserKind browser) =>
        browser == BrowserKind.Chrome
            ? _chromeState
            : _edgeState;

    private void SetState(
        BrowserKind browser,
        BrowserSetupState state)
    {
        if (browser == BrowserKind.Chrome)
        {
            if (!SetProperty(
                    ref _chromeState,
                    state,
                    nameof(ChromeState)))
            {
                return;
            }

            OnPropertyChanged(nameof(ChromeStatus));
        }
        else
        {
            if (!SetProperty(
                    ref _edgeState,
                    state,
                    nameof(EdgeState)))
            {
                return;
            }

            OnPropertyChanged(nameof(EdgeStatus));
        }

        OnPropertyChanged(nameof(IsBrowserFollowingAvailable));
    }

    private static string StateLabel(BrowserSetupState state) =>
        state switch
        {
            BrowserSetupState.Detecting => "正在检测",
            BrowserSetupState.NotDetected => "未安装",
            BrowserSetupState.ExtensionNotConnected => "扩展未连接",
            BrowserSetupState.WaitingForConnection => "等待连接",
            BrowserSetupState.Connected => "已连接",
            BrowserSetupState.BridgeError => "连接需修复",
            _ => "未知状态",
        };
}
