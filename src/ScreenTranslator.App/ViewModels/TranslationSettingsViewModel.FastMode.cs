namespace ScreenTranslator.App.ViewModels;

public sealed partial class TranslationSettingsViewModel
{
    private bool _chatGptFastModeEnabled, _defaultChatGptFastModeEnabled;
    public bool ChatGptFastModeEnabled
    {
        get => _chatGptFastModeEnabled;
        set
        {
            if (!CanEditChatGptFastMode || !SetProperty(ref _chatGptFastModeEnabled, value)) return;
            InvalidateConnection();
        }
    }
    public bool CanEditChatGptFastMode => UsesChatGptPlan && CanEditProvider;
    public string ChatGptFastModeDescription => "请求更快的翻译处理，可能更快消耗 ChatGPT 额度；实际支持由账户、工作区和模型决定。";
}
