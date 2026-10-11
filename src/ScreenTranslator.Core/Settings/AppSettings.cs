using System.Text.Json.Serialization;
using System.Collections.Immutable;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Hotkeys;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Settings;

public enum DisplayMode
{
    SidePanel,
    Overlay,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings
{
    public const int CurrentVersion = 15;

    public int Version { get; init; } = CurrentVersion;

    public string SourceLanguage { get; init; } = "auto";

    public string TargetLanguage { get; init; } = "zh-CN";

    public TranslationStyle TranslationStyle { get; init; } = TranslationStyle.Natural;

    public string TranslationContext { get; init; } = string.Empty;

    public string DeepSeekModel { get; init; } = "deepseek-flash";

    public TranslationProviderKind TranslationProvider { get; init; } = TranslationProviderKind.DeepSeek;
    public ImmutableList<TranslationProviderPreset> TranslationProviderPresets { get; init; } = [];
    public string SelectedTranslationPresetId { get; init; } = "";
    public ApiNetworkOptions ApiNetwork { get; init; } = new();
    public string ChatCompletionsModel { get; init; } = "";
    public string ChatCompletionsBaseUrl { get; init; } = TranslationProviderConfiguration.DefaultOpenAiBaseUrl;
    public string OpenAiModel { get; init; } = TranslationProviderConfiguration.DefaultOpenAiModel;
    public string OpenAiBaseUrl { get; init; } = TranslationProviderConfiguration.DefaultOpenAiBaseUrl;
    public TranslationAuthenticationMode OpenAiAuthenticationMode { get; init; }
    public string ChatGptAccountId { get; init; } = "";
    public string ClaudeModel { get; init; } = TranslationProviderConfiguration.DefaultClaudeModel;
    public string ClaudeBaseUrl { get; init; } = TranslationProviderConfiguration.DefaultClaudeBaseUrl;

    public ThinkingLevel ThinkingLevel { get; init; } = ThinkingLevel.None;

    public bool BriefThinkingGuidanceEnabled { get; init; } = true;

    public bool BoundedLowThinkingEnabled { get; init; } = true;


    public TranslationInputMode InputMode { get; init; } = TranslationInputMode.VisionOnly;
    public bool InPlaceTranslationEnabled { get; init; } = true;

    public TranslationTriggerMode TriggerMode { get; init; } = TranslationTriggerMode.ClickOrDrag;
    public string DeepSeekBaseUrl { get; init; } = "https://api.deepseek.com";

    public DisplayMode DisplayMode { get; init; } = DisplayMode.SidePanel;

    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool TranslationAnimationsEnabled { get; init; } = true;

    public string Hotkey { get; init; } = HotkeyGesture.Default.ToPersistedString();

    public bool HotkeyEnabled { get; init; } = true;

    public bool BrowserFollowingEnabled { get; init; }

    public bool BrowserAutoImageTranslationEnabled { get; init; }

    public bool BrowserAutoPageTranslationEnabled { get; init; }
    public ImmutableList<string> BrowserTranslationWhitelist { get; init; } = [];
    public ImmutableList<string> BrowserTranslationBlacklist { get; init; } = [];

    public BrowserPageJudgementMode BrowserPageJudgementMode { get; init; } = BrowserPageJudgementMode.Rules;

    public int BrowserPageTranslationConcurrency { get; init; } = 10;
    public bool BrowserPageCacheEnabled { get; init; } = true;
    public int BrowserPageCacheMegabytes { get; init; } = 256;

    public WindowPlacement? SidePanelPlacement { get; init; }

    public double MinimumOverlayFontSize { get; init; } = 12;

    public double MaximumOverlayFontSize { get; init; } = 32;

    public double OverlayOpacity { get; init; } = 1;

    public bool SaveHistory { get; init; }

    public bool StartWithWindows { get; init; }

    public bool StartSilently { get; init; }

    public bool MinimizeToTray { get; init; } = true;

    public bool ShowTrayIcon { get; init; } = true;

    public AppSettings Migrate(out bool hotkeyWasReset)
    {
        hotkeyWasReset = !HotkeyGesture.TryParse(Hotkey, out var gesture);
        var placement = SidePanelPlacement is { } value && value.IsValid
            ? value
            : null;
        var presets = (TranslationProviderPresets ?? []).Where(p => p is not null && Guid.TryParseExact(p.Id, "N", out _)
            && Enum.IsDefined(p.Provider)).DistinctBy(p => p.Id).Select(p => p with
            { Name = string.IsNullOrWhiteSpace(p.Name) ? "自定义服务商" : p.Name.Trim(),
              Model = p.Model?.Trim() ?? "", BaseUrl = p.BaseUrl?.Trim() ?? "",
              ThinkingLevel = Enum.IsDefined(p.ThinkingLevel) ? p.ThinkingLevel : ThinkingLevel.None,
              AuthenticationMode = p.Provider == TranslationProviderKind.OpenAI && Enum.IsDefined(p.AuthenticationMode)
                  ? p.AuthenticationMode : TranslationAuthenticationMode.ApiKey,
              ChatGptAccountId = Guid.TryParseExact(p.ChatGptAccountId, "N", out _) ? p.ChatGptAccountId : "",
              Network = (p.Network ?? new()).Normalize() }).ToImmutableList();
        var white = BrowserSitePolicy.NormalizeList(BrowserTranslationWhitelist);
        var black = BrowserSitePolicy.NormalizeList(BrowserTranslationBlacklist);

        return this with
        {
            Version = CurrentVersion,
            ApiNetwork = (ApiNetwork ?? new()).Normalize(),
            BrowserTranslationWhitelist = BrowserTranslationWhitelist is not null && BrowserTranslationWhitelist.SequenceEqual(white) ? BrowserTranslationWhitelist : white,
            BrowserTranslationBlacklist = BrowserTranslationBlacklist is not null && BrowserTranslationBlacklist.SequenceEqual(black) ? BrowserTranslationBlacklist : black,
            TranslationProviderPresets = (TranslationProviderPresets ?? []).SequenceEqual(presets) ? TranslationProviderPresets! : presets,
            SelectedTranslationPresetId = presets.Any(p => p.Id == SelectedTranslationPresetId) ? SelectedTranslationPresetId : "",
            Hotkey = gesture.ToPersistedString(),
            SidePanelPlacement = placement,
            DeepSeekModel = "deepseek-flash",
            DeepSeekBaseUrl = "https://api.deepseek.com",
            TranslationProvider = Enum.IsDefined(TranslationProvider) ? TranslationProvider : TranslationProviderKind.DeepSeek,
            OpenAiModel = string.IsNullOrWhiteSpace(OpenAiModel) ? TranslationProviderConfiguration.DefaultOpenAiModel : OpenAiModel.Trim(),
            OpenAiBaseUrl = string.IsNullOrWhiteSpace(OpenAiBaseUrl) ? TranslationProviderConfiguration.DefaultOpenAiBaseUrl : OpenAiBaseUrl.Trim(),
            OpenAiAuthenticationMode = Enum.IsDefined(OpenAiAuthenticationMode) ? OpenAiAuthenticationMode : TranslationAuthenticationMode.ApiKey,
            ChatGptAccountId = Guid.TryParseExact(ChatGptAccountId, "N", out _) ? ChatGptAccountId : "",
            ClaudeModel = string.IsNullOrWhiteSpace(ClaudeModel) ? TranslationProviderConfiguration.DefaultClaudeModel : ClaudeModel.Trim(),
            ClaudeBaseUrl = string.IsNullOrWhiteSpace(ClaudeBaseUrl) ? TranslationProviderConfiguration.DefaultClaudeBaseUrl : ClaudeBaseUrl.Trim(),
            ThinkingLevel = Version < 7 || !Enum.IsDefined(ThinkingLevel) ? ThinkingLevel.None : ThinkingLevel,
            InputMode = Version < 7 || InputMode == TranslationInputMode.TextOnly || !Enum.IsDefined(InputMode) ? TranslationInputMode.VisionOnly : InputMode,
            TriggerMode = Enum.IsDefined(TriggerMode) ? TriggerMode : TranslationTriggerMode.ClickOrDrag,
            BrowserPageJudgementMode = Enum.IsDefined(BrowserPageJudgementMode) ? BrowserPageJudgementMode : BrowserPageJudgementMode.Rules,
            BrowserPageTranslationConcurrency = Math.Clamp(BrowserPageTranslationConcurrency, 1, 10),
            BrowserPageCacheMegabytes = Math.Clamp(BrowserPageCacheMegabytes, 32, 2048),
            DisplayMode = DisplayMode.SidePanel,
        };
    }
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height)
{
    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}
