using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserPageTranslationCacheTests
{
    [Fact]
    public async Task RestartRestoresVerifiedWholeParagraphWithNewDomIdAndNoProviderCreation()
    {
        using var directory = new TestDirectory();
        var options = new BrowserPageTranslationOptions { CacheScope = "openai|model-A|https://example.test|prompt-v3" };
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var first = new BrowserPageTranslationService(_ => new Provider(), cache: cache);
            var result = await Translate(first, [new("first", "A complete paragraph about the river.")], options);
            Assert.Equal(1, result.RequestCount);
            await cache.FlushAsync();
        }
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var providerCreations = 0;
            var second = new BrowserPageTranslationService(_ => { providerCreations++; throw new InvalidOperationException(); }, cache: cache);
            var updates = new List<BrowserPageTranslationUpdate>();
            var result = await second.TranslateAsync([new("rebuilt-dom", "A complete paragraph about the river.")], options,
                (update, _) => { updates.Add(update); return ValueTask.CompletedTask; }, CancellationToken.None);
            Assert.Equal(0, providerCreations); Assert.Equal(0, result.RequestCount); Assert.Equal(1, result.CacheHits);
            Assert.Equal("rebuilt-dom", Assert.Single(updates).Id);
            Assert.True(updates[0].IsComplete); Assert.True(updates[0].FromCache);
        }
        var bytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories)));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("完整缓存译文", text);
        Assert.DoesNotContain("A complete paragraph", text);
        Assert.DoesNotContain("example.test", text);
        Assert.DoesNotContain("openai", text);
    }

    [Theory]
    [InlineData("openai|model-B|endpoint-A|prompt-A")]
    [InlineData("claude|model-A|endpoint-A|prompt-A")]
    [InlineData("openai|model-A|endpoint-B|prompt-A")]
    [InlineData("openai|model-A|endpoint-A|prompt-B")]
    public async Task TransportModelAndPromptPolicyCannotSharePersistentTranslations(string secondScope)
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var service = new BrowserPageTranslationService(_ => new Provider(), cache: cache);
        var options = new BrowserPageTranslationOptions { CacheScope = "openai|model-A|endpoint-A|prompt-A" };
        await Translate(service, [new("source", "The river is calm.")], options);
        Assert.Equal(1, (await Translate(service, [new("next", "The river is calm.")], options with { CacheScope = secondScope })).RequestCount);
    }

    [Fact]
    public async Task DistantSourceChangeInvalidatesEntireSemanticGroupWhileOtherGroupStaysCached()
    {
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => new Provider(), cache: cache);
        BrowserPageTextBlock[] source = [new("a", "The river.", "p"), new("b", "The bank.", "p"),
            new("c", "The boat.", "p"), new("d", "The door.", "p"), new("independent", "A blue sky.", "q")];
        await Translate(service, source, new());
        var changed = source.Select(block => block.Id == "d" ? block with { Text = "The investment." } : block).ToArray();
        var result = await Translate(service, changed, new());
        Assert.Equal(1, result.CacheHits);
        Assert.True(Assert.Single(result.Blocks, block => block.FromCache).Id == "independent");
        Assert.All(result.Blocks.Where(block => block.Id != "independent"), block => Assert.False(block.FromCache));
    }

    [Fact]
    public async Task MoreThanOld512EntriesStayHotAndDiskRestoresMemoryEvictions()
    {
        using var directory = new TestDirectory();
        await using var large = new BrowserPageTranslationCache();
        var keys = Enumerable.Range(0, 700).Select(Key).ToArray();
        foreach (var key in keys) large.Set(key, "译文");
        Assert.Equal(700, large.Count);
        Assert.Equal(700, (await large.GetManyAsync(keys)).Count);
        await using var small = new BrowserPageTranslationCache(maximumMemoryEntries: 2, directory: directory.Path);
        foreach (var key in keys.Take(8)) small.Set(key, "译文");
        await small.FlushAsync();
        Assert.Equal(2, small.Count);
        Assert.Equal(8, (await small.GetManyAsync(keys.Take(8))).Count);
        Assert.Equal(2, small.Count);
    }

    [Fact]
    public async Task MalformedOrTruncatedDpapiFileIsAColdMissInsteadOfAnApplicationFailure()
    {
        using var directory = new TestDirectory();
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        { cache.Set(Key(1), "完整译文"); await cache.FlushAsync(); }
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        Assert.Empty(await restarted.GetManyAsync([Key(1)]));
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdReadCannotOverwriteNewTranslationOrResurrectRemovalWhileDiskMutationIsPending(bool remove)
    {
        using var directory = new TestDirectory();
        await using (var first = new BrowserPageTranslationCache(directory: directory.Path))
        { first.Set(Key(1), "磁盘中的旧译文"); await first.FlushAsync(); }
        await using var cache = new BrowserPageTranslationCache(maximumMemoryEntries: 0, directory: directory.Path);
        // Hold only the I/O scheduling gate to deterministically admit the cold
        // read before a later mutation; this does not replace file I/O or DPAPI.
        var gate = (SemaphoreSlim)typeof(BrowserPageTranslationCache)
            .GetField("_diskGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        await gate.WaitAsync();
        Task<IReadOnlyDictionary<string, string>> read;
        try
        {
            read = cache.GetManyAsync([Key(1)]);
            Assert.False(read.IsCompleted);
            if (remove) cache.Remove(Key(1)); else cache.Set(Key(1), "新的完整译文");
        }
        finally { gate.Release(); }
        var restored = await read;
        if (remove) Assert.Empty(restored);
        else Assert.Equal("新的完整译文", restored[Key(1)]);
        await cache.FlushAsync();
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        var again = await restarted.GetManyAsync([Key(1)]);
        if (remove) Assert.Empty(again);
        else Assert.Equal("新的完整译文", again[Key(1)]);
    }

    [Fact]
    public async Task EnlargingSamePendingKeyAtCharacterQuotaKeepsItsNewestValue()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(maximumMemoryEntries: 0, directory: directory.Path);
        var gate = (SemaphoreSlim)typeof(BrowserPageTranslationCache)
            .GetField("_diskGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        await gate.WaitAsync();
        var replacement = new string('新', 120_000);
        try
        {
            // 20 × 99,000 chars nearly fills the asynchronous write budget. The
            // first entry then grows, requiring old entries to yield its place.
            for (var index = 0; index < 20; index++) cache.Set(Key(index), new string('旧', 99_000));
            cache.Set(Key(0), replacement);
            Assert.Equal(replacement, (await cache.GetManyAsync([Key(0)]))[Key(0)]);
        }
        finally { gate.Release(); }
        await cache.FlushAsync();
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        Assert.Equal(replacement, (await restarted.GetManyAsync([Key(0)]))[Key(0)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusedWriteDuringColdReadCannotRestoreOldDiskOverNewHotValueOrAfterEviction(bool keepHot)
    {
        using var directory = new TestDirectory();
        await using (var first = new BrowserPageTranslationCache(directory: directory.Path))
        { first.Set(Key(999), "旧磁盘译文"); await first.FlushAsync(); }
        await using var cache = new BrowserPageTranslationCache(maximumMemoryEntries: keepHot ? 64 : 1, directory: directory.Path);
        var gate = (SemaphoreSlim)typeof(BrowserPageTranslationCache)
            .GetField("_diskGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        await gate.WaitAsync();
        Task<IReadOnlyDictionary<string, string>> read;
        var newest = new string('新', 40_000);
        try
        {
            read = cache.GetManyAsync([Key(999)]); Assert.False(read.IsCompleted);
            for (var index = 0; index < 20; index++) cache.Set(Key(index), new string('译', 99_000));
            // New key does not fit the 2M pending character budget. Its hot value
            // is valid, while the old file must never become current again.
            cache.Set(Key(999), newest);
            if (!keepHot) cache.Set(Key(1000), "淘汰前一热项的完整译文");
        }
        finally { gate.Release(); }
        var restored = await read;
        if (keepHot) Assert.Equal(newest, restored[Key(999)]); else Assert.Empty(restored);
        await cache.FlushAsync();
        var again = await cache.GetManyAsync([Key(999)]);
        if (keepHot) Assert.Equal(newest, again[Key(999)]); else Assert.Empty(again);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedAtomicReplaceCannotRestoreOldDiskAfterPendingValueHasDrained(bool keepHot)
    {
        using var directory = new TestDirectory();
        await using (var first = new BrowserPageTranslationCache(directory: directory.Path))
        { first.Set(Key(1), "旧磁盘译文"); await first.FlushAsync(); }
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        await using var cache = new BrowserPageTranslationCache(maximumMemoryEntries: keepHot ? 16 : 0, directory: directory.Path);
        // Read sharing allows cold reads but excludes atomic rename/delete. The
        // write therefore fails after the latest value already entered memory.
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        { cache.Set(Key(1), "新的完整译文"); await cache.FlushAsync(); }
        var result = await cache.GetManyAsync([Key(1)]);
        if (keepHot) Assert.Equal("新的完整译文", result[Key(1)]); else Assert.Empty(result);
        cache.Set(Key(1), "重试成功的新译文"); await cache.FlushAsync();
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        Assert.Equal("重试成功的新译文", (await restarted.GetManyAsync([Key(1)]))[Key(1)]);
    }

    [Fact]
    public async Task ClearDropsPendingWritesAndMemoryWithoutDeletingUnrelatedFiles()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Path);
        var unrelated = System.IO.Path.Combine(directory.Path, "user-notes.txt");
        await File.WriteAllTextAsync(unrelated, "Keep this file");
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        for (var index = 0; index < 500; index++) cache.Set(Key(index), "完成的译文");
        await cache.ClearAsync(); await cache.FlushAsync();
        Assert.Equal(0, cache.Count);
        Assert.Empty(await cache.GetManyAsync([Key(1)]));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        Assert.Equal("Keep this file", await File.ReadAllTextAsync(unrelated));
        cache.Set(Key(999), "清除后的新译文"); await cache.FlushAsync();
        Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ParagraphStartedBeforeClearCannotWriteBackAfterItsProviderCompletes()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BrowserPageTranslationService(_ => new DelayedProvider(started, release), cache: cache);
        var old = Translate(service, [new("a", "The river is calm.")], new());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cache.ClearAsync(); release.TrySetResult(); await old; await cache.FlushAsync();
        Assert.Equal(0, cache.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        var fresh = new BrowserPageTranslationService(_ => new Provider(), cache: cache);
        Assert.Equal(1, (await Translate(fresh, [new("b", "The river is calm.")], new())).RequestCount);
    }

    [Fact]
    public async Task ExplicitClearReportsLockedDiskFailureInsteadOfClaimingSuccess()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        cache.Set(Key(1), "完整译文"); await cache.FlushAsync();
        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => cache.ClearAsync());
        await cache.ClearAsync(); Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DiskQuotaAndShrinkAreBoundedAndRetainMostRecentlyUsedEntries()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(maximumMemoryEntries: 0,
            directory: directory.Path, maximumDiskBytes: 4_096);
        for (var index = 0; index < 20; index++) cache.Set(Key(index), new string('译', 200));
        await cache.FlushAsync();
        Assert.True(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= 4_096);
        Assert.True((await cache.GetManyAsync([Key(19)])).ContainsKey(Key(19)));
        cache.ConfigureDiskLimit(1_024);
        // A new write and flush provide a deterministic maintenance barrier.
        cache.Set(Key(20), "新的译文"); await cache.FlushAsync();
        Assert.True(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= 1_024);
        Assert.True((await cache.GetManyAsync([Key(20)])).ContainsKey(Key(20)));
    }

    [Fact]
    public async Task DisablingPersistenceRetainsFilesButPerformsNoColdReadOrWrite()
    {
        using var directory = new TestDirectory();
        await using (var first = new BrowserPageTranslationCache(directory: directory.Path))
        { first.Set(Key(1), "保留的译文"); await first.FlushAsync(); }
        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        await using var disabled = new BrowserPageTranslationCache(directory: directory.Path, maximumDiskBytes: 0);
        Assert.Empty(await disabled.GetManyAsync([Key(1)]));
        disabled.Set(Key(2), "内存译文");
        Assert.True((await disabled.GetManyAsync([Key(2)])).ContainsKey(Key(2)));
        await disabled.FlushAsync(); Assert.True(File.Exists(file));
        Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        disabled.ConfigureDiskLimit(BrowserPageTranslationCache.DefaultMaximumDiskBytes);
        Assert.True((await disabled.GetManyAsync([Key(1)])).ContainsKey(Key(1)));
        disabled.ConfigureDiskLimit(0); await disabled.ClearAsync();
        Assert.False(File.Exists(file));
        Assert.Empty(await disabled.GetManyAsync([Key(2)]));
    }

    [Fact]
    public async Task IncompleteAndFactCorruptProviderOutputNeverPersists()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var malformed = new BrowserPageTranslationService(_ => new Provider("{\"blocks\":[]}"), cache: cache);
        Assert.Equal(1, (await Translate(malformed, [new("a", "The river is calm.")], new())).FailedCount);
        var corrupt = new BrowserPageTranslationService(_ => new Provider(translation: "在星期二见面。"), cache: cache);
        var result = await Translate(corrupt, [new("a", "Meet on Thursday.")], new());
        Assert.True(Assert.Single(result.Blocks).Failed);
        await cache.FlushAsync();
        Assert.Equal(0, cache.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HtmlWhitespaceReflowReusesWordsButIndependentTerminologyRemainsPartOfIdentity()
    {
        var service = new BrowserPageTranslationService(_ => new Provider());
        await Translate(service, [new("a", "The river is calm.", Context: "water")], new());
        Assert.Equal(0, (await Translate(service, [new("b", "The river  is calm.", Context: "water")], new())).RequestCount);
        Assert.Equal(1, (await Translate(service, [new("c", "The river is calm.", Context: "finance")], new())).RequestCount);
    }

    [Fact]
    public async Task SiblingTerminologyChangeInvalidatesItsWholeGroupButNotAnIndependentParagraph()
    {
        var service = new BrowserPageTranslationService(_ => new Provider());
        BrowserPageTextBlock[] source = [new("a", "The river.", "g", "water"),
            new("b", "The bank.", "g", "shore"), new("c", "The door.", "independent", "building")];
        await Translate(service, source, new());
        var result = await Translate(service, source.Select(block => block.Id == "b"
            ? block with { Context = "finance" } : block).ToArray(), new());
        Assert.Equal(1, result.CacheHits);
        Assert.Equal("c", Assert.Single(result.Blocks, block => block.FromCache).Id);
    }

    [Fact]
    public async Task AtomicUpdatesLoadLatestColdArtifactAndDoNotLoseConcurrentVerifiedValues()
    {
        using var directory = new TestDirectory();
        await using (var initial = new BrowserPageTranslationCache(directory: directory.Path))
        { initial.Set(Key(800), "甲"); await initial.FlushAsync(); }
        await using (var cache = new BrowserPageTranslationCache(maximumMemoryEntries: 0, directory: directory.Path))
        {
            await cache.UpdateAsync(Key(800), old => old + "乙", 0);
            await cache.FlushAsync();
            var updates = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
                cache.UpdateAsync(Key(800), old => old + "丙", 0))).ToArray();
            await Task.WhenAll(updates); await cache.FlushAsync();
            Assert.Equal("甲乙" + new string('丙', 20), (await cache.GetManyAsync([Key(800)]))[Key(800)]);
        }
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        Assert.Equal("甲乙" + new string('丙', 20), (await restarted.GetManyAsync([Key(800)]))[Key(800)]);
    }

    [Fact]
    public async Task AtomicInvalidUpdateKeepsOldVerifiedValueAndLateClearEpochCannotWriteBack()
    {
        await using var cache = new BrowserPageTranslationCache(); cache.Set(Key(900), "已验证译文");
        Assert.False(await cache.UpdateAsync(Key(900), _ => null, 0));
        Assert.Equal("已验证译文", (await cache.GetManyAsync([Key(900)]))[Key(900)]);
        await cache.ClearAsync();
        Assert.False(await cache.UpdateAsync(Key(900), _ => "过期回调", 0));
        Assert.Empty(await cache.GetManyAsync([Key(900)]));
    }

    private static Task<BrowserPageTranslationResult> Translate(BrowserPageTranslationService service,
        IReadOnlyList<BrowserPageTextBlock> blocks, BrowserPageTranslationOptions options) =>
        service.TranslateAsync(blocks, options, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
    private static string Key(int value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));

    private sealed class Provider(string? raw = null, string translation = "完整缓存译文") : ITranslationProvider
    {
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(raw ?? JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation }) }));
    }

    private sealed class DelayedProvider(TaskCompletionSource started, TaskCompletionSource release) : ITranslationProvider
    {
        public async Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            started.TrySetResult(); await release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation = "完整缓存译文" }) });
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, "work", "page-cache-tests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
