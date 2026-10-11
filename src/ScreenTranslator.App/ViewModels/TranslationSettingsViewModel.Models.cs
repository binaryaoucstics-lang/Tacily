using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.App.ViewModels;

public sealed partial class TranslationSettingsViewModel
{
    private int _modelListRevision;
    private bool _isLoadingModels, _updatingModels, _isModelListOpen, _reloadChatGptModels;
    private string _modelListStatus = "";
    private CancellationTokenSource? _modelListCancellation;
    private Func<TranslationModelListRequest, CancellationToken, Task<TranslationModelList>>? _modelListLoader;
    public IAsyncRelayCommand LoadModelsCommand { get; private set; } = null!;
    public IRelayCommand CancelLoadModelsCommand { get; private set; } = null!;
    public Func<TranslationModelListRequest, CancellationToken, Task<TranslationModelList>>? ModelListLoader
    {
        get => _modelListLoader;
        set { _modelListLoader = value; LoadModelsCommand.NotifyCanExecuteChanged(); }
    }
    public bool SupportsModelListing => ProviderKind != TranslationProviderKind.DeepSeek;
    public bool IsLoadingModels
    {
        get => _isLoadingModels;
        private set { if (SetProperty(ref _isLoadingModels, value)) { OnPropertyChanged(nameof(ModelListButtonText)); LoadModelsCommand.NotifyCanExecuteChanged(); CancelLoadModelsCommand.NotifyCanExecuteChanged(); } }
    }
    public string ModelListButtonText => IsLoadingModels ? "正在读取…" : "读取模型列表";
    public string ModelListStatus { get => _modelListStatus; private set => SetProperty(ref _modelListStatus, value); }
    public bool IsModelListOpen { get => _isModelListOpen; set => SetProperty(ref _isModelListOpen, value); }
    private void InitializeModelDiscovery()
    {
        LoadModelsCommand = new AsyncRelayCommand(LoadModelsAsync, CanLoadModels);
        CancelLoadModelsCommand = new RelayCommand(() => { _reloadChatGptModels = false; _modelListCancellation?.Cancel(); }, () => IsLoadingModels);
    }
    private bool CanLoadModels() => SupportsModelListing && !IsSaving && !IsLoadingModels && ModelListLoader is not null
        && !IsChatGptBusy && (UsesChatGptPlan ? CanUseChatGptPlan : !string.IsNullOrWhiteSpace(BaseUrl) && (HasSavedApiKey || !string.IsNullOrWhiteSpace(ApiKey)
            || Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback));
    private void InvalidateModelList(bool resetChoices = true)
    {
        _modelListRevision++; _modelListCancellation?.Cancel(); IsModelListOpen = false; ModelListStatus = "";
        _reloadChatGptModels = false;
        if (resetChoices) { ReplaceModels(UsesChatGptPlan ? [] : TranslationProviderConfiguration.Models(ProviderKind)); ChatGptModels.Clear(); }
        OnPropertyChanged(nameof(SupportsModelListing)); LoadModelsCommand.NotifyCanExecuteChanged();
    }
    private void ReplaceModels(IEnumerable<string> choices)
    {
        _updatingModels = true;
        try { Models.Clear(); foreach (var model in choices) Models.Add(model); }
        finally { _updatingModels = false; }
        OnPropertyChanged(nameof(SelectedModel));
    }
    private async Task LoadModelsAsync(CancellationToken token)
    {
        var revision = _modelListRevision;
        var request = new TranslationModelListRequest(ApiKey, UsesChatGptPlan ? TranslationProviderConfiguration.DefaultOpenAiBaseUrl : BaseUrl, ProviderKind, ActiveSecretName, CollectNetwork())
        { UseChatGptPlan = UsesChatGptPlan, ChatGptAccountId = _chatGptAccountId };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _modelListCancellation = cancellation; IsLoadingModels = true; ModelListStatus = "正在读取当前服务商的模型列表…";
        try
        {
            var result = await ModelListLoader!(request, cancellation.Token);
            if (revision != _modelListRevision) return;
            if (cancellation.IsCancellationRequested) { ModelListStatus = "已取消读取，仍可手动填写模型 ID。"; return; }
            if (result.Models.Count == 0) { if (UsesChatGptPlan) { ReplaceModels([]); ChatGptModels.Clear(); SelectedModel = ""; }
                ModelListStatus = UsesChatGptPlan ? "此 ChatGPT 账户暂未返回可用模型，请稍后重试。" : "服务商未返回可选模型，可继续手动输入模型 ID。"; return; }
            ReplaceModels(result.Models);
            if (UsesChatGptPlan)
            {
                var selected = SelectedModel;
                _updatingModels = true;
                ChatGptModels.Clear(); foreach (var choice in result.Choices) ChatGptModels.Add(choice);
                _updatingModels = false;
                SelectedModel = result.Models.Contains(selected) ? selected : result.Models[0];
            }
            ModelListStatus = $"已读取 {result.Models.Count} 个模型{(result.IsTruncated ? "（列表较大，仅显示部分）" : "")}，请选择后保存。";
            IsModelListOpen = true;
        }
        catch (OperationCanceledException) { if (revision == _modelListRevision) ModelListStatus = "已取消读取，仍可手动填写模型 ID。"; }
        catch (ModelCatalogException error) { if (revision == _modelListRevision) ModelListStatus = error.Message; }
        catch (TranslationConfigurationException error) { if (revision == _modelListRevision) ModelListStatus = error.Message; }
        catch (ScreenTranslator.Core.Authentication.ChatGptException error) { if (revision == _modelListRevision) ModelListStatus = error.Message; }
        catch { if (revision == _modelListRevision) ModelListStatus = "读取模型列表失败，请重试或手动输入模型 ID。"; }
        finally
        {
            if (ReferenceEquals(_modelListCancellation, cancellation)) _modelListCancellation = null;
            IsLoadingModels = false;
            if (_reloadChatGptModels && !token.IsCancellationRequested)
            {
                _reloadChatGptModels = false;
                if (CanLoadModels()) await LoadModelsAsync(token);
            }
        }
    }
}

public sealed record TranslationModelListRequest(string ApiKey, string BaseUrl, TranslationProviderKind Provider, string SecretName, ApiNetworkOptions? Network = null)
{
    public bool UseChatGptPlan { get; init; }
    public string ChatGptAccountId { get; init; } = "";
    public override string ToString() => $"Model list request ({Provider})";
}
