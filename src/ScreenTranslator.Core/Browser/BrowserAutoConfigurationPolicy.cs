using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.Core.Browser;

public static class BrowserAutoConfigurationPolicy
{
    public static bool OnlyAnimationPreferenceChanged(AppSettings previous, AppSettings next) =>
        previous.TranslationAnimationsEnabled != next.TranslationAnimationsEnabled
        && (previous with { TranslationAnimationsEnabled = next.TranslationAnimationsEnabled }) == next;
    // Auto admission switches do not change the translation inputs or transport.
    // Keep the accepted manual identities; genuine setting changes still retire them.
    public static bool PreserveManualGeneration(AppSettings previous, AppSettings next) =>
        previous with { BrowserAutoImageTranslationEnabled = next.BrowserAutoImageTranslationEnabled,
            BrowserAutoPageTranslationEnabled = next.BrowserAutoPageTranslationEnabled,
            BrowserTranslationWhitelist = next.BrowserTranslationWhitelist,
            BrowserTranslationBlacklist = next.BrowserTranslationBlacklist,
            BrowserPageTranslationConcurrency = next.BrowserPageTranslationConcurrency } == next;
}
