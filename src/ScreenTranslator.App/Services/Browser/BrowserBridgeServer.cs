using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace ScreenTranslator.App.Services.Browser;

public sealed class BrowserBridgeMessageEventArgs(
    Guid connectionId,
    string json) : EventArgs
{
    public Guid ConnectionId { get; } = connectionId;

    public string Json { get; } = json;
}

public sealed class BrowserBridgeConnectionEventArgs(Guid connectionId) : EventArgs
{
    public Guid ConnectionId { get; } = connectionId;
}

public sealed class BrowserBridgeServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private readonly string _pipeName;

    public BrowserBridgeServer(string? pipeName = null) => _pipeName = pipeName ?? GetPipeName();

    public event EventHandler<BrowserBridgeMessageEventArgs>? MessageReceived;

    public event EventHandler<BrowserBridgeConnectionEventArgs>? ConnectionOpened;

    public event EventHandler<BrowserBridgeConnectionEventArgs>? ConnectionClosed;

    public IReadOnlyCollection<Guid> Connections => _connections.Keys.ToArray();

    public static string GetPipeName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? Environment.UserName;
        var safeSid = string.Concat(sid.Select(character =>
            char.IsLetterOrDigit(character) ? character : '_'));
        return $"ScreenTranslator.BrowserBridge.{safeSid}";
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        _acceptLoop ??= Task.Run(
            () => AcceptLoopAsync(_shutdown.Token),
            CancellationToken.None);
    }

    public async Task SendAsync(
        Guid connectionId,
        string json,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            throw new InvalidOperationException("浏览器连接已断开。");
        }

        await connection.SendAsync(json, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during application shutdown.
            }
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var connection = new Connection(pipe);
            _connections[connection.Id] = connection;
            ConnectionOpened?.Invoke(
                this,
                new BrowserBridgeConnectionEventArgs(connection.Id));
            _ = ReadConnectionAsync(connection, cancellationToken);
        }
    }

    private async Task ReadConnectionAsync(
        Connection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var json = await NativeMessagingHost.ReadAsync(
                    connection.Stream,
                    cancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    return;
                }

                MessageReceived?.Invoke(
                    this,
                    new BrowserBridgeMessageEventArgs(connection.Id, json));
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or EndOfStreamException
                or OperationCanceledException
                or ObjectDisposedException)
        {
            // A browser closing its native port is an expected disconnect.
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
            ConnectionClosed?.Invoke(
                this,
                new BrowserBridgeConnectionEventArgs(connection.Id));
        }
    }

    private sealed class Connection(NamedPipeServerStream stream) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;

        public Guid Id { get; } = Guid.NewGuid();

        public NamedPipeServerStream Stream { get; } = stream;

        public async Task SendAsync(string json, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var frameStarted = false;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                cancellationToken.ThrowIfCancellationRequested();
                frameStarted = true;
                // A cancelled caller may stop waiting for an ACK, but must not
                // truncate the length-prefixed frame on a shared connection.
                using var frameDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await NativeMessagingHost.WriteAsync(
                    Stream,
                    json,
                    frameDeadline.Token).ConfigureAwait(false);
            }
            catch when (frameStarted)
            {
                // A failed/expired frame cannot safely be followed by another
                // frame; closing also wakes the connection's reader/owner.
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // An in-flight sender still releases this gate in its finally.
            // Do not dispose its managed semaphore until that sender is gone.
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
