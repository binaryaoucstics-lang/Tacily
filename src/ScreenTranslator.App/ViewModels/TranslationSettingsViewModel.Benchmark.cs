using CommunityToolkit.Mvvm.Input;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.ViewModels;

public sealed partial class TranslationSettingsViewModel
{
    private string _connectionTranslation = "";
    private TranslationBenchmarkResult? _connectionBenchmark;
    public string ConnectionSource => TranslationBenchmark.SourceSentence;
    public string ConnectionTranslation
    {
        get => _connectionTranslation;
        private set { if (SetProperty(ref _connectionTranslation, value)) OnPropertyChanged(nameof(ShowConnectionDetails)); }
    }
    public TranslationBenchmarkResult? ConnectionBenchmark
    {
        get => _connectionBenchmark;
        private set
        {
            if (!SetProperty(ref _connectionBenchmark, value)) return;
            OnPropertyChanged(nameof(ShowConnectionDetails)); OnPropertyChanged(nameof(FirstCharacterMetric));
            OnPropertyChanged(nameof(ThroughputMetric)); OnPropertyChanged(nameof(TotalTimeMetric)); OnPropertyChanged(nameof(ThroughputNote));
            OnPropertyChanged(nameof(ServiceTierNote)); OnPropertyChanged(nameof(ShowServiceTierNote));
        }
    }
    public bool ShowConnectionDetails => IsTesting || ConnectionTranslation.Length > 0 || ConnectionBenchmark is not null;
    public string FirstCharacterMetric => ConnectionBenchmark?.FirstCharacterLatency is { } first ? $"首字  {first.TotalMilliseconds:F0} ms" : "首字  —";
    public string ThroughputMetric => ConnectionBenchmark?.TokensPerSecond is { } tps ? $"TPS  {tps:F1}" : "TPS  —";
    public string TotalTimeMetric => ConnectionBenchmark is { } value ? $"总耗时  {value.Elapsed.TotalSeconds:F2} s" : "总耗时  —";
    public bool ShowServiceTierNote => ConnectionBenchmark is { } benchmark
        && (UsesChatGptPlan || !string.IsNullOrWhiteSpace(benchmark.RequestedServiceTier)
            || !string.IsNullOrWhiteSpace(benchmark.EffectiveServiceTier));
    public string ServiceTierNote
    {
        get
        {
            if (!ShowServiceTierNote || ConnectionBenchmark is not { } benchmark) return "";
            var requested = benchmark.RequestedServiceTier?.Trim().ToLowerInvariant();
            var effective = benchmark.EffectiveServiceTier?.Trim().ToLowerInvariant();
            var requestNote = requested switch
            {
                "fast" or "priority" => "快速模式",
                "default" => "普通模式",
                null or "" => "未指定快速模式",
                _ => $"未识别的模式（{benchmark.RequestedServiceTier}）",
            };
            var effectiveNote = effective switch
            {
                "fast" or "priority" => "快速模式",
                "default" when requested is "fast" or "priority" => "普通模式（快速模式未生效）",
                "default" => "普通模式",
                null or "" => "未确认（服务端未返回处理模式）",
                _ => $"未确认（服务端返回未识别的模式：{benchmark.EffectiveServiceTier}）",
            };
            return $"请求：{requestNote}；实际：{effectiveNote}。";
        }
    }
    public string ThroughputNote => ConnectionBenchmark switch
    {
        { RateKind: BenchmarkRateKind.ContentGeneration } => "TPS 按服务商 token 用量与内容流持续时间计算，不含思考；结果格式也计入输出 token。",
        { RateKind: BenchmarkRateKind.OverallOutput } => "服务商未给出思考用量分项：显示整体输出 TPS，包含网络等待与思考时间。",
        { OutputTokens: null } => "服务商未返回 token 用量，TPS 不作字数估算。",
        { } => "流式时间样本不足，无法可靠计算 TPS。",
        _ => "实际翻译固定英文句子为简体中文，使用当前模型、节点与思考设置。",
    };
    public IRelayCommand CancelConnectionTestCommand { get; private set; } = null!;
    private void InitializeBenchmark() => CancelConnectionTestCommand = new RelayCommand(() => TestConnectionCommand.Cancel(), () => IsTesting);
}
