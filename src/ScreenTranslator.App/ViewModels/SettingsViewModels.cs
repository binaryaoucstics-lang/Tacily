using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.Core.Hotkeys;
using ScreenTranslator.Core.Browser;
using Key = System.Windows.Input.Key;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace ScreenTranslator.App.ViewModels;

public sealed class GeneralSettingsViewModel : ObservableObject
{
    private bool _startWithWindows;
    private bool _startSilently;
    private bool _minimizeToTray = true;
    private bool _showTrayIcon = true;
    private bool _translationAnimationsEnabled = true;
    private bool _browserAutoImageTranslationEnabled;
    private bool _browserAutoPageTranslationEnabled;
    private int _browserChineseProseShareThresholdPercent = PageTranslationDecision.DefaultChineseProseShareThresholdPercent;
    private ImmutableList<string> _browserTranslationWhitelist = [], _browserTranslationBlacklist = [];
    private string _browserTranslationWhitelistText = "", _browserTranslationBlacklistText = "";
    private int _browserPageTranslationConcurrency = 10;
    private bool _browserPageCacheEnabled = true;
    private int _browserPageCacheMegabytes = 256;
    private string _browserPageCacheStatus = "已完成译文保存在本机，切回标签或再次打开时复用。";
    private string _targetLanguage = "简体中文";
    private string _captureHotkeyText = HotkeyGesture.Default.ToDisplayString();
    private bool _isContinuousCaptureActive;
    private int _continuousPendingCount;

    public GeneralSettingsViewModel(
        BrowserIntegrationViewModel? browserIntegration = null)
    {
        BrowserIntegration = browserIntegration ?? new BrowserIntegrationViewModel();
        StartCaptureCommand = new RelayCommand(
            () => StartCaptureRequested?.Invoke(this, EventArgs.Empty),
            () => !IsContinuousCaptureActive);
        StartContinuousCaptureCommand = new RelayCommand(
            () => StartContinuousCaptureRequested?.Invoke(this, EventArgs.Empty),
            () => !IsContinuousCaptureActive);
        ClearPageCacheCommand = new RelayCommand(() => ClearPageCacheRequested?.Invoke(this, EventArgs.Empty));
    }

    public BrowserIntegrationViewModel BrowserIntegration { get; }

    public ObservableCollection<string> TargetLanguages { get; } =
        ["简体中文", "繁体中文", "英语", "日语", "韩语"];

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool StartSilently
    {
        get => _startSilently;
        set => SetProperty(ref _startSilently, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set => SetProperty(ref _showTrayIcon, value);
    }
    public bool TranslationAnimationsEnabled { get => _translationAnimationsEnabled; set => SetProperty(ref _translationAnimationsEnabled, value); }

    public bool BrowserAutoImageTranslationEnabled
    {
        get => _browserAutoImageTranslationEnabled;
        set { if (SetProperty(ref _browserAutoImageTranslationEnabled, value)) OnPropertyChanged(nameof(BrowserAutoImageModeIndex)); }
    }

    // Reuse the saved opt-in flag. Older explicit opt-ins become large-image mode;
    // enabling automatic page text alone never opts images in.
    public ObservableCollection<string> BrowserAutoImageModes { get; } =
        ["不自动翻译图片（默认）", "仅大图 / 点开放大后自动判断"];
    public int BrowserAutoImageModeIndex
    {
        get => BrowserAutoImageTranslationEnabled ? 1 : 0;
        set => BrowserAutoImageTranslationEnabled = value == 1;
    }

    public bool BrowserAutoPageTranslationEnabled
    {
        get => _browserAutoPageTranslationEnabled;
        set => SetProperty(ref _browserAutoPageTranslationEnabled, value);
    }

    public int BrowserChineseProseShareThresholdPercent
    {
        get => _browserChineseProseShareThresholdPercent;
        set => SetProperty(ref _browserChineseProseShareThresholdPercent,
            PageTranslationDecision.NormalizeChineseProseShareThresholdPercent(value));
    }

    public bool BrowserPageCacheEnabled { get => _browserPageCacheEnabled; set => SetProperty(ref _browserPageCacheEnabled, value); }

    public ImmutableList<string> BrowserTranslationWhitelist
    {
        get => _browserTranslationWhitelist;
        set {
            var list = BrowserSitePolicy.NormalizeList(value);
            if (!_browserTranslationWhitelist.SequenceEqual(list)) { _browserTranslationWhitelist = list; OnPropertyChanged(); }
            _browserTranslationWhitelistText = string.Join(Environment.NewLine, list);
            OnPropertyChanged(nameof(BrowserTranslationWhitelistText)); OnPropertyChanged(nameof(BrowserSiteRulesValidation));
        }
    }
    public ImmutableList<string> BrowserTranslationBlacklist
    {
        get => _browserTranslationBlacklist;
        set {
            var list = BrowserSitePolicy.NormalizeList(value);
            if (!_browserTranslationBlacklist.SequenceEqual(list)) { _browserTranslationBlacklist = list; OnPropertyChanged(); }
            _browserTranslationBlacklistText = string.Join(Environment.NewLine, list);
            OnPropertyChanged(nameof(BrowserTranslationBlacklistText)); OnPropertyChanged(nameof(BrowserSiteRulesValidation));
        }
    }
    private static string[] SiteEntries(string text) => text.Split(['\r', '\n', ',', ';', '，', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public string BrowserTranslationWhitelistText
    {
        get => _browserTranslationWhitelistText;
        set {
            if (!SetProperty(ref _browserTranslationWhitelistText, value ?? "")) return;
            _browserTranslationWhitelist = BrowserSitePolicy.NormalizeList(SiteEntries(_browserTranslationWhitelistText));
            OnPropertyChanged(nameof(BrowserTranslationWhitelist)); OnPropertyChanged(nameof(BrowserSiteRulesValidation));
        }
    }
    public string BrowserTranslationBlacklistText
    {
        get => _browserTranslationBlacklistText;
        set {
            if (!SetProperty(ref _browserTranslationBlacklistText, value ?? "")) return;
            _browserTranslationBlacklist = BrowserSitePolicy.NormalizeList(SiteEntries(_browserTranslationBlacklistText));
            OnPropertyChanged(nameof(BrowserTranslationBlacklist)); OnPropertyChanged(nameof(BrowserSiteRulesValidation));
        }
    }
    public string BrowserSiteRulesValidation {
        get {
            var entries = SiteEntries(_browserTranslationWhitelistText).Concat(SiteEntries(_browserTranslationBlacklistText)).ToArray();
            var invalid = entries.Count(entry => BrowserSitePolicy.NormalizeDomain(entry) is null);
            if (invalid > 0) return $"有 {invalid} 条无效域名未保存，请检查输入。";
            if (SiteEntries(_browserTranslationWhitelistText).Length > BrowserSitePolicy.MaximumDomains || SiteEntries(_browserTranslationBlacklistText).Length > BrowserSitePolicy.MaximumDomains)
                return $"每个列表最多保存 {BrowserSitePolicy.MaximumDomains} 个域名。";
            return "输入域名或网页地址，每行一个；离开输入框后保存。具体子域名优先，相同域名黑名单优先。";
        }
    }
    public ObservableCollection<int> BrowserPageCacheSizes { get; } = [32, 64, 128, 256, 512, 1024, 2048];
    public int BrowserPageCacheMegabytes { get => _browserPageCacheMegabytes; set => SetProperty(ref _browserPageCacheMegabytes, Math.Clamp(value, 32, 2048)); }
    public string BrowserPageCacheStatus { get => _browserPageCacheStatus; set => SetProperty(ref _browserPageCacheStatus, value); }
    public IRelayCommand ClearPageCacheCommand { get; }
    public event EventHandler? ClearPageCacheRequested;

    public string BrowserPageJudgementMode => "程序规则";

    public ObservableCollection<int> BrowserPageTranslationConcurrencies { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    public int BrowserPageTranslationConcurrency
    {
        get => _browserPageTranslationConcurrency;
        set => SetProperty(ref _browserPageTranslationConcurrency, Math.Clamp(value, 1, 10));
    }

    public string TargetLanguage
    {
        get => _targetLanguage;
        set => SetProperty(ref _targetLanguage, value);
    }

    public string CaptureHotkeyText
    {
        get => _captureHotkeyText;
        set => SetProperty(ref _captureHotkeyText, value);
    }

    public bool IsContinuousCaptureActive
    {
        get => _isContinuousCaptureActive;
        set
        {
            if (!SetProperty(ref _isContinuousCaptureActive, value))
            {
                return;
            }

            StartCaptureCommand.NotifyCanExecuteChanged();
            StartContinuousCaptureCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ContinuousCaptureStatusText));
        }
    }

    public int ContinuousPendingCount
    {
        get => _continuousPendingCount;
        set
        {
            if (SetProperty(ref _continuousPendingCount, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(ContinuousCaptureStatusText));
            }
        }
    }

    public string ContinuousCaptureStatusText => IsContinuousCaptureActive
        ? $"连续框选中 · 待处理 {ContinuousPendingCount}"
        : "连续选择多个区域，Esc 或右键结束，译文会依次显示。";

    public IRelayCommand StartCaptureCommand { get; }

    public IRelayCommand StartContinuousCaptureCommand { get; }

    public event EventHandler? StartCaptureRequested;

    public event EventHandler? StartContinuousCaptureRequested;
}

public sealed class AppearanceSettingsViewModel : ObservableObject
{
    private string _theme = "跟随系统";
    private string _displayMode = "原文旁边";
    private double _panelOpacity = 94;
    private bool _useAnimations = true;

    public ObservableCollection<string> Themes { get; } =
        ["跟随系统", "浅色", "深色"];

    public ObservableCollection<string> DisplayModes { get; } =
        ["鼠标附近悬浮窗"];

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public string DisplayMode
    {
        get => _displayMode;
        set => SetProperty(ref _displayMode, value);
    }

    public double PanelOpacity
    {
        get => _panelOpacity;
        set => SetProperty(ref _panelOpacity, value);
    }

    public bool UseAnimations
    {
        get => _useAnimations;
        set => SetProperty(ref _useAnimations, value);
    }
}

public sealed class HotkeySettingsViewModel : ObservableObject
{
    private HotkeyGesture _gesture = HotkeyGesture.Default;
    private string _hotkeyText = HotkeyGesture.Default.ToDisplayString();
    private bool _isEnabled = true;
    private string _statusText = "快捷键可用";
    private bool _isRecording;

    public HotkeySettingsViewModel()
    {
        BeginRecordingCommand = new RelayCommand(BeginRecording);
        CancelRecordingCommand = new RelayCommand(CancelRecording, () => IsRecording);
        UseDefaultCommand = new RelayCommand(
            () => Submit(HotkeyGesture.Default),
            () => IsRecording);
    }

    public string HotkeyText
    {
        get => _hotkeyText;
        set => SetProperty(ref _hotkeyText, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(RecordButtonText));
                CancelRecordingCommand.NotifyCanExecuteChanged();
                UseDefaultCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RecordButtonText => IsRecording ? "正在录制…" : "修改快捷键";

    public HotkeyGesture Gesture => _gesture;

    public RelayCommand BeginRecordingCommand { get; }

    public RelayCommand CancelRecordingCommand { get; }

    public RelayCommand UseDefaultCommand { get; }

    public event EventHandler? RecordingStarted;

    public event EventHandler? RecordingCancelled;

    public event EventHandler<HotkeyGesture>? GestureSubmitted;

    public void AcceptKeyboardInput(ModifierKeys modifiers, Key key)
    {
        if (!IsRecording)
        {
            return;
        }

        if (IsModifierKey(key))
        {
            StatusText = "请同时按下一个字母、数字或功能键";
            return;
        }

        try
        {
            var gesture = HotkeyGesture.Create(
                ToCoreModifiers(modifiers),
                ToCoreKeyName(key));
            Submit(gesture);
        }
        catch (FormatException exception)
        {
            StatusText = exception.Message;
        }
    }

    public void ApplyGesture(
        HotkeyGesture gesture,
        bool isEnabled,
        string statusText)
    {
        _gesture = gesture;
        HotkeyText = gesture.ToDisplayString();
        IsRecording = false;
        IsEnabled = isEnabled;
        StatusText = statusText;
    }

    private void BeginRecording()
    {
        if (IsRecording)
        {
            return;
        }

        IsRecording = true;
        HotkeyText = "请按新的组合键";
        StatusText = "Esc 取消，Backspace 恢复默认";
        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        HotkeyText = _gesture.ToDisplayString();
        RecordingCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void Submit(HotkeyGesture gesture)
    {
        IsRecording = false;
        GestureSubmitted?.Invoke(this, gesture);
    }

    private static HotkeyModifiers ToCoreModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= HotkeyModifiers.Windows;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        return result;
    }

    private static string ToCoreKeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return ((int)key - (int)Key.NumPad0).ToString();
        }

        return key switch
        {
            >= Key.F1 and <= Key.F12 => key.ToString(),
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Space => "Space",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => key.ToString(),
        };
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin;
}

public sealed class PrivacySettingsViewModel : ObservableObject
{
    private bool _saveTextHistory;

    public PrivacySettingsViewModel()
    {
        ClearHistoryCommand = new RelayCommand(
            () => ClearHistoryRequested?.Invoke(this, EventArgs.Empty));
    }

    public bool SaveTextHistory
    {
        get => _saveTextHistory;
        set => SetProperty(ref _saveTextHistory, value);
    }

    public IRelayCommand ClearHistoryCommand { get; }

    public event EventHandler? ClearHistoryRequested;
}
