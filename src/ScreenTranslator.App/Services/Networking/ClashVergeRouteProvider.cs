using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Networking;

/// <summary>Read-only integration with the user's core. Selected nodes run in isolated child cores.</summary>
public sealed class ClashVergeRouteProvider : IClashRouteProvider
{
    private readonly Func<ClashVergeInstallation> _discover;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, NodeWorker> _workers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private ClashSnapshot? _snapshot;
    private DateTime _snapshotTime;
    private int _disposed;
    public ClashVergeRouteProvider(Func<ClashVergeInstallation>? discover = null) => _discover = discover ?? ClashVergeInstallation.Discover;

    public async Task<IReadOnlyList<string>> ReadNodesAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return (await ReadSnapshotAsync(linked.Token, force: true).ConfigureAwait(false)).Nodes; }
        finally { _gate.Release(); }
    }

    public async Task<ClashProxyEndpoint> GetEndpointAsync(string? node, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var snapshot = await ReadSnapshotAsync(linked.Token).ConfigureAwait(false);
            if (node is null) return snapshot.DefaultEndpoint();
            var key = snapshot.Installation.ConfigurationPath + "\n" + snapshot.Stamp + ":" + snapshot.Length + "\n" + node;
            if (_workers.TryGetValue(key, out var worker) && worker.IsAlive) return worker.Endpoint;
            worker?.Dispose(); _workers.Remove(key);
            // Keep busy routes intact on preset changes; retire unused workers after a quiet interval.
            foreach (var entry in _workers.Where(w => !w.Value.IsAlive || DateTime.UtcNow - w.Value.LastUsed > TimeSpan.FromMinutes(10)).ToArray())
            { entry.Value.Dispose(); _workers.Remove(entry.Key); }
            if (_workers.Count >= 16) throw new TranslationConfigurationException("独立 Clash 通道过多，请稍后重试或重启翻译软件。");
            var created = await NodeWorker.StartAsync(snapshot, node, linked.Token).ConfigureAwait(false);
            _workers.Add(key, created); return created.Endpoint;
        }
        finally { _gate.Release(); }
    }

    private async Task<ClashSnapshot> ReadSnapshotAsync(CancellationToken token, bool force = false)
    {
        if (!force && _snapshot is { } cached && DateTime.UtcNow - _snapshotTime < TimeSpan.FromSeconds(3)) return cached;
        var installation = _discover();
        var info = new FileInfo(installation.ConfigurationPath);
        if (!force && _snapshot is { } previous && previous.Installation.ConfigurationPath == installation.ConfigurationPath
            && info.LastWriteTimeUtc.Ticks == previous.Stamp && info.Length == previous.Length)
        { _snapshotTime = DateTime.UtcNow; return previous; }
        _snapshot = await installation.ReadAsync(token).ConfigureAwait(false); _snapshotTime = DateTime.UtcNow; return _snapshot;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        // StartAsync observes shutdown before publishing a worker; wait for ownership to settle.
        _gate.Wait();
        try { foreach (var worker in _workers.Values) worker.Dispose(); _workers.Clear(); }
        finally { _gate.Release(); }
        _shutdown.Dispose();
    }

    private sealed class NodeWorker : IDisposable
    {
        private readonly Process _process;
        private readonly ProcessJob _job;
        private readonly string _directory;
        private readonly ClashProxyEndpoint _endpoint;
        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;
        public bool IsAlive { get { try { return !_process.HasExited; } catch (InvalidOperationException) { return false; } } }
        public ClashProxyEndpoint Endpoint { get { LastUsed = DateTime.UtcNow; return _endpoint; } }
        private NodeWorker(Process process, ProcessJob job, string directory, ClashProxyEndpoint endpoint)
        { _process = process; _job = job; _directory = directory; _endpoint = endpoint; }

        public static async Task<NodeWorker> StartAsync(ClashSnapshot snapshot, string node, CancellationToken token)
        {
            // -f - is Mihomo's stdin configuration input; passwords never enter files or command lines.
            var id = Guid.NewGuid().ToString("N"); var pipe = @"\\.\pipe\translator-api-" + id;
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var config = ClashNodeConfiguration.Build(snapshot, node, port, pipe, secret, password);
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAITranslator", "NetworkWorkers", id);
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo(snapshot.Installation.CorePath)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add("-d"); start.ArgumentList.Add(directory);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add("-");
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("CLASH_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            Process? process = null; ProcessJob? job = null; NodeWorker? worker = null;
            try
            {
                job = new(); process = Process.Start(start) ?? throw new IOException(); job.Attach(process);
                _ = DrainAsync(process.StandardOutput); _ = DrainAsync(process.StandardError);
                worker = new(process, job, directory, new(new Uri($"http://127.0.0.1:{port}"), new("translator", password)));
                await process.StandardInput.WriteAsync(config.AsMemory(), token).ConfigureAwait(false);
                process.StandardInput.Close();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (!deadline.IsCancellationRequested)
                {
                    if (process.HasExited) throw new TranslationConfigurationException("Clash 独立节点启动失败，请确认所选节点在 Clash Verge 中可用。");
                    try
                    {
                        using var client = ClashVergeInstallation.Controller(pipe, "", secret);
                        using var response = await client.GetAsync("version", deadline.Token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            // Verify the owned proxy port as well as the authenticated controller.
                            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
                            return worker;
                        }
                    }
                    catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException) { }
                    await Task.Delay(75, deadline.Token).ConfigureAwait(false);
                }
                deadline.Token.ThrowIfCancellationRequested(); throw new IOException();
            }
            catch (OperationCanceledException) { worker?.Dispose(); if (worker is null) { job?.Dispose(); process?.Dispose(); } throw; }
            catch (TranslationConfigurationException) { worker?.Dispose(); if (worker is null) { job?.Dispose(); process?.Dispose(); } throw; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                worker?.Dispose(); if (worker is null) { job?.Dispose(); process?.Dispose(); }
                throw new TranslationConfigurationException("无法启动 Clash 独立节点通道，请确认 Clash Verge 已安装并启动。");
            }
        }
        private static async Task DrainAsync(StreamReader reader)
        {
            try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        public void Dispose()
        {
            _job.Dispose(); // Kill-on-close also applies when the parent crashes.
            try { if (!_process.HasExited) _process.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
            try
            {
                var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalAITranslator", "NetworkWorkers")) + Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(_directory);
                if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(target), "N", out _)) Directory.Delete(target, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
