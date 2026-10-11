using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private async Task<TranslationModelList> ReadModelListAsync(TranslationModelListRequest request, CancellationToken token)
    {
        _ = TranslationModelCatalog.Endpoint(request.Provider, request.BaseUrl);
        var key = await ApiKeyResolver.ResolveAsync(request.ApiKey, _secretStore, token, request.Provider, request.SecretName);
        return await new TranslationModelCatalog(_apiHttp.GetClient(request.Network)).ReadAsync(request.Provider, request.BaseUrl, key, token);
    }
}
