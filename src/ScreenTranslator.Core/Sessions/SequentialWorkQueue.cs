using System.Threading.Channels;

namespace ScreenTranslator.Core.Sessions;

public sealed class WorkItemFailedEventArgs<T>(
    T item,
    Exception exception) : EventArgs
{
    public T Item { get; } = item;

    public Exception Exception { get; } = exception;
}

public sealed class SequentialWorkQueue<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel;
    private readonly Func<T, CancellationToken, Task> _processor;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _slots;
    private readonly Task _consumer;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _pendingCount;
    private int _completed;

    public SequentialWorkQueue(
        int capacity,
        Func<T, CancellationToken, Task> processor)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Queue capacity must be positive.");
        }

        ArgumentNullException.ThrowIfNull(processor);

        Capacity = capacity;
        _processor = processor;
        _slots = new SemaphoreSlim(capacity, capacity);
        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        _consumer = ConsumeAsync();
    }

    public event EventHandler<int>? PendingCountChanged;

    public event EventHandler<WorkItemFailedEventArgs<T>>? ItemFailed;

    public int Capacity { get; }

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public async ValueTask<int> EnqueueAsync(
        T item,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _completed) != 0,
            this);

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellation.Token);
        await _slots.WaitAsync(linkedCancellation.Token);
        var pending = Interlocked.Increment(ref _pendingCount);
        PendingCountChanged?.Invoke(this, pending);
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken);
            return pending;
        }
        catch
        {
            pending = Interlocked.Decrement(ref _pendingCount);
            PendingCountChanged?.Invoke(this, pending);
            _slots.Release();
            throw;
        }
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        await _consumer;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        await _cancellation.CancelAsync();
        try
        {
            await _consumer;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(
                           _cancellation.Token))
        {
            try
            {
                await _processor(item, _cancellation.Token);
            }
            catch (OperationCanceledException)
                when (_cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ItemFailed?.Invoke(
                    this,
                    new WorkItemFailedEventArgs<T>(item, exception));
            }
            finally
            {
                var pending = Interlocked.Decrement(ref _pendingCount);
                PendingCountChanged?.Invoke(this, pending);
                _slots.Release();
            }
        }
    }
}
