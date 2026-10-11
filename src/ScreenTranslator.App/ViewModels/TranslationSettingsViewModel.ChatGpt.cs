using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.ViewModels;

public sealed partial class TranslationSettingsViewModel
{
    private TranslationAuthenticationMode _authenticationMode;
    private TranslationAuthenticationMode _defaultOpenAiAuthenticationMode;
    private string _chatGptAccountId = "", _defaultChatGptAccountId = "", _chatGptStatus = "登录后可使用获授权的 ChatGPT 订阅额度，无需 API Key。";
    private bool _isChatGptBusy, _updatingChatGptAccounts;
    private int _accountListRevision;
    public ObservableCollection<string> AuthenticationChoices { get; } = ["API Key", "ChatGPT 登录（使用订阅额度）"];
    public ObservableCollection<ChatGptAccount> ChatGptAccounts { get; } = [];
    public ObservableCollection<TranslationModelChoice> ChatGptModels { get; } = [];
    public bool IsOpenAi => ProviderKind == TranslationProviderKind.OpenAI;
    public bool UsesChatGptPlan => IsOpenAi && _authenticationMode == TranslationAuthenticationMode.ChatGptOAuth;
    public bool UsesApiKey => !UsesChatGptPlan;
    public string SelectedAuthentication
    {
        get => AuthenticationChoices[(int)_authenticationMode];
        set
        {
            var index = AuthenticationChoices.IndexOf(value);
            if (index < 0 || (int)_authenticationMode == index || IsChatGptBusy) return;
            _authenticationMode = (TranslationAuthenticationMode)index;
            OnPropertyChanged();
            InvalidateConnection(); InvalidateModelList(); NotifyChatGptState();
            QueueChatGptModelRefresh();
        }
    }
    public string ChatGptAccountId => _chatGptAccountId;
    public ChatGptAccount? SelectedChatGptAccount
    {
        get => ChatGptAccounts.FirstOrDefault(a => a.Id == _chatGptAccountId);
        set
        {
            if (_updatingChatGptAccounts || IsChatGptBusy || value?.Id == _chatGptAccountId) return;
            SetChatGptAccount(value?.Id ?? "");
            QueueChatGptModelRefresh();
        }
    }
    private void SetChatGptAccount(string id)
    {
        if (_chatGptAccountId == id) return;
        _chatGptAccountId = id;
        InvalidateConnection(); InvalidateModelList();
        OnPropertyChanged(nameof(SelectedChatGptAccount)); OnPropertyChanged(nameof(ChatGptAccountId));
        NotifyChatGptState();
    }
    public bool IsChatGptBusy
    {
        get => _isChatGptBusy;
        private set
        {
            if (!SetProperty(ref _isChatGptBusy, value)) return;
            NotifyChatGptState();
            OnPropertyChanged(nameof(CanEditProvider)); OnPropertyChanged(nameof(CanEditPreset));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }
    public bool CanUseChatGptPlan => SelectedChatGptAccount is { IsConnected: true, CanUsePlan: true, UsagePaused: false };
    public bool IsChatGptUsagePaused => SelectedChatGptAccount?.UsagePaused == true;
    public bool ShowChatGptConsent => SelectedChatGptAccount is { IsConnected: true, CanUsePlan: false };
    public string ChatGptPlanStatus => SelectedChatGptAccount switch
    {
        { UsagePaused: true } => "额度已达到限制 · 请管理额度后恢复翻译",
        { CanUsePlan: true } => "正在使用 ChatGPT 订阅额度",
        { IsConnected: true } => "已登录 · 尚未授权使用 ChatGPT 额度",
        _ => "尚未连接 ChatGPT 账户",
    };
    public string ChatGptStatus { get => _chatGptStatus; private set => SetProperty(ref _chatGptStatus, value); }
    public IAsyncRelayCommand SignInChatGptCommand { get; private set; } = null!;
    public IAsyncRelayCommand AddChatGptAccountCommand { get; private set; } = null!;
    public IAsyncRelayCommand AuthorizeChatGptPlanCommand { get; private set; } = null!;
    public IAsyncRelayCommand SignOutChatGptCommand { get; private set; } = null!;
    public IRelayCommand CancelChatGptSignInCommand { get; private set; } = null!;
    public IRelayCommand ManageChatGptUsageCommand { get; private set; } = null!;
    public IRelayCommand ResumeChatGptUsageCommand { get; private set; } = null!;
    public Func<CancellationToken, Task<IReadOnlyList<ChatGptAccount>>>? ChatGptAccountsLoader { get; set; }
    public Func<ChatGptSignInRequest, CancellationToken, Task<ChatGptSignInResult>>? ChatGptSigner { get; set; }
    public Func<string, ApiNetworkOptions, CancellationToken, Task<ChatGptSignOutResult>>? ChatGptSignOut { get; set; }
    public Action? ChatGptUsageOpener { get; set; }
    public Action<string>? ChatGptUsageResumer { get; set; }

    private void InitializeChatGpt()
    {
        SignInChatGptCommand = new AsyncRelayCommand(token => SignInChatGptAsync(false, false, token), CanSignInChatGpt);
        AddChatGptAccountCommand = new AsyncRelayCommand(token => SignInChatGptAsync(true, false, token), CanSignInChatGpt);
        AuthorizeChatGptPlanCommand = new AsyncRelayCommand(token => SignInChatGptAsync(false, true, token), () => CanSignInChatGpt() && ShowChatGptConsent);
        SignOutChatGptCommand = new AsyncRelayCommand(SignOutChatGptAsync, () => UsesChatGptPlan && !IsChatGptBusy && !IsSaving && SelectedChatGptAccount?.IsConnected == true);
        CancelChatGptSignInCommand = new RelayCommand(() =>
        { SignInChatGptCommand.Cancel(); AddChatGptAccountCommand.Cancel(); AuthorizeChatGptPlanCommand.Cancel(); }, () => IsChatGptBusy);
        ManageChatGptUsageCommand = new RelayCommand(() => ChatGptUsageOpener?.Invoke());
        ResumeChatGptUsageCommand = new RelayCommand(() => ChatGptUsageResumer?.Invoke(_chatGptAccountId), () => IsChatGptUsagePaused && !IsChatGptBusy);
    }
    private bool CanSignInChatGpt() => UsesChatGptPlan && !IsChatGptBusy && !IsSaving;
    private void QueueChatGptModelRefresh()
    {
        if (!UsesChatGptPlan || !CanUseChatGptPlan) return;
        if (IsLoadingModels) _reloadChatGptModels = true;
        else if (CanLoadModels()) _ = LoadModelsCommand.ExecuteAsync(null);
    }
    private void NotifyChatGptState()
    {
        foreach (var name in new[] { nameof(IsOpenAi), nameof(UsesChatGptPlan), nameof(UsesApiKey), nameof(CanUseChatGptPlan), nameof(CanEditEndpoint),
            nameof(CanEditModel), nameof(ChatGptPlanStatus), nameof(IsChatGptUsagePaused), nameof(ShowChatGptConsent),
            nameof(ModelSelectionDescription), nameof(ProviderDescription), nameof(PresetDescription) }) OnPropertyChanged(name);
        SignInChatGptCommand?.NotifyCanExecuteChanged(); AddChatGptAccountCommand?.NotifyCanExecuteChanged();
        AuthorizeChatGptPlanCommand?.NotifyCanExecuteChanged(); SignOutChatGptCommand?.NotifyCanExecuteChanged();
        CancelChatGptSignInCommand?.NotifyCanExecuteChanged(); ResumeChatGptUsageCommand?.NotifyCanExecuteChanged();
        TestConnectionCommand?.NotifyCanExecuteChanged(); LoadModelsCommand?.NotifyCanExecuteChanged();
    }
    public async Task RefreshChatGptAccountsAsync(CancellationToken token = default)
    {
        if (ChatGptAccountsLoader is null) return;
        var revision = ++_accountListRevision;
        var accounts = await ChatGptAccountsLoader(token);
        if (revision != _accountListRevision || token.IsCancellationRequested) return;
        var previousAccount = SelectedChatGptAccount;
        _updatingChatGptAccounts = true;
        try { ChatGptAccounts.Clear(); foreach (var account in accounts) ChatGptAccounts.Add(account); }
        finally { _updatingChatGptAccounts = false; }
        if (previousAccount != SelectedChatGptAccount)
        {
            InvalidateConnection(); InvalidateModelList();
            QueueChatGptModelRefresh();
        }
        OnPropertyChanged(nameof(SelectedChatGptAccount)); NotifyChatGptState();
    }
    private async Task SignInChatGptAsync(bool newAccount, bool consent, CancellationToken token)
    {
        IsChatGptBusy = true;
        ChatGptStatus = "请在系统浏览器中完成 ChatGPT 登录与额度授权…";
        try
        {
            if (ChatGptSigner is null) { ChatGptStatus = "ChatGPT 登录服务尚未就绪。"; return; }
            var result = await ChatGptSigner(new(newAccount ? null : SelectedChatGptAccount?.Id, consent, CollectNetwork()), token);
            await RefreshChatGptAccountsAsync(token);
            SetChatGptAccount(result.Account.Id);
            ChatGptStatus = result.Account.CanUsePlan ? "已登录，翻译将使用此账户授权的 ChatGPT 额度。" : "已登录，请授权使用额度后继续。";
            IsChatGptBusy = false;
            if (result.Account.CanUsePlan) await LoadModelsCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException) { ChatGptStatus = "ChatGPT 登录已取消。"; }
        catch (ChatGptException error) { ChatGptStatus = error.Message; }
        catch { ChatGptStatus = "ChatGPT 登录未完成，请检查网络与本机凭据存储后重试。"; }
        finally
        {
            IsChatGptBusy = false;
            try { await RefreshChatGptAccountsAsync(); } catch { }
        }
    }
    private async Task SignOutChatGptAsync(CancellationToken token)
    {
        if (ChatGptSignOut is null || SelectedChatGptAccount is not { } account) return;
        IsChatGptBusy = true;
        try
        {
            var result = await ChatGptSignOut(account.Id, CollectNetwork(), token);
            InvalidateConnection(); InvalidateModelList();
            ChatGptStatus = result.RevocationConfirmed ? "已退出 ChatGPT，账户连接保留供下次登录。"
                : "本机已退出；未确认远端会话撤销。可在 ChatGPT 设置中断开应用连接。";
            await RefreshChatGptAccountsAsync();
        }
        catch { ChatGptStatus = "退出 ChatGPT 未完成，请重试。"; }
        finally { IsChatGptBusy = false; }
    }
}

public sealed record ChatGptSignInRequest(string? AccountId, bool RequestConsent, ApiNetworkOptions Network);
