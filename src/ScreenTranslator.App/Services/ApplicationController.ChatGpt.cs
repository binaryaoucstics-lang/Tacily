using System.Diagnostics;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using MessageBox = System.Windows.MessageBox;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private readonly ChatGptAccountService _chatGpt;

    private async Task InitializeChatGptAsync()
    {
        TranslationSettings.ChatGptAccountsLoader = _chatGpt.ListAccountsAsync;
        TranslationSettings.ChatGptSigner = SignInChatGptAsync;
        TranslationSettings.ChatGptSignOut = SignOutChatGptAsync;
        TranslationSettings.ChatGptUsageOpener = () => Process.Start(new ProcessStartInfo(ChatGptAccountService.UsageUrl) { UseShellExecute = true });
        TranslationSettings.ChatGptUsageResumer = _chatGpt.ResumeUsage;
        _chatGpt.AccountsChanged += OnChatGptAccountsChanged;
        try
        {
            await TranslationSettings.RefreshChatGptAccountsAsync();
            if (TranslationSettings.UsesChatGptPlan && TranslationSettings.CanUseChatGptPlan)
                await TranslationSettings.LoadModelsCommand.ExecuteAsync(null);
        }
        catch { TranslationSettings.SetConnectionError("读取 ChatGPT 账户失败，请检查本机凭据存储后重试。"); }
    }

    private async Task<ChatGptSignInResult> SignInChatGptAsync(ChatGptSignInRequest request, CancellationToken token)
    {
        var result = await _chatGpt.SignInAsync(_apiHttp.GetClient(request.Network), request.AccountId, request.RequestConsent, token);
        if (result.ShowWelcome)
        {
            MessageBox.Show("你正在使用 ChatGPT 订阅额度。\n\n符合条件的翻译请求会使用此账户授权的计划额度或余额，可随时通过“管理额度”查看和调整。",
                "ChatGPT 已连接", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            await _chatGpt.MarkWelcomeShownAsync(result.Account.Id, token);
        }
        return result;
    }

    private async Task<ChatGptSignOutResult> SignOutChatGptAsync(string id, ApiNetworkOptions network, CancellationToken token)
    {
        var result = await _chatGpt.SignOutAsync(_apiHttp.GetClient(network), id, token);
        SyncAutoImageConfiguration(force: true);
        return result;
    }

    private void OnChatGptAccountsChanged()
    {
        if (_disposed) return;
        _application.Dispatcher.BeginInvoke(new Action(() => _ = RefreshChatGptStateAsync()));
    }

    private async Task RefreshChatGptStateAsync()
    {
        try
        {
            await TranslationSettings.RefreshChatGptAccountsAsync();
            if (TranslationSettings.UsesChatGptPlan)
            {
                if (TranslationSettings.IsChatGptUsagePaused)
                    MainWindow.StatusText = "ChatGPT 额度达到限制，请在翻译设置中管理额度。";
                SyncAutoImageConfiguration(force: true);
            }
        }
        catch { if (!_disposed) TranslationSettings.SetConnectionError("无法更新 ChatGPT 账户状态，请重新打开设置。 "); }
    }
}
