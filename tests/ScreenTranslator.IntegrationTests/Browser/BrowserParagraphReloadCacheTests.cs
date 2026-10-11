using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserParagraphReloadCacheTests
{
    private const string Before = "In France, interest payments could exceed 90 billion euros ($100 billion) next year. Emmanuel Moulin told";
    private const string Link = "The Financial Times";
    private const string After = "that public finances were under pressure.";
    private static string Article => Before + " " + Link + " " + After;
    private static BrowserPageTranslationOptions Options => new() { CacheScope = "same-provider-model-endpoint-prompt-094" };

    [Fact]
    public async Task ArticleInlineNodesCanMergeAfterReloadAndRestartWithZeroProviderCreation()
    {
        using var directory = new TestDirectory();
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var first = new BrowserPageTranslationService(_ => new ArticleProvider(), cache: cache);
            var original = await Translate(first, Fine("initial"));
            Assert.Equal(3, original.CompletedCount); Assert.Equal(1, original.RequestCount);
            await cache.FlushAsync();
        }
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        var providerCreations = 0; var diagnostics = new List<string>();
        var reloaded = new BrowserPageTranslationService(_ => { providerCreations++; throw new InvalidOperationException("Must use persisted artifact."); },
            cache: restarted, diagnostic: diagnostics.Add);
        var merged = await Translate(reloaded, [Block("new-node", Article, 0, context: null)]);
        Assert.Equal(0, providerCreations); Assert.Equal(0, merged.RequestCount); Assert.Equal(1, merged.CacheHits);
        var text = Assert.Single(merged.Blocks).Translation;
        Assert.Contains("900亿欧元", text); Assert.Contains("1000亿美元", text); Assert.Contains("金融时报", text);
        Assert.True(merged.Blocks[0].FromCache); Assert.False(merged.Blocks[0].Failed);
        Assert.Contains(diagnostics, line => line.Contains("artifact_hits=1") && line.Contains("requests=0"));
        Assert.All(diagnostics, line => { Assert.DoesNotContain("Emmanuel", line); Assert.DoesNotContain(Article, line); });
    }

    [Fact]
    public async Task LongGroupSplitIntoDifferentCandidateBatchesKeepsExactRangesReusable()
    {
        var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => new ArticleProvider(), cache: cache);
        var source = Fine("lazy");
        // The first viewport batch was cut before the inline link; another batch
        // arrives after the page refills. Their whole-source metadata is identical.
        Assert.Equal(1, (await Translate(service, [source[0]])).RequestCount);
        Assert.Equal(1, (await Translate(service, source.Skip(1).ToArray())).RequestCount);
        var creators = 0;
        var after = new BrowserPageTranslationService(_ => { creators++; throw new InvalidOperationException(); }, cache: cache);
        var exact = await Translate(after, Fine("new-dom-ids"));
        Assert.Equal(3, exact.CacheHits); Assert.Equal(0, exact.RequestCount);
        var coarse = await Translate(after, [Block("merged", Article, 0, context: null)]);
        Assert.Equal(0, coarse.RequestCount); Assert.Equal(0, creators);
    }

    [Fact]
    public async Task NewTwoNodeInlineBoundariesReuseFineRangesWhileOriginalLinkTranslationStaysNonempty()
    {
        var service = new BrowserPageTranslationService(_ => new ArticleProvider());
        await Translate(service, Fine("first"));
        var two = new[] { Block("text-plus-link", Before + " " + Link, 0), Block("following-text", After, Before.Length + 1 + Link.Length + 1) };
        var restored = await Translate(service, two);
        Assert.Equal(0, restored.RequestCount); Assert.Equal(2, restored.CacheHits);
        Assert.Contains("金融时报", restored.Blocks[0].Translation); Assert.NotEmpty(restored.Blocks[1].Translation);
    }

    [Fact]
    public async Task CoarseOldTranslationCannotBeGuessedIntoNewFinerLinkNodes()
    {
        var creators = 0;
        var service = new BrowserPageTranslationService(_ => { creators++; return new ArticleProvider(); });
        await Translate(service, [Block("coarse", Article, 0)]);
        var split = await Translate(service, Fine("new-inline-link"));
        Assert.Equal(1, split.RequestCount); Assert.Equal(2, creators);
        Assert.False(Assert.Single(split.Blocks, block => block.Id.EndsWith("-link")).FromCache);
        Assert.Equal("《金融时报》", Assert.Single(split.Blocks, block => block.Id.EndsWith("-link")).Translation);
        Assert.All(split.Blocks, block => Assert.False(string.IsNullOrWhiteSpace(block.Translation)));
    }

    [Fact]
    public async Task SameParagraphWithChangedMoneyOrTerminologyNeverUsesOldRanges()
    {
        var service = new BrowserPageTranslationService(_ => new ArticleProvider());
        await Translate(service, Fine("initial"));
        var changed = Article.Replace("90 billion", "95 billion", StringComparison.Ordinal);
        var changedMoney = new BrowserPageTextBlock("changed", changed, "g", changed,
            Hash(changed), changed.Length, 0, changed.Length, ContextKind: "source");
        var result = await Translate(service, [changedMoney]); Assert.Equal(1, result.RequestCount); Assert.Equal(0, result.CacheHits);
        Assert.Contains("950亿欧元", Assert.Single(result.Blocks).Translation);
        var glossary = Fine("new-glossary").Select(block => block with { Context = "Use financial terminology precisely." }).ToArray();
        Assert.Equal(1, (await Translate(service, glossary)).RequestCount);
    }

    [Fact]
    public async Task SourceRangeHashMismatchOrUnknownForeignGapRefusesCompositeReplay()
    {
        var service = new BrowserPageTranslationService(_ => new ArticleProvider());
        // Translate only surrounding pieces: the untranscribed link is not a gap
        // which may be silently omitted in a later merged paragraph.
        await Translate(service, [Fine("partial")[0], Fine("partial")[2]]);
        Assert.Equal(1, (await Translate(service, [Block("coarse", Article, 0)])).RequestCount);
        var fineService = new BrowserPageTranslationService(_ => new ArticleProvider());
        await Translate(fineService, Fine("known"));
        var spoofedText = Before.Replace("90 billion", "95 billion", StringComparison.Ordinal) + " " + Link;
        var spoofed = new BrowserPageTextBlock("spoofed-range", spoofedText, "g", null,
            Hash(Article), Article.Length, 0, spoofedText.Length);
        Assert.Equal(1, (await Translate(fineService, [spoofed])).RequestCount);
    }

    [Fact]
    public async Task HtmlWhitespaceAndOptionalDerivedContextDoNotForceUnchangedWholeTextToTranslateAgain()
    {
        var creators = 0;
        var service = new BrowserPageTranslationService(_ => { creators++; return new ArticleProvider(); });
        await Translate(service, [new("plain-before", After)]);
        var hydrated = new BrowserPageTextBlock("hydrated-new-id", After.Replace("public finances", "public\n\tfinances", StringComparison.Ordinal),
            Context: After);
        Assert.Equal(0, (await Translate(service, [hydrated])).RequestCount); Assert.Equal(1, creators);
        var terms = hydrated with { Id = "new-user-terms", Context = "Use fiscal public-sector terminology." };
        Assert.Equal(1, (await Translate(service, [terms])).RequestCount);
    }

    [Fact]
    public async Task ArticleNativeNumbersCreditsUrlsAndChineseChunksRemainReadonlyAndNeverReachProvider()
    {
        var provider = new ArticleProvider();
        var service = new BrowserPageTranslationService(_ => provider);
        BrowserPageTextBlock[] source = [new("before", Before, "p"), new("link", Link, "p"), new("after", After, "p"),
            new("native", "法国财政面临压力。", "native"), new("percent", "120%", "percent"),
            new("date", "October 9, 2026", "date"), new("credit", "Sarah Meyssonnier/Reuters", "credit"),
            new("url", "https://example.test/article", "url"), new("menu", "Read more", "menu")];
        var result = await Translate(service, source);
        Assert.Equal(4, provider.Seen.Count); Assert.All(provider.Seen, text => Assert.Contains(text, new[] { Before, Link, After, "Read more" }));
        foreach (var native in source.Skip(3).Where(block => block.Id is not ("menu" or "date")))
            Assert.Equal(native.Text, Assert.Single(result.Blocks, block => block.Id == native.Id).Translation);
        Assert.Equal("2026年10月9日", Assert.Single(result.Blocks, block => block.Id == "date").Translation);
        Assert.NotEqual("Read more", Assert.Single(result.Blocks, block => block.Id == "menu").Translation);
        Assert.Equal(4, service.CacheCount);
    }

    [Fact]
    public async Task PersistedRangeArtifactsContainOnlySourceDigestsAndTargets()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        await Translate(new BrowserPageTranslationService(_ => new ArticleProvider(), cache: cache), Fine("first")); await cache.FlushAsync();
        var keys = Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories)
            .Select(System.IO.Path.GetFileNameWithoutExtension).Select(key => key!).ToArray();
        var entries = await cache.GetManyAsync(keys);
        var artifact = Assert.Single(entries.Values, value => value.StartsWith("{", StringComparison.Ordinal));
        Assert.DoesNotContain(Before, artifact); Assert.DoesNotContain(Link, artifact); Assert.DoesNotContain(After, artifact);
        Assert.DoesNotContain("sourceText", artifact); Assert.DoesNotContain("context", artifact);
        using var json = JsonDocument.Parse(artifact);
        Assert.All(json.RootElement.GetProperty("ranges").EnumerateArray(), range =>
            Assert.Equal(new[] { "length", "sourceHash", "start", "translation" }, range.EnumerateObject().Select(field => field.Name).Order().ToArray()));
    }

    [Theory]
    [InlineData("Learn more", false)]
    [InlineData("Privacy policy", false)]
    [InlineData("Photo: Sarah Meyssonnier/Reuters", true)]
    public async Task PublicControlsTranslateInBothModesWhileWholeCreditsRequireExplicitSelection(string source, bool credit)
    {
        var provider = new ArticleProvider();
        var service = new BrowserPageTranslationService(_ => provider);
        var automatic = await Translate(service, [new("auto", source)]);
        Assert.Equal(credit ? 0 : 1, automatic.RequestCount);
        if (credit) Assert.Equal(source, Assert.Single(automatic.Blocks).Translation);
        else Assert.NotEqual(source, Assert.Single(automatic.Blocks).Translation);
        var selected = await service.TranslateAsync([new("selection", source)], Options with { ExplicitSelection = true },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(credit ? 1 : 0, selected.RequestCount); Assert.Single(provider.Seen); Assert.NotEqual(source, Assert.Single(selected.Blocks).Translation);
        var automaticAgain = await Translate(service, [new("page", source)]);
        Assert.Equal(0, automaticAgain.RequestCount);
        if (credit) Assert.Equal(source, Assert.Single(automaticAgain.Blocks).Translation);
        else Assert.Equal(Assert.Single(selected.Blocks).Translation, Assert.Single(automaticAgain.Blocks).Translation);
    }

    [Fact]
    public async Task SelectionDoesNotForceUrlNumbersFormulaOrNativeTargetTextIntoProvider()
    {
        var creations = 0;
        var service = new BrowserPageTranslationService(_ => { creations++; throw new InvalidOperationException(); });
        BrowserPageTextBlock[] blocks = [new("url", "https://example.test"), new("number", "120%"),
            new("formula", "x+y=2"), new("native", "这是中文正文。")];
        var result = await service.TranslateAsync(blocks, Options with { ExplicitSelection = true },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(0, result.RequestCount); Assert.Equal(0, creations);
        Assert.All(result.Blocks, item => Assert.Equal(blocks.First(block => block.Id == item.Id).Text, item.Translation));
    }

    [Fact]
    public async Task TwoServicesCompletingDifferentRangesCannotOverwriteOneAnothersArtifact()
    {
        // Use the default shared hot cache for the actual race; separate service
        // instances represent independently started scopes with the same source.
        await using var shared = new BrowserPageTranslationCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new BrowserPageTranslationService(_ => new DelayedArticleProvider(started, release), cache: shared);
        var source = Fine("concurrent");
        var waiting = Translate(second, source.Skip(1).ToArray());
        try
        {
            var deadline = started.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(waiting, deadline) == waiting)
            { await waiting; throw new InvalidOperationException("The delayed provider did not start."); }
            await deadline;
            var first = new BrowserPageTranslationService(_ => new ArticleProvider(), cache: shared);
            await Translate(first, [source[0]]);
        }
        finally
        {
            release.TrySetResult();
            await waiting.WaitAsync(TimeSpan.FromSeconds(8));
        }
        var creators = 0;
        var merged = new BrowserPageTranslationService(_ => { creators++; throw new InvalidOperationException(); }, cache: shared);
        Assert.Equal(0, (await Translate(merged, [Block("coarse", Article, 0, context: null)])).RequestCount);
        Assert.Equal(0, creators);
    }

    [Fact]
    public async Task ExactLegacyV4WholeNodeWithoutDerivedContextMigratesWithoutProvider()
    {
        await using var cache = new BrowserPageTranslationCache();
        var source = After; var options = Options;
        var oldGroup = source.Length + ":" + source;
        var values = new[] { "page-text-v4-scoped-facts", source, options.SourceLanguage, options.TargetLanguage,
            options.Style.ToString(), options.Context, options.CacheScope, "", oldGroup };
        var key = Hash(string.Concat(values.Select(value => value.Length + ":" + value)));
        cache.Set(key, "称公共财政正承受压力。");
        var creators = 0; var diagnostics = new List<string>();
        var service = new BrowserPageTranslationService(_ => { creators++; throw new InvalidOperationException(); }, cache: cache, diagnostic: diagnostics.Add);
        var current = new BrowserPageTextBlock("new", source, "different-generated-group-id", source,
            Hash(source), source.Length, 0, source.Length, ContextKind: "source");
        var result = await Translate(service, [current]); Assert.Equal(0, result.RequestCount); Assert.Equal(0, creators);
        Assert.Equal("称公共财政正承受压力。", Assert.Single(result.Blocks).Translation);
        Assert.Contains(diagnostics, line => line.Contains("legacy_hits=1"));
        var changedContext = current with { Id = "new-independent-context", Context = "Use different fiscal terminology." };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Translate(service, [changedContext]));
    }

    [Fact]
    public async Task SelectedKanjiUsesOnlyBooleanJapaneseEvidenceWithoutSendingUnselectedKana()
    {
        var provider = new ArticleProvider(); var service = new BrowserPageTranslationService(_ => provider);
        var options = Options with { ExplicitSelection = true };
        var native = await service.TranslateAsync([new("native", "東京都", Context: "東京都")], options,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(0, native.RequestCount);
        var selected = await service.TranslateAsync([new("selected", "東京都", Context: "東京都", ForeignHanContext: true)], options,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, selected.RequestCount); Assert.Equal("東京都", Assert.Single(provider.Seen));
        var request = Assert.Single(provider.Requests);
        Assert.Equal("ja", request.SourceLanguage);
        Assert.DoesNotContain("に", request.Context);
        Assert.Equal("東京都", Assert.Single(request.Blocks).Text);
        var after = await service.TranslateAsync([new("later-native", "東京都", Context: "東京都")], options,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(0, after.RequestCount); Assert.Equal("東京都", Assert.Single(after.Blocks).Translation);
    }

    [Fact]
    public async Task PartialThreeOfFiveInlineSelectionThenPageAndReloadReusesAcceptedBodyRanges()
    {
        var provider = new ArticleProvider();
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var source = SelectionArticle("first", longPrefix: false);
        var selected = SelectionHint(source);
        Assert.Equal(49, selected[0].Context!.Length);
        Assert.Equal(9, selected[0].Context!.Split(' ').Length);
        var first = await service.TranslateAsync(selected, Options with { ExplicitSelection = true },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, first.RequestCount); Assert.Equal(3, first.CompletedCount);
        Assert.Equal(selected.Select(block => block.Text), provider.Seen);
        Assert.DoesNotContain(source[0].Text, Assert.Single(provider.Requests).Context);
        var page = await Translate(service, source);
        Assert.Equal(1, page.RequestCount); Assert.Equal(3, page.CacheHits);
        Assert.Equal(5, provider.Seen.Count);
        Assert.Equal(new[] { source[0].Text, source[4].Text }, provider.Seen.Skip(3));
        var creations = 0;
        var newDocument = new BrowserPageTranslationService(_ => { creations++; throw new InvalidOperationException(); }, cache: cache);
        var restored = await Translate(newDocument, SelectionArticle("new-document", longPrefix: false));
        Assert.Equal(0, creations); Assert.Equal(0, restored.RequestCount); Assert.Equal(5, restored.CacheHits);
        Assert.All(restored.Blocks, block => Assert.True(block.IsComplete && block.FromCache && !block.Failed));
    }

    [Fact]
    public async Task LongParentSelectionAndSourcePrefixTailBatchesShareOnlyDerivedContext()
    {
        var provider = new ArticleProvider();
        var service = new BrowserPageTranslationService(_ => provider);
        var source = SelectionArticle("long", longPrefix: true);
        Assert.Equal(512, source[0].Context!.Length);
        Assert.True(source[1].SourceStart > 512);
        var selected = SelectionHint(source);
        Assert.Equal(1, (await service.TranslateAsync(selected, Options with { ExplicitSelection = true },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None)).RequestCount);
        // None of these page chunks overlaps the first 512 characters, but their
        // stable parent identity still proves this is the source-derived hint.
        var tail = await Translate(service, source.Skip(1).Take(3).ToArray());
        Assert.Equal(0, tail.RequestCount); Assert.Equal(3, tail.CacheHits);
        Assert.Equal(1, (await Translate(service, [source[0], source[4]])).RequestCount);
        var reload = await Translate(service, SelectionArticle("long-new-document", longPrefix: true));
        Assert.Equal(0, reload.RequestCount); Assert.Equal(5, reload.CacheHits);
        Assert.Equal(5, provider.Seen.Count);
        Assert.DoesNotContain(source[0].Text, provider.Requests[0].Context);
    }

    [Fact]
    public async Task DerivedSelectionSharingKeepsIndependentTermsAndChangedAuthorParentIsolated()
    {
        var service = new BrowserPageTranslationService(_ => new ArticleProvider());
        var source = SelectionArticle("source", longPrefix: false);
        var selected = SelectionHint(source);
        await service.TranslateAsync(selected, Options with { ExplicitSelection = true },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var privateHint = source.Skip(1).Take(3).Select(block => block with
        { Id = block.Id + "-private", Context = "Use public-finance terminology.", ContextKind = "selection" }).ToArray();
        Assert.Equal(1, (await Translate(service, privateHint)).RequestCount);
        var unknownHint = selected.Select(block => block with { Id = block.Id + "-unknown", ContextKind = null }).ToArray();
        Assert.Equal(1, (await Translate(service, unknownHint)).RequestCount);
        var fullParentPrivateHint = selected.Select(block => block with { Id = block.Id + "-full-parent-private",
            Context = source[0].Context }).ToArray();
        Assert.Equal(1, (await Translate(service, fullParentPrivateHint)).RequestCount);
        var userTerms = await service.TranslateAsync(source.Skip(1).Take(3).ToArray(), Options with
        { Context = "Translate debt as sovereign liabilities." }, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, userTerms.RequestCount);
        var changedParent = SelectionArticle("different-author", longPrefix: false, changedAuthor: true);
        Assert.Equal(1, (await Translate(service, changedParent.Skip(1).Take(3).ToArray())).RequestCount);
    }

    [Fact]
    public async Task SourceHintConflictWithKnownPrefixDoesNotReuseDerivedContextCache()
    {
        var service = new BrowserPageTranslationService(_ => new ArticleProvider());
        var source = SelectionArticle("long", longPrefix: true);
        await Translate(service, source);
        var conflict = source.Select(block => block with { Id = block.Id + "-conflict",
            Context = "Z" + block.Context![1..] }).ToArray();
        Assert.Equal(1, (await Translate(service, conflict)).RequestCount);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("source")]
    public async Task LegacyOpaqueSingleWordCannotSupplyPartialSelectionFromAnotherAuthorParent(string contextKind)
    {
        await using var cache = new BrowserPageTranslationCache();
        var options = Options; const string selected = "bank";
        var values = new[] { "page-text-v4-scoped-facts", selected, options.SourceLanguage, options.TargetLanguage,
            options.Style.ToString(), options.Context, options.CacheScope, "", "4:bank" };
        cache.Set(Hash(string.Concat(values.Select(value => value.Length + ":" + value))), "旧标题银行");
        const string parent = "We rested beside the river bank after the walk.";
        var current = new BrowserPageTextBlock("selected", selected, "new-parent", contextKind == "source" ? parent : selected,
            Hash(parent), parent.Length, parent.IndexOf(selected, StringComparison.Ordinal), selected.Length, ContextKind: contextKind);
        var service = new BrowserPageTranslationService(_ => new ArticleProvider(), cache: cache);
        var result = await service.TranslateAsync([current], options with { ExplicitSelection = true },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, result.RequestCount); Assert.Equal(0, result.CacheHits);
        Assert.NotEqual("旧标题银行", Assert.Single(result.Blocks).Translation);
    }

    [Theory]
    [InlineData("東京都", true)]
    [InlineData("Photo: Sarah Meyssonnier/Reuters", false)]
    public async Task LegacyCacheWithoutPolicyTraitsCannotOverrideJapaneseEvidenceOrExplicitSelection(string source, bool foreignHan)
    {
        await using var cache = new BrowserPageTranslationCache();
        var options = Options with { ExplicitSelection = true };
        var values = new[] { "page-text-v4-scoped-facts", source, options.SourceLanguage, options.TargetLanguage,
            options.Style.ToString(), options.Context, options.CacheScope, source, "" };
        cache.Set(Hash(string.Concat(values.Select(value => value.Length + ":" + value))), "不应复用的旧模式译文");
        var service = new BrowserPageTranslationService(_ => new ArticleProvider(), cache: cache);
        var result = await service.TranslateAsync([new("selected", source, Context: source, ForeignHanContext: foreignHan)], options,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, result.RequestCount); Assert.Equal(0, result.CacheHits);
        Assert.NotEqual("不应复用的旧模式译文", Assert.Single(result.Blocks).Translation);
    }

    private static BrowserPageTextBlock[] SelectionArticle(string prefix, bool longPrefix, bool changedAuthor = false)
    {
        var introduction = longPrefix ? string.Join(' ', Enumerable.Repeat("Public reports explained the financial situation carefully.", 11))
            : changedAuthor ? "Another author reviewed the national economy." : "An author reviewed the national economy.";
        string[] pieces = [introduction, "The risk of", "public debt", "was growing very quickly.", "Officials discussed the next steps."];
        var full = string.Join(' ', pieces); var cursor = 0;
        return pieces.Select((text, index) =>
        {
            var block = new BrowserPageTextBlock(prefix + "-" + index, text, prefix + "-group",
                full[..Math.Min(512, full.Length)], Hash(full), full.Length, cursor, text.Length, ContextKind: "source");
            cursor += text.Length + 1; return block;
        }).ToArray();
    }

    private static BrowserPageTextBlock[] SelectionHint(BrowserPageTextBlock[] source)
    {
        var chosen = source.Skip(1).Take(3).ToArray();
        var hint = string.Join(' ', chosen.Select(block => block.Text));
        return chosen.Select(block => block with { Context = hint, ContextKind = "selection" }).ToArray();
    }

    private static BrowserPageTextBlock[] Fine(string prefix) => [Block(prefix + "-before", Before, 0),
        Block(prefix + "-link", Link, Before.Length + 1), Block(prefix + "-after", After, Before.Length + 1 + Link.Length + 1)];
    private static BrowserPageTextBlock Block(string id, string text, int start, string? context = "source") =>
        new(id, text, "group", context == "source" ? Article : context, Hash(Article), Article.Length, start, text.Length, ContextKind: "source");
    private static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    private static Task<BrowserPageTranslationResult> Translate(BrowserPageTranslationService service, IReadOnlyList<BrowserPageTextBlock> blocks) =>
        service.TranslateAsync(blocks, Options, (_, _) => ValueTask.CompletedTask, CancellationToken.None);

    private sealed class ArticleProvider : ITranslationProvider
    {
        public List<string> Seen { get; } = [];
        public List<TranslationRequest> Requests { get; } = [];
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Seen.AddRange(request.Blocks.Select(block => block.Text));
            string Translate(string source) => source == Link ? "《金融时报》" : source.StartsWith("that public", StringComparison.Ordinal) ? "称公共财政正承受压力。"
                : source.StartsWith("In France", StringComparison.Ordinal) ? (source.Contains("95 billion", StringComparison.Ordinal) ? "在法国，明年利息支出可能超过950亿欧元（1000亿美元）。埃马纽埃尔·穆兰表示" : "在法国，明年利息支出可能超过900亿欧元（1000亿美元）。埃马纽埃尔·穆兰表示")
                    + (source.Contains(Link, StringComparison.Ordinal) ? "《金融时报》" : "") + (source.Contains(After, StringComparison.Ordinal) ? "称公共财政正承受压力。" : "")
                : "已翻译的正文。";
            return Task.FromResult(JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation = Translate(block.Text) }) }));
        }
    }

    private sealed class DelayedArticleProvider(TaskCompletionSource started, TaskCompletionSource release) : ITranslationProvider
    {
        public async Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
        { started.TrySetResult(); await release.Task.WaitAsync(token); return await new ArticleProvider().TranslateRawAsync(request, token); }
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, "work", "paragraph-reload-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
