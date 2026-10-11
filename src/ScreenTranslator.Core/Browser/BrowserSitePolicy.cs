using System.Collections.Immutable;
using System.Text.Json;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.Core.Browser;

public sealed record BrowserSiteRule(string Mode, string? Domain = null);

public static class BrowserSitePolicy
{
    public const int MaximumDomains = 512;

    public static string? NormalizeDomain(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2048 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl)) return null;
        if (text.StartsWith("*.", StringComparison.Ordinal)) text = text[2..];
        if (!text.Contains("://", StringComparison.Ordinal)) {
            if (Uri.CheckHostName(text.Trim('[', ']')) == UriHostNameType.IPv6) text = "[" + text.Trim('[', ']') + "]";
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return null;
        var domain = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (domain.Length is < 1 or > 253 || domain.Contains('*') || domain.Contains('%') || Uri.CheckHostName(domain) == UriHostNameType.Unknown) return null;
        return domain;
    }

    public static ImmutableList<string> NormalizeList(IEnumerable<string>? values) => (values ?? [])
        .Select(NormalizeDomain).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaximumDomains).ToImmutableList();

    public static bool Matches(string host, string domain) => string.Equals(host, domain, StringComparison.Ordinal)
        || host.EndsWith("." + domain, StringComparison.Ordinal);

    public static BrowserSiteRule Resolve(string? hostname, AppSettings settings)
    {
        var host = NormalizeDomain(hostname); if (host is null) return new("default");
        var allow = settings.BrowserTranslationWhitelist.Where(domain => Matches(host, domain)).OrderByDescending(domain => domain.Length).FirstOrDefault();
        var block = settings.BrowserTranslationBlacklist.Where(domain => Matches(host, domain)).OrderByDescending(domain => domain.Length).FirstOrDefault();
        if (block is not null && (allow is null || block.Length >= allow.Length)) return new("block", block);
        return allow is not null ? new("allow", allow) : new("default");
    }

    public static AppSettings SetRule(AppSettings settings, string host, string mode)
    {
        var domain = NormalizeDomain(host) ?? throw new ArgumentException("Invalid domain.", nameof(host));
        if (mode is not ("default" or "allow" or "block")) throw new ArgumentException("Invalid site mode.", nameof(mode));
        var white = settings.BrowserTranslationWhitelist.RemoveAll(entry => entry == domain);
        var black = settings.BrowserTranslationBlacklist.RemoveAll(entry => entry == domain);
        if (mode == "allow") white = white.Add(domain);
        if (mode == "block") black = black.Add(domain);
        if (white.Count > MaximumDomains || black.Count > MaximumDomains) throw new ArgumentException("Site list limit exceeded.");
        return settings with { BrowserTranslationWhitelist = white, BrowserTranslationBlacklist = black };
    }
}

public sealed record BrowserQuickSettingsPatch(bool? AutoPage = null, bool? AutoImages = null,
    string? TargetLanguage = null, int? Concurrency = null, string? SiteMode = null)
{
    public static bool TryRead(JsonElement changes, out BrowserQuickSettingsPatch? patch)
    {
        patch = null;
        if (changes.ValueKind != JsonValueKind.Object) return false;
        bool? page = null, images = null; string? target = null, mode = null; int? concurrency = null;
        var fields = new HashSet<string>();
        foreach (var field in changes.EnumerateObject()) {
            if (!fields.Add(field.Name)) return false;
            switch (field.Name) {
                case "autoPage":
                case "autoImages":
                    if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    if (field.Name == "autoPage") page = field.Value.GetBoolean(); else images = field.Value.GetBoolean();
                    break;
                case "targetLanguage":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    target = field.Value.GetString();
                    if (target is not ("zh-CN" or "zh-TW" or "en" or "ja" or "ko")) return false;
                    break;
                case "concurrency":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number) || number is < 1 or > 10) return false;
                    concurrency = number; break;
                case "siteMode":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    mode = field.Value.GetString(); if (mode is not ("default" or "allow" or "block")) return false;
                    break;
                default: return false;
            }
        }
        if (fields.Count == 0) return false;
        patch = new(page, images, target, concurrency, mode); return true;
    }

    public AppSettings Apply(AppSettings settings, string host)
    {
        var next = settings with { BrowserAutoPageTranslationEnabled = AutoPage ?? settings.BrowserAutoPageTranslationEnabled,
            BrowserAutoImageTranslationEnabled = AutoImages ?? settings.BrowserAutoImageTranslationEnabled,
            TargetLanguage = TargetLanguage ?? settings.TargetLanguage,
            BrowserPageTranslationConcurrency = Concurrency ?? settings.BrowserPageTranslationConcurrency };
        return SiteMode is null ? next : BrowserSitePolicy.SetRule(next, host, SiteMode);
    }
}
