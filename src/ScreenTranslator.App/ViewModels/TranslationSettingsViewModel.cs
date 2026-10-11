using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.App.ViewModels;

public sealed partial class TranslationSettingsViewModel : ObservableObject
{
    private string _selectedProvider = "DeepSeek";
    private readonly Dictionary<TranslationProviderKind, (string Model, string Url)> _providerDrafts = new()
    {
        [TranslationProviderKind.DeepSeek] = ("deepseek-flash", "https://api.deepseek.com"),
        [TranslationProviderKind.OpenAI] = (TranslationProviderConfiguration.DefaultOpenAiModel, TranslationProviderConfiguration.DefaultOpenAiBaseUrl),
        [TranslationProviderKind.Claude] = (TranslationProviderConfiguration.DefaultClaudeModel, TranslationProviderConfiguration.DefaultClaudeBaseUrl),
        [TranslationProviderKind.ChatCompletions] = ("", TranslationProviderConfiguration.DefaultOpenAiBaseUrl),
    };
    private readonly Dictionary<string, string> _presetKeyMasks = new();
    private bool _applyingPreset;
    private string _defaultProvider = "DeepSeek";
    private string _defaultThinking = "关闭";
    private bool _defaultBounded = true;
    private ProviderPresetViewModel? _selectedPreset;
    public ObservableCollection<ProviderPresetViewModel> Presets { get; } = [];
    public ProviderPresetViewModel? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (ReferenceEquals(value, _selectedPreset) || IsSaving || IsChatGptBusy) return;
            if (_selectedPreset is null)
            {
                _providerDrafts[ProviderKind] = (SelectedModel, BaseUrl);
                _defaultProvider = SelectedProvider;
                _defaultThinking = ThinkingLevel;
                _defaultBounded = BoundedLowThinkingEnabled;
                _defaultNetwork = CollectNetwork();
                _defaultOpenAiAuthenticationMode = _authenticationMode;
                _defaultChatGptAccountId = _chatGptAccountId;
            }
            CapturePreset();
            PendingCopySecretName = null;
            _selectedPreset = value;
            _applyingPreset = true;
            IsSwitchingProvider = true;
            try
            {
                if (value is { Config: { } config })
                {
                    SelectedProvider = TranslationProviderConfiguration.Label(config.Provider);
                    SelectedModel = config.Model;
                    BaseUrl = config.BaseUrl;
                    ThinkingLevel = config.ThinkingLevel switch
                    { ScreenTranslator.Core.Models.ThinkingLevel.None => "关闭", ScreenTranslator.Core.Models.ThinkingLevel.High => "高", ScreenTranslator.Core.Models.ThinkingLevel.Max => "最高", _ => "低" };
                    BoundedLowThinkingEnabled = config.BoundedLowThinkingEnabled;
                    ApplyNetwork(config.Network);
                    _authenticationMode = config.AuthenticationMode;
                    _chatGptAccountId = config.ChatGptAccountId;
                }
                else
                {
                    SelectedProvider = _defaultProvider;
                    var draft = _providerDrafts[ProviderKind];
                    SelectedModel = draft.Model; BaseUrl = draft.Url;
                    ThinkingLevel = _defaultThinking; BoundedLowThinkingEnabled = _defaultBounded;
                    ApplyNetwork(_defaultNetwork);
                    _authenticationMode = _defaultOpenAiAuthenticationMode;
                    _chatGptAccountId = _defaultChatGptAccountId;
                }
                LoadProviderKeyMask();
                InvalidateConnection();
            }
            finally { _applyingPreset = false; IsSwitchingProvider = false; }
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetName)); OnPropertyChanged(nameof(CanEditPreset));
            OnPropertyChanged(nameof(ActiveSecretName));
            OnPropertyChanged(nameof(SelectedAuthentication)); OnPropertyChanged(nameof(SelectedChatGptAccount));
            OnPropertyChanged(nameof(ChatGptAccountId)); NotifyChatGptState();
            InvalidateModelList(); QueueChatGptModelRefresh();
        }
    }
    public string PresetName
    {
        get => SelectedPreset?.Name ?? "";
        set { if (SelectedPreset is not null && SelectedPreset.Name != value) { SelectedPreset.Name = value; OnPropertyChanged(); } }
    }
    public string PresetDescription => UsesChatGptPlan
        ? "预设分别保存模型、账户、思考等级和网络方式。复制会引用同一账户连接，可再选择其他账户。"
        : "预设分别保存地址、模型、思考等级、网络方式和加密密钥。新增后填写连接信息并点击保存；复制会一并复制已保存的密钥。";
    public string ModelSelectionDescription => UsesChatGptPlan ? "选择当前账户可用的模型" : "可选择预设或输入完整模型 ID";
    public bool CanEditPreset => SelectedPreset is not null && CanEditProvider;
    public string ActiveSecretName => SelectedPreset is { } preset
        ? TranslationProviderConfiguration.PresetSecretName(preset.Id) : TranslationProviderConfiguration.SecretName(ProviderKind);
    public string? PendingCopySecretName { get; private set; }
    public void CompletePresetKeyCopy() => PendingCopySecretName = null;

    private void CapturePreset()
    {
        if (SelectedPreset is not { } preset) return;
        preset.Config = new TranslationProviderPreset
        {
            Id = preset.Id, Name = string.IsNullOrWhiteSpace(preset.Name) ? "自定义服务商" : preset.Name.Trim(),
            Provider = ProviderKind, Model = SelectedModel.Trim(), BaseUrl = BaseUrl.Trim(),
            ThinkingLevel = ThinkingLevel switch { "关闭" => ScreenTranslator.Core.Models.ThinkingLevel.None,
                "高" => ScreenTranslator.Core.Models.ThinkingLevel.High, "最高" => ScreenTranslator.Core.Models.ThinkingLevel.Max,
                _ => ScreenTranslator.Core.Models.ThinkingLevel.Low }, BoundedLowThinkingEnabled = BoundedLowThinkingEnabled,
            Network = CollectNetwork(),
            AuthenticationMode = UsesChatGptPlan ? TranslationAuthenticationMode.ChatGptOAuth : TranslationAuthenticationMode.ApiKey,
            ChatGptAccountId = _chatGptAccountId,
        };
    }

    private void CreatePreset(bool copy)
    {
        if (!CanEditProvider) return;
        var sourceSecret = ActiveSecretName;
        var draftKey = ApiKey;
        var config = new TranslationProviderPreset
        {
            Name = copy ? (SelectedPreset?.Name ?? SelectedProvider) + " 副本" : "自定义服务商 " + (Presets.Count + 1),
            Provider = copy ? ProviderKind : TranslationProviderKind.ChatCompletions,
            Model = copy ? SelectedModel : "", BaseUrl = copy ? BaseUrl : TranslationProviderConfiguration.DefaultOpenAiBaseUrl,
            ThinkingLevel = copy ? (ThinkingLevel switch { "低" => ScreenTranslator.Core.Models.ThinkingLevel.Low,
                "高" => ScreenTranslator.Core.Models.ThinkingLevel.High, "最高" => ScreenTranslator.Core.Models.ThinkingLevel.Max,
                _ => ScreenTranslator.Core.Models.ThinkingLevel.None }) : ScreenTranslator.Core.Models.ThinkingLevel.None,
            BoundedLowThinkingEnabled = BoundedLowThinkingEnabled,
            Network = copy ? CollectNetwork() : new(),
            AuthenticationMode = copy && UsesChatGptPlan ? TranslationAuthenticationMode.ChatGptOAuth : TranslationAuthenticationMode.ApiKey,
            ChatGptAccountId = copy ? _chatGptAccountId : "",
        };
        var preset = new ProviderPresetViewModel(config);
        Presets.Add(preset);
        SelectedPreset = preset;
        PendingCopySecretName = copy && UsesApiKey ? sourceSecret : null;
        if (copy && draftKey.Length > 0) ApiKey = draftKey;
        if (copy) SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    public IRelayCommand AddPresetCommand { get; }
    public IRelayCommand CopyPresetCommand { get; }
    public IRelayCommand DeletePresetCommand { get; }
    public IRelayCommand DefaultPresetCommand { get; }
    public event Action<string>? PresetDeleted;
    private int _connectionRevision;
    private bool _isSaving;
    public bool IsSwitchingProvider { get; private set; }

    public ObservableCollection<string> Providers { get; } = ["DeepSeek", "OpenAI", "Claude", "OpenAI 兼容"];
    public ObservableCollection<string> Protocols { get; } = ["DeepSeek 官方", "OpenAI Responses", "Anthropic Messages", "OpenAI 兼容 Chat Completions"];
    public string SelectedProtocol
    {
        get => Protocols[(int)ProviderKind];
        set
        {
            var index = Protocols.IndexOf(value);
            if (index >= 0) SelectedProvider = TranslationProviderConfiguration.Label((TranslationProviderKind)index);
        }
    }
    public TranslationProviderKind ProviderKind => SelectedProvider switch
    { "OpenAI" => TranslationProviderKind.OpenAI, "Claude" => TranslationProviderKind.Claude, "OpenAI 兼容" => TranslationProviderKind.ChatCompletions, _ => TranslationProviderKind.DeepSeek };
    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (_selectedProvider == value || !Providers.Contains(value)) return;
            IsSwitchingProvider = true;
            try
            {
                if (SelectedPreset is null && !_applyingPreset) _providerDrafts[ProviderKind] = (SelectedModel, BaseUrl);
                _selectedProvider = value;
                var draft = _providerDrafts[ProviderKind];
                Models.Clear();
                foreach (var model in TranslationProviderConfiguration.Models(ProviderKind)) Models.Add(model);
                SelectedModel = draft.Model;
                BaseUrl = draft.Url;
                LoadProviderKeyMask();
                InvalidateConnection();
                OnPropertyChanged(nameof(ProviderKind));
                OnPropertyChanged(nameof(ProviderDescription));
                OnPropertyChanged(nameof(CanEditEndpoint));
                OnPropertyChanged(nameof(CanEditModel));
                OnPropertyChanged(nameof(CanBoundLowThinkingWait));
                OnPropertyChanged(nameof(ThinkingDescription));
                OnPropertyChanged(nameof(SelectedProtocol));
                OnPropertyChanged(nameof(ActiveSecretName));
                NotifyChatGptState();
            }
            finally { IsSwitchingProvider = _applyingPreset; }
            OnPropertyChanged();
        }
    }
    public string ProviderDescription => ProviderKind switch
    {
        TranslationProviderKind.OpenAI => UsesChatGptPlan ? "ChatGPT 账户额度 · 输入支持由所选模型决定" : "Responses API · 文字、图片与流式输出",
        TranslationProviderKind.Claude => "Messages API · 文字、图片与流式输出",
        TranslationProviderKind.ChatCompletions => "Chat Completions · 自定义服务商和本机服务；模型需支持对应输入",
        _ => "DeepSeek V4.1 Flash · 支持图片",
    };
    public bool CanEditEndpoint => ProviderKind != TranslationProviderKind.DeepSeek && CanEditProvider && UsesApiKey;
    public bool CanEditModel => ProviderKind != TranslationProviderKind.DeepSeek && CanEditProvider && (!UsesChatGptPlan || CanUseChatGptPlan);
    public bool CanEditProvider => !IsSaving && !IsChatGptBusy;
    public bool IsSaving => _isSaving;
    public void SetSaving(bool saving)
    {
        if (!SetProperty(ref _isSaving, saving, nameof(IsSaving))) return;
        OnPropertyChanged(nameof(CanEditEndpoint)); OnPropertyChanged(nameof(CanEditModel)); OnPropertyChanged(nameof(CanEditProvider));
        OnPropertyChanged(nameof(CanEditPreset));
        SaveCommand.NotifyCanExecuteChanged();
        if (saving) InvalidateModelList(false);
        LoadModelsCommand.NotifyCanExecuteChanged();
        ReadClashNodesCommand.NotifyCanExecuteChanged();
        NotifyProbeCommands();
        NotifyChatGptState();
        if (saving) InvalidateClashDiscovery();
    }
    public string ThinkingDescription => TranslationProviderConfiguration.ThinkingDescription(ProviderKind, SelectedModel, _thinkingLevel switch
    { "关闭" => ScreenTranslator.Core.Models.ThinkingLevel.None, "高" => ScreenTranslator.Core.Models.ThinkingLevel.High,
        "最高" => ScreenTranslator.Core.Models.ThinkingLevel.Max, _ => ScreenTranslator.Core.Models.ThinkingLevel.Low });

    public void ApplyProviderSettings(AppSettings settings)
    {
        _providerDrafts[TranslationProviderKind.DeepSeek] = (settings.DeepSeekModel, settings.DeepSeekBaseUrl);
        _providerDrafts[TranslationProviderKind.OpenAI] = (settings.OpenAiModel, settings.OpenAiBaseUrl);
        _providerDrafts[TranslationProviderKind.Claude] = (settings.ClaudeModel, settings.ClaudeBaseUrl);
        _providerDrafts[TranslationProviderKind.ChatCompletions] = (settings.ChatCompletionsModel, settings.ChatCompletionsBaseUrl);
        _authenticationMode = _defaultOpenAiAuthenticationMode = settings.OpenAiAuthenticationMode;
        _chatGptAccountId = _defaultChatGptAccountId = settings.ChatGptAccountId;
        _selectedPreset = null;
        _defaultNetwork = settings.ApiNetwork;
        ApplyNetwork(_defaultNetwork);
        Presets.Clear();
        foreach (var preset in settings.TranslationProviderPresets) Presets.Add(new ProviderPresetViewModel(preset));
        // Do not overwrite the just-loaded draft when changing provider.
        var target = TranslationProviderConfiguration.Label(settings.TranslationProvider);
        if (target != SelectedProvider)
        {
            var activeDraft = _providerDrafts[ProviderKind];
            SelectedModel = activeDraft.Model;
            BaseUrl = activeDraft.Url;
            SelectedProvider = target;
        }
        var selected = _providerDrafts[ProviderKind];
        SelectedModel = selected.Model;
        BaseUrl = selected.Url;
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == settings.SelectedTranslationPresetId);
        OnPropertyChanged(nameof(SelectedPreset)); OnPropertyChanged(nameof(PresetName)); OnPropertyChanged(nameof(CanEditPreset));
        OnPropertyChanged(nameof(SelectedAuthentication)); OnPropertyChanged(nameof(SelectedChatGptAccount)); NotifyChatGptState();
    }

    public AppSettings CollectProviderSettings(AppSettings settings)
    {
        if (SelectedPreset is null)
        {
            _providerDrafts[ProviderKind] = (SelectedModel.Trim(), BaseUrl.Trim());
            _defaultNetwork = CollectNetwork();
            _defaultOpenAiAuthenticationMode = _authenticationMode;
            _defaultChatGptAccountId = _chatGptAccountId;
        }
        CapturePreset();
        return settings with
        {
            TranslationProvider = ProviderKind, DeepSeekModel = "deepseek-flash", DeepSeekBaseUrl = "https://api.deepseek.com",
            OpenAiModel = _providerDrafts[TranslationProviderKind.OpenAI].Model,
            OpenAiBaseUrl = _providerDrafts[TranslationProviderKind.OpenAI].Url,
            ClaudeModel = _providerDrafts[TranslationProviderKind.Claude].Model,
            ClaudeBaseUrl = _providerDrafts[TranslationProviderKind.Claude].Url,
            ChatCompletionsModel = _providerDrafts[TranslationProviderKind.ChatCompletions].Model,
            ChatCompletionsBaseUrl = _providerDrafts[TranslationProviderKind.ChatCompletions].Url,
            TranslationProviderPresets = settings.TranslationProviderPresets.SequenceEqual(Presets.Select(p => p.Config))
                ? settings.TranslationProviderPresets : Presets.Select(p => p.Config).ToImmutableList(),
            SelectedTranslationPresetId = SelectedPreset?.Id ?? "",
            ApiNetwork = _defaultNetwork,
            OpenAiAuthenticationMode = _defaultOpenAiAuthenticationMode, ChatGptAccountId = _defaultChatGptAccountId,
        };
    }

    private string _apiKey = string.Empty;
    private string _savedApiKeyMask = string.Empty;
    private bool _hasSavedApiKey;
    private bool _isEditingApiKey;
    private string _selectedModel = "deepseek-flash";
    private string _thinkingLevel = "关闭";
    private bool _boundedLowThinkingEnabled = true;
    private string _inputMode = "VLM 直接翻译";
    private bool _inPlaceTranslationEnabled = true;
    private string _triggerMode = "快捷键后点击或拖框";
    private string _baseUrl = "https://api.deepseek.com";
    private string _sourceLanguage = "自动检测";
    private string _targetLanguage = "简体中文";
    private string _translationStyle = "自然";
    private string _customContext = string.Empty;
    private string _connectionStatus = "尚未测试连接";
    private bool _isTesting;
    private bool _connectionSucceeded;
    public TranslationSettingsViewModel()
    {
        InitializeModelDiscovery();
        InitializeNetwork();
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, CanTestConnection);
        InitializeBenchmark();
        SaveCommand = new RelayCommand(() => SaveRequested?.Invoke(this, EventArgs.Empty), () => CanEditProvider);
        BeginApiKeyEditCommand = new RelayCommand(BeginApiKeyEdit);
        CancelApiKeyEditCommand = new RelayCommand(CancelApiKeyEdit);
        InitializeChatGpt();
        AddPresetCommand = new RelayCommand(() => CreatePreset(false));
        CopyPresetCommand = new RelayCommand(() => CreatePreset(true));
        DeletePresetCommand = new RelayCommand(() =>
        {
                if (!CanEditProvider || SelectedPreset is not { } preset) return;
            SelectedPreset = null;
            Presets.Remove(preset);
            var secretName = TranslationProviderConfiguration.PresetSecretName(preset.Id);
            _presetKeyMasks.Remove(secretName);
            PendingCopySecretName = null;
            OnPropertyChanged(nameof(SelectedPreset));
            PresetDeleted?.Invoke(secretName);
        });
        DefaultPresetCommand = new RelayCommand(() => SelectedPreset = null);
    }

    public ObservableCollection<string> Models { get; } =
        ["deepseek-flash"];

    public ObservableCollection<string> ThinkingLevels { get; } = ["关闭", "低", "高", "最高"];

    public ObservableCollection<string> InputModes { get; } = ["自动识别", "VLM 直接翻译"];
    public ObservableCollection<string> TriggerModes { get; } = ["快捷键后点击或拖框", "快捷键即译鼠标位置"];
    public string TriggerMode
    {
        get => _triggerMode;
        set => SetProperty(ref _triggerMode, value);
    }

    public string ThinkingLevel
    {
        get => _thinkingLevel;
        set
        {
            if (SetProperty(ref _thinkingLevel, value))
            { OnPropertyChanged(nameof(CanBoundLowThinkingWait)); OnPropertyChanged(nameof(ThinkingDescription)); }
        }
    }

    public bool BoundedLowThinkingEnabled
    {
        get => _boundedLowThinkingEnabled;
        set => SetProperty(ref _boundedLowThinkingEnabled, value);
    }

    public bool CanBoundLowThinkingWait => ProviderKind == TranslationProviderKind.DeepSeek && ThinkingLevel == "低";

    public string InputMode
    {
        get => _inputMode;
        set => SetProperty(ref _inputMode, value);
    }

    public bool InPlaceTranslationEnabled { get => _inPlaceTranslationEnabled; set => SetProperty(ref _inPlaceTranslationEnabled, value); }


    public ObservableCollection<string> SourceLanguages { get; } =
        ["自动检测", "英语", "简体中文", "日语", "韩语"];

    public ObservableCollection<string> TargetLanguages { get; } =
        ["简体中文", "繁体中文", "英语", "日语", "韩语"];

    public ObservableCollection<string> TranslationStyles { get; } =
        ["自然", "直译", "学习模式"];

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                InvalidateConnection();
                TestConnectionCommand.NotifyCanExecuteChanged();
                CancelConnectionTestCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowConnectionDetails));
                InvalidateModelList();
            }
        }
    }

    public string SavedApiKeyMask
    {
        get => _savedApiKeyMask;
        private set => SetProperty(ref _savedApiKeyMask, value);
    }

    public bool HasSavedApiKey
    {
        get => _hasSavedApiKey;
        private set
        {
            if (SetProperty(ref _hasSavedApiKey, value))
            {
                NotifyApiKeyStateChanged();
                TestConnectionCommand.NotifyCanExecuteChanged();
                LoadModelsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsEditingApiKey
    {
        get => _isEditingApiKey;
        private set
        {
            if (SetProperty(ref _isEditingApiKey, value))
            {
                NotifyApiKeyStateChanged();
            }
        }
    }

    public bool ShowSavedApiKey => HasSavedApiKey && !IsEditingApiKey;

    public bool ShowApiKeyEditor => !ShowSavedApiKey;

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_updatingModels) return;
            value ??= "";
            if (SetProperty(ref _selectedModel, value))
            { InvalidateConnection(); OnPropertyChanged(nameof(ThinkingDescription)); TestConnectionCommand.NotifyCanExecuteChanged(); }
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { if (SetProperty(ref _baseUrl, value)) { InvalidateConnection(); InvalidateModelList(); } }
    }

    public string SourceLanguage
    {
        get => _sourceLanguage;
        set => SetProperty(ref _sourceLanguage, value);
    }

    public string TargetLanguage
    {
        get => _targetLanguage;
        set => SetProperty(ref _targetLanguage, value);
    }

    public string TranslationStyle
    {
        get => _translationStyle;
        set => SetProperty(ref _translationStyle, value);
    }

    public string CustomContext
    {
        get => _customContext;
        set => SetProperty(ref _customContext, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                TestConnectionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ConnectionSucceeded
    {
        get => _connectionSucceeded;
        private set => SetProperty(ref _connectionSucceeded, value);
    }

    public IAsyncRelayCommand TestConnectionCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand BeginApiKeyEditCommand { get; }

    public IRelayCommand CancelApiKeyEditCommand { get; }

    public Func<TranslationConnectionTestRequest, CancellationToken, Task<ConnectionTestResult>>?
        ConnectionTester { get; set; }

    public event EventHandler? SaveRequested;

    public void SetConnectionStatus(string message)
    {
        ConnectionSucceeded = true;
        ConnectionStatus = message;
    }

    public void SetConnectionError(string message)
    {
        ConnectionSucceeded = false;
        ConnectionStatus = message;
    }

    public void ApplySavedApiKey(string? apiKey)
    {
        ApplySavedApiKey(ActiveSecretName, apiKey);
    }

    public void ApplySavedApiKey(string secretName, string? apiKey)
    {
        _presetKeyMasks[secretName] = ApiKeyMasker.Mask(apiKey);
        if (secretName == ActiveSecretName) LoadProviderKeyMask();
    }

    public void ApplySavedApiKey(TranslationProviderKind provider, string? apiKey)
    {
        ApplySavedApiKey(TranslationProviderConfiguration.SecretName(provider), apiKey);
    }

    private void LoadProviderKeyMask()
    {
        ApiKey = string.Empty;
        SavedApiKeyMask = _presetKeyMasks.GetValueOrDefault(ActiveSecretName, string.Empty);
        HasSavedApiKey = SavedApiKeyMask.Length > 0;
        IsEditingApiKey = false;
        NotifyApiKeyStateChanged();
        InvalidateModelList();
    }

    private bool CanTestConnection() =>
        !IsTesting && !IsChatGptBusy &&
        (UsesChatGptPlan ? CanUseChatGptPlan && !string.IsNullOrWhiteSpace(SelectedModel) : HasSavedApiKey || !string.IsNullOrWhiteSpace(ApiKey));

    public void BeginApiKeyEdit()
    {
        ApiKey = string.Empty;
        IsEditingApiKey = true;
    }

    public void CancelApiKeyEdit()
    {
        ApiKey = string.Empty;
        IsEditingApiKey = false;
    }

    private void NotifyApiKeyStateChanged()
    {
        OnPropertyChanged(nameof(ShowSavedApiKey));
        OnPropertyChanged(nameof(ShowApiKeyEditor));
    }

    private void InvalidateConnection()
    {
        _connectionRevision++;
        if (IsTesting) TestConnectionCommand.Cancel();
        ConnectionSucceeded = false;
        ConnectionStatus = "尚未测试连接";
        ConnectionTranslation = ""; ConnectionBenchmark = null;
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        IsTesting = true;
        ConnectionSucceeded = false;
        var revision = _connectionRevision;
        var provider = ProviderKind;
        ConnectionTranslation = ""; ConnectionBenchmark = null;
        var stream = new Progress<ScreenTranslator.Core.Models.TranslationStreamUpdate>(update =>
        {
            if (revision == _connectionRevision && IsTesting && !string.IsNullOrWhiteSpace(update.Translation))
                ConnectionTranslation = update.Translation;
        });
        var request = new TranslationConnectionTestRequest(ApiKey, SelectedModel, UsesChatGptPlan ? TranslationProviderConfiguration.DefaultOpenAiBaseUrl : BaseUrl, provider, ActiveSecretName, CollectNetwork())
        {
            ThinkingLevel = ThinkingLevel switch { "低" => ScreenTranslator.Core.Models.ThinkingLevel.Low,
                "高" => ScreenTranslator.Core.Models.ThinkingLevel.High, "最高" => ScreenTranslator.Core.Models.ThinkingLevel.Max,
                _ => ScreenTranslator.Core.Models.ThinkingLevel.None }, Progress = stream,
            UseChatGptPlan = UsesChatGptPlan, ChatGptAccountId = _chatGptAccountId,
        };
        ConnectionStatus = $"正在通过 {TranslationProviderConfiguration.Label(provider)} 翻译测试句…";

        try
        {
            if (ConnectionTester is null)
            {
                ConnectionStatus = "等待应用服务接入连接测试";
                return;
            }

            ConnectionTestResult result = await ConnectionTester(
                request,
                cancellationToken);

            if (revision != _connectionRevision) return;

            ConnectionSucceeded = result.Succeeded;
            ConnectionBenchmark = result.Benchmark;
            if (result.Benchmark is { } benchmark) ConnectionTranslation = benchmark.Translation;
            ConnectionStatus = result.Elapsed is { } elapsed && result.Succeeded
                ? $"{result.Message} · {elapsed.TotalMilliseconds:F0} ms"
                : result.Message;
        }
        catch (OperationCanceledException)
        {
            if (revision == _connectionRevision) ConnectionStatus = "连接测试已取消";
        }
        catch (Exception exception)
        {
            if (revision == _connectionRevision) ConnectionStatus = $"连接失败：{exception.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}

public sealed record TranslationConnectionTestRequest(
    string ApiKey,
    string Model,
    string BaseUrl,
    TranslationProviderKind Provider = TranslationProviderKind.DeepSeek,
    string? SecretName = null, ApiNetworkOptions? Network = null)
{
    public ScreenTranslator.Core.Models.ThinkingLevel ThinkingLevel { get; init; }
    public IProgress<ScreenTranslator.Core.Models.TranslationStreamUpdate>? Progress { get; init; }
    public bool UseChatGptPlan { get; init; }
    public string ChatGptAccountId { get; init; } = "";
    public override string ToString() => $"Connection test ({Provider})";
}

public sealed class ProviderPresetViewModel(TranslationProviderPreset config) : ObservableObject
{
    private string _name = config.Name;
    public string Id => Config.Id;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public TranslationProviderPreset Config { get; set; } = config;
}

public sealed record ConnectionTestResult(
    bool Succeeded,
    string Message,
    TimeSpan? Elapsed = null)
{
    public TranslationBenchmarkResult? Benchmark { get; init; }
}
