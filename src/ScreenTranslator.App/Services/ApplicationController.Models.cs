using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private async Task<TranslationModelList> ReadModelListAsync(TranslationModelListRequest request, CancellationToken token)
    {
        if (request.UseChatGptPlan)
        {
            if (request.Provider != TranslationProviderKind.OpenAI) throw new TranslationConfigurationException("ChatGPT 登录仅支持 OpenAI Responses。");
            return await new TranslationModelCatalog(_apiHttp.GetClient(request.Network)).ReadChatGptAsync(_chatGpt, request.ChatGptAccountId, token);
        }
        _ = TranslationModelCatalog.Endpoint(request.Provider, request.BaseUrl);
        var key = await ApiKeyResolver.ResolveAsync(request.ApiKey, _secretStore, token, request.Provider, request.SecretName);
        return await new TranslationModelCatalog(_apiHttp.GetClient(request.Network)).ReadAsync(request.Provider, request.BaseUrl, key, token);
    }
}
