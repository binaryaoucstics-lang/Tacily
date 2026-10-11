using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>
/// Verified complete paragraph translations. Hot reads are synchronous; disk reads
/// and bounded, coalesced writes run off the caller's UI thread. Files contain only
/// an opaque source/settings hash and a DPAPI-protected translation, never keys,
/// endpoint/URL metadata or original paragraphs.
/// </summary>
public sealed class BrowserPageTranslationCache : IAsyncDisposable
{
    public const int DefaultMaximumMemoryEntries = 16_384;
    public const int DefaultMaximumMemoryCharacters = 32_000_000;
    public const long DefaultMaximumDiskBytes = 256L * 1024 * 1024;
    public const long MaximumDiskBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumTranslationCharacters = 131_072;
    private const int MaximumDiskEntries = 100_000;
    private const int MaximumPendingWrites = 2_048;
    private const int MaximumPendingCharacters = 2_000_000;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GlobalAITranslator.PageCache.v1");
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ReadLease>> _reads = new(StringComparer.Ordinal);
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(1);
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly Dictionary<string, DiskEntry> _disk = new(StringComparer.Ordinal);
    private readonly int _maximumMemoryEntries;
    private readonly int _maximumMemoryCharacters;
    private readonly string? _directory;
    private readonly Task _writer;
    private long _maximumDiskBytes;
    private long _diskBytes;
    private int _memoryCharacters;
    private int _pendingCharacters;
    private long _epoch;
    private long _clearEpoch;
    private bool _diskInitialized;
    private bool _disposed;
    private long _enqueued;
    private long _processed;
    private readonly List<(long Sequence, TaskCompletionSource Completion)> _flushes = [];

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlobalAITranslator", "page-translation-cache-v1");

    public BrowserPageTranslationCache(int maximumMemoryEntries = DefaultMaximumMemoryEntries,
        int maximumMemoryCharacters = DefaultMaximumMemoryCharacters, string? directory = null,
        long maximumDiskBytes = DefaultMaximumDiskBytes)
    {
        if (maximumMemoryEntries is < 0 or > 65_536 || maximumMemoryCharacters is < 0 or > 128_000_000)
            throw new ArgumentOutOfRangeException(nameof(maximumMemoryEntries));
        ValidateDiskLimit(maximumDiskBytes);
        _maximumMemoryEntries = maximumMemoryEntries;
        _maximumMemoryCharacters = maximumMemoryCharacters;
        _directory = directory is null ? null : Path.GetFullPath(directory);
        _maximumDiskBytes = maximumDiskBytes;
        _writer = _directory is null ? Task.CompletedTask : Task.Run(WriteAsync);
    }

    public int Count { get { lock (_gate) return _memory.Count; } }
    internal long ClearEpoch { get { lock (_gate) return _clearEpoch; } }

    public void ConfigureDiskLimit(long maximumDiskBytes)
    {
        ValidateDiskLimit(maximumDiskBytes);
        lock (_gate)
        {
            if (_disposed) return;
            _maximumDiskBytes = maximumDiskBytes;
            if (maximumDiskBytes == 0) _epoch++;
            if (_directory is null) return;
            _enqueued++;
            _signal.Writer.TryWrite(true);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var leases = new List<ReadLease>();
        long epoch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            epoch = _epoch;
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                ValidateKey(key);
                if (_memory.TryGetValue(key, out var hit))
                {
                    _lru.Remove(hit); _lru.AddFirst(hit);
                    result.Add(key, hit.Value.Translation);
                }
                else if (_removed.Contains(key)) { }
                else if (_pending.TryGetValue(key, out var queued))
                { if (queued.Translation is { } text) result.Add(key, text); }
                else missing.Add(key);
            }
            if (_directory is null || _maximumDiskBytes == 0 || missing.Count == 0) return result;
            foreach (var key in missing)
            {
                var lease = new ReadLease(key); leases.Add(lease);
                if (!_reads.TryGetValue(key, out var readers)) _reads[key] = readers = [];
                readers.Add(lease);
            }
        }
        // One lock/scan and one set of asynchronous reads per snapshot, not one UI
        // round-trip per paragraph. At most 128 keys are supplied by the service.
        var entered = false;
        try
        {
            await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            lock (_gate) if (_maximumDiskBytes == 0) return result;
            await InitializeDiskAsync(cancellationToken).ConfigureAwait(false);
            foreach (var lease in leases)
            {
                var key = lease.Key;
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_epoch != epoch || _disposed || _maximumDiskBytes == 0) break;
                    if (ReadNewest(lease, result)) continue;
                }
                if (!_disk.TryGetValue(key, out var file)) continue;
                try
                {
                    var encrypted = await File.ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    Entry? entry;
                    try { entry = JsonSerializer.Deserialize<Entry>(plain); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                    if (entry is null || entry.Key != key || !ValidTranslation(entry.Translation))
                        throw new InvalidDataException("Invalid page cache entry.");
                    lock (_gate)
                    {
                        if (_epoch != epoch || _disposed || _maximumDiskBytes == 0) break;
                        if (ReadNewest(lease, result)) continue;
                        SetMemory(entry);
                        result.Add(key, entry.Translation);
                    }
                    _disk[key] = file with { LastUse = DateTime.UtcNow.Ticks };
                    // Last-write time survives a restart and supplies the LRU index.
                    try { File.SetLastWriteTimeUtc(file.Path, DateTime.UtcNow); }
                    catch (Exception error) when (IsRecoverable(error)) { }
                }
                catch (Exception error) when (IsRecoverable(error)) { RemoveDisk(key); }
            }
        }
        catch (Exception error) when (IsRecoverable(error)) { /* Disk cache is optional. */ }
        finally
        {
            if (entered) _diskGate.Release();
            lock (_gate)
                foreach (var lease in leases)
                    if (_reads.TryGetValue(lease.Key, out var readers))
                    {
                        readers.Remove(lease);
                        if (readers.Count == 0) _reads.Remove(lease.Key);
                    }
        }
        return result;
    }

    /// <summary>Called only after complete JSON and semantic fact validation.</summary>
    public void Set(string key, string translation, long? clearEpoch = null)
    {
        ValidateKey(key);
        if (!ValidTranslation(translation)) return;
        lock (_gate)
        {
            if (_disposed || clearEpoch is { } expected && expected != _clearEpoch) return;
            _removed.Remove(key);
            MarkReadersChanged(key);
            SetMemory(new(key, translation));
            if (!Queue(key, translation) && _directory is not null && _maximumDiskBytes > 0) _removed.Add(key);
        }
    }

    public void Remove(string key, long? clearEpoch = null)
    {
        ValidateKey(key);
        lock (_gate)
        {
            if (_disposed || clearEpoch is { } expected && expected != _clearEpoch) return;
            if (_directory is not null) _removed.Add(key);
            MarkReadersChanged(key);
            RemoveMemory(key);
            Queue(key, null);
        }
    }

    /// <summary>
    /// Atomically merges a verified artifact with the latest value, across service
    /// instances/tabs. The delegate runs under the cache lock and must be bounded
    /// and synchronous. Cold disk I/O completes before that lock is acquired.
    /// </summary>
    public async Task<bool> UpdateAsync(string key, Func<string?, string?> update, long clearEpoch,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key); ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || clearEpoch != _clearEpoch) return false;
            if (TryLatestValue(key, out var current) || _directory is null || _maximumDiskBytes == 0)
                return ApplyUpdate(key, current, update);
        }
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? diskValue = null;
        try
        {
            try
            {
                await InitializeDiskAsync(cancellationToken).ConfigureAwait(false);
                if (_disk.TryGetValue(key, out var file))
                {
                    var encrypted = await File.ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    try
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(plain);
                        if (entry?.Key == key && ValidTranslation(entry.Translation)) diskValue = entry.Translation;
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
            }
            catch (Exception error) when (IsRecoverable(error)) { }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed || clearEpoch != _clearEpoch) return false;
                // A concurrently completed paragraph wins over this older read.
                if (TryLatestValue(key, out var latest)) diskValue = latest;
                return ApplyUpdate(key, diskValue, update);
            }
        }
        finally { _diskGate.Release(); }
    }

    private bool TryLatestValue(string key, out string? value)
    {
        if (_memory.TryGetValue(key, out var hot)) { value = hot.Value.Translation; return true; }
        if (_removed.Contains(key)) { value = null; return true; }
        if (_pending.TryGetValue(key, out var pending)) { value = pending.Translation; return true; }
        value = null; return false;
    }

    private bool ApplyUpdate(string key, string? current, Func<string?, string?> update)
    {
        var value = update(current);
        if (!ValidTranslation(value)) return false;
        _removed.Remove(key); MarkReadersChanged(key); SetMemory(new(key, value!));
        if (!Queue(key, value) && _directory is not null && _maximumDiskBytes > 0) _removed.Add(key);
        return true;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) if (_disposed) return;
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Take the disk gate before advancing the epoch, so a new Set after
            // this point cannot be persisted and then accidentally erased by the
            // same clear operation. The writer holds disk -> memory in this order.
            lock (_gate)
            {
                if (_disposed) return;
                _epoch++;
                _clearEpoch++;
                _memory.Clear(); _lru.Clear(); _memoryCharacters = 0;
                _pending.Clear(); _pendingCharacters = 0; _removed.Clear();
            }
            await InitializeDiskAsync(cancellationToken).ConfigureAwait(false);
            var failures = 0;
            foreach (var key in _disk.Keys.ToArray()) if (!RemoveDisk(key)) failures++;
            if (failures != 0) throw new IOException("部分网页缓存文件正在使用，清理未完成。请稍后重试。");
        }
        finally { _diskGate.Release(); }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_processed >= _enqueued) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushes.Add((_enqueued, completion));
            _signal.Writer.TryWrite(true);
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private bool Queue(string key, string? translation)
    {
        if (_directory is null || _maximumDiskBytes == 0) return false;
        // Large bursts remain bounded; hot entries still render immediately. A
        // repeated key coalesces to its newest verified value/removal.
        _pending.TryGetValue(key, out var old);
        if (old is null && _pending.Count >= MaximumPendingWrites) return false;
        var projected = (long)_pendingCharacters - (old?.Translation?.Length ?? 0) + (translation?.Length ?? 0);
        if (old is null && projected > MaximumPendingCharacters) return false;
        // An existing-key replacement must never leave its obsolete queued value
        // alive. Preserve its newest value by converting other queued writes to
        // removals until it fits; their hot translations remain immediately usable.
        while (projected > MaximumPendingCharacters)
        {
            var victim = _pending.Values.First(item => item.Key != key && item.Translation is not null);
            _pendingCharacters -= victim.Translation!.Length;
            _removed.Add(victim.Key);
            _pending[victim.Key] = new(victim.Key, null, _epoch, ++_enqueued);
            projected -= victim.Translation.Length;
        }
        _pendingCharacters -= old?.Translation?.Length ?? 0;
        _pending[key] = new(key, translation, _epoch, ++_enqueued);
        _pendingCharacters += translation?.Length ?? 0;
        _signal.Writer.TryWrite(true);
        return true;
    }

    private async Task WriteAsync()
    {
        await foreach (var unused in _signal.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Pending[] pending;
            long through;
            lock (_gate) { pending = _pending.Values.ToArray(); through = _enqueued; }
            await _diskGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate) if (_maximumDiskBytes == 0) continue;
                await InitializeDiskAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var item in pending)
                {
                    string? temporary = null;
                    var committed = false;
                    try
                    {
                        lock (_gate)
                            if (item.Epoch != _epoch || _maximumDiskBytes == 0
                                || !_pending.TryGetValue(item.Key, out var newest) || newest.Sequence != item.Sequence) continue;
                        if (item.Translation is null)
                        {
                            if (RemoveDisk(item.Key))
                                lock (_gate)
                                    if (_pending.TryGetValue(item.Key, out var current) && current.Sequence == item.Sequence)
                                        _removed.Remove(item.Key);
                            continue;
                        }
                        var plain = JsonSerializer.SerializeToUtf8Bytes(new Entry(item.Key, item.Translation));
                        byte[] encrypted;
                        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                        long limit;
                        lock (_gate) limit = _maximumDiskBytes;
                        if (encrypted.Length > limit) continue;
                        var path = EntryPath(item.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        if ((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Page cache shard must be a local directory.");
                        temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllBytesAsync(temporary, encrypted).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (item.Epoch != _epoch || _maximumDiskBytes == 0
                                || !_pending.TryGetValue(item.Key, out var newest) || newest.Sequence != item.Sequence) continue;
                            File.Move(temporary, path, overwrite: true);
                        }
                        if (_disk.Remove(item.Key, out var old)) _diskBytes -= old.Length;
                        _disk[item.Key] = new(path, encrypted.Length, DateTime.UtcNow.Ticks);
                        _diskBytes += encrypted.Length;
                        committed = true;
                    }
                    catch (Exception error) when (IsRecoverable(error)) { }
                    finally
                    {
                        if (temporary is not null)
                            try { File.Delete(temporary); } catch (Exception error) when (IsRecoverable(error)) { }
                        lock (_gate)
                            if (_pending.TryGetValue(item.Key, out var current) && current.Sequence == item.Sequence)
                            {
                                if (!committed && item.Translation is not null && item.Epoch == _epoch
                                    && _maximumDiskBytes > 0) _removed.Add(item.Key);
                                _pending.Remove(item.Key); _pendingCharacters -= current.Translation?.Length ?? 0;
                            }
                    }
                }
                TrimDisk();
            }
            catch (Exception error) when (IsRecoverable(error)) { }
            finally
            {
                _diskGate.Release();
                lock (_gate)
                {
                    // Initialization/disabled persistence can skip the whole batch.
                    // Remove only the captured mutations, never a newer replacement.
                    foreach (var item in pending)
                        if (_pending.TryGetValue(item.Key, out var current) && current.Sequence == item.Sequence)
                        {
                            if (item.Translation is not null && item.Epoch == _epoch && _maximumDiskBytes > 0) _removed.Add(item.Key);
                            _pending.Remove(item.Key); _pendingCharacters -= current.Translation?.Length ?? 0;
                        }
                    _processed = Math.Max(_processed, through);
                    foreach (var wait in _flushes.Where(wait => wait.Sequence <= _processed).ToArray())
                    { wait.Completion.TrySetResult(); _flushes.Remove(wait); }
                }
            }
        }
    }

    private async Task InitializeDiskAsync(CancellationToken cancellationToken)
    {
        if (_diskInitialized || _directory is null) return;
        await Task.Run(() => InitializeDisk(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private void InitializeDisk(CancellationToken cancellationToken)
    {
        _disk.Clear(); _diskBytes = 0;
        Directory.CreateDirectory(_directory!);
        if ((File.GetAttributes(_directory!) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Page cache directory must be a local directory.");
        foreach (var directory in Directory.EnumerateDirectories(_directory!))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.Length != 2 || !name.All(Uri.IsHexDigit)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.cache", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Path.GetFileNameWithoutExtension(path);
                if (!IsKey(key) || !key.StartsWith(name, StringComparison.Ordinal)
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaximumTranslationCharacters * 8L) continue;
                _disk[key] = new(path, info.Length, info.LastWriteTimeUtc.Ticks);
                _diskBytes += info.Length;
            }
        }
        _diskInitialized = true;
        TrimDisk();
    }

    private void TrimDisk()
    {
        long limit;
        lock (_gate) limit = _maximumDiskBytes;
        // Disabled persistence preserves existing files for a later re-enable.
        // Only explicit ClearAsync removes them while persistence is disabled.
        if (limit == 0) return;
        if (_diskBytes <= limit && _disk.Count <= MaximumDiskEntries) return;
        foreach (var item in _disk.OrderBy(pair => pair.Value.LastUse).ToArray())
        {
            if (_diskBytes <= limit && _disk.Count <= MaximumDiskEntries) break;
            RemoveDisk(item.Key);
        }
    }

    private bool RemoveDisk(string key)
    {
        if (!_disk.TryGetValue(key, out var old)) return true;
        try { File.Delete(old.Path); }
        catch (Exception error) when (IsRecoverable(error)) { return false; }
        _disk.Remove(key); _diskBytes -= old.Length;
        return true;
    }

    private string EntryPath(string key) => Path.Combine(_directory!, key[..2], key + ".cache");

    private void SetMemory(Entry entry)
    {
        if (_maximumMemoryEntries == 0 || entry.Translation.Length > _maximumMemoryCharacters) return;
        RemoveMemory(entry.Key);
        _memory.Add(entry.Key, _lru.AddFirst(entry));
        _memoryCharacters += entry.Translation.Length;
        while (_memory.Count > _maximumMemoryEntries || _memoryCharacters > _maximumMemoryCharacters)
            RemoveMemory(_lru.Last!.Value.Key);
    }

    private void RemoveMemory(string key)
    {
        if (_memory.Remove(key, out var old)) { _lru.Remove(old); _memoryCharacters -= old.Value.Translation.Length; }
    }

    private void MarkReadersChanged(string key)
    { if (_reads.TryGetValue(key, out var readers)) foreach (var read in readers) read.Changed = true; }

    // Called under _gate. Mutation leases exist only for admitted cold reads;
    // no unbounded history of source hashes is needed after those reads finish.
    private bool ReadNewest(ReadLease read, Dictionary<string, string> result)
    {
        if (_memory.TryGetValue(read.Key, out var latest))
        {
            _lru.Remove(latest); _lru.AddFirst(latest);
            result.Add(read.Key, latest.Value.Translation); return true;
        }
        if (_removed.Contains(read.Key)) return true;
        if (_pending.TryGetValue(read.Key, out var current))
        { if (current.Translation is { } newest) result.Add(read.Key, newest); return true; }
        return read.Changed;
    }

    private static bool ValidTranslation(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumTranslationCharacters;
    private static bool IsKey(string? key) => key is { Length: 64 } && key.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static void ValidateKey(string key)
    { if (!IsKey(key)) throw new ArgumentException("Cache key must be an uppercase SHA-256 digest.", nameof(key)); }
    private static void ValidateDiskLimit(long limit)
    { if (limit is < 0 or > MaximumDiskBytes) throw new ArgumentOutOfRangeException(nameof(limit)); }
    private static bool IsRecoverable(Exception error) => error is IOException or UnauthorizedAccessException
        or CryptographicException or JsonException or NotSupportedException;

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _signal.Writer.TryComplete();
        }
        await _writer.ConfigureAwait(false);
        // Do not dispose the semaphore while an already-admitted asynchronous
        // cold read/clear may still release it. It has no unmanaged wait handle.
    }

    private sealed record Entry(string Key, string Translation);
    private sealed record Pending(string Key, string? Translation, long Epoch, long Sequence);
    private sealed record DiskEntry(string Path, long Length, long LastUse);
    private sealed class ReadLease(string key)
    { public string Key { get; } = key; public bool Changed { get; set; } }
}
