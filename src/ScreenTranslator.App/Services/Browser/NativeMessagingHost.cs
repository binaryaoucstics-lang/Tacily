using System.Buffers.Binary;
using System.IO;
using System.Text;
using ScreenTranslator.App.Infrastructure;

namespace ScreenTranslator.App.Services.Browser;

public static class NativeMessagingHost
{
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int ShutdownGraceMilliseconds = 750;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool IsBrowserInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 &&
        Uri.TryCreate(arguments[0], UriKind.Absolute, out var origin) &&
        origin.Scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase);

    public static async Task RunAsync(
        Stream browserInput,
        Stream browserOutput,
        CancellationToken cancellationToken,string? pipeName=null)
    {
        await using var bridge = new BrowserBridgeClient(pipeName);
        await bridge.ConnectAsync(cancellationToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var browserToDesktop = ForwardBrowserToDesktopAsync(
            browserInput,
            bridge,
            linkedCancellation.Token);
        var desktopToBrowser = ForwardDesktopToBrowserAsync(
            bridge,
            browserOutput,
            linkedCancellation.Token);

        await CompleteForwardersAsync(browserToDesktop, desktopToBrowser, linkedCancellation);
    }

    // Console stdin on Windows may keep its ReadAsync pending after cancellation.
    // Let Main exit after a bounded grace period so Chrome sees native-port EOF and
    // reconnects immediately. The deferred read remains fault-observed until exit.
    internal static async Task CompleteForwardersAsync(
        Task browserToDesktop,
        Task desktopToBrowser,
        CancellationTokenSource linkedCancellation,
        TimeSpan? shutdownGrace = null,
        Action<Exception>? backgroundFaultObserver = null)
    {
        var grace = shutdownGrace ?? TimeSpan.FromMilliseconds(ShutdownGraceMilliseconds);
        if (grace <= TimeSpan.Zero || grace > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(shutdownGrace));
        try
        {
            await Task.WhenAny(browserToDesktop, desktopToBrowser).WaitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            // Preserve normal native-host cancellation shutdown, even if both
            // underlying streams ignore their cancellation tokens.
        }
        finally { linkedCancellation.Cancel(); }

        ObserveFault(browserToDesktop, backgroundFaultObserver);
        ObserveFault(desktopToBrowser, backgroundFaultObserver);
        var completion = Task.WhenAll(browserToDesktop, desktopToBrowser);
        ObserveFault(completion, null, log: false);

        try
        {
            await completion.WaitAsync(grace);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            // The other side closing is the normal native-messaging shutdown path.
        }
        catch (TimeoutException)
        {
            if (completion.IsCompleted)
            {
                try { await completion; }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested) { }
                return;
            }
            // A protocol/IO failure already observed on either forwarder must still
            // fail RunAsync, even if the other side is stuck in Console.ReadAsync.
            foreach (var forwarder in new[] { browserToDesktop, desktopToBrowser })
                if (forwarder.IsFaulted) await forwarder;
        }
    }

    private static void ObserveFault(Task task, Action<Exception>? observer, bool log = true)
    {
        _ = task.ContinueWith(completed =>
        {
            var aggregate = completed.Exception!; // Observes even faults that arrive after RunAsync returned.
            var error = aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? aggregate;
            try
            {
                observer?.Invoke(error);
                if (log) AppDiagnostics.Write($"native_host_forwarder_fault type={error.GetType().Name}");
            }
            catch { /* A diagnostic callback must not create another unobserved task fault. */ }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public static async Task<string?> ReadAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        var prefixRead = await ReadAtMostAsync(input, prefix, cancellationToken);
        if (prefixRead == 0)
        {
            return null;
        }

        if (prefixRead != prefix.Length)
        {
            throw new EndOfStreamException("原生消息长度前缀不完整。");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("原生消息长度无效。");
        }

        var payload = new byte[length];
        await input.ReadExactlyAsync(payload, cancellationToken);
        return StrictUtf8.GetString(payload);
    }

    public static async Task WriteAsync(
        Stream output,
        string json,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var payload = StrictUtf8.GetBytes(json);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("原生消息超过 1 MiB 上限。");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await output.WriteAsync(prefix, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task ForwardBrowserToDesktopAsync(
        Stream browserInput,
        BrowserBridgeClient bridge,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadAsync(browserInput, cancellationToken);
            if (message is null)
            {
                return;
            }

            await bridge.WriteAsync(message, cancellationToken);
        }
    }

    private static async Task ForwardDesktopToBrowserAsync(
        BrowserBridgeClient bridge,
        Stream browserOutput,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await bridge.ReadAsync(cancellationToken);
            if (message is null)
            {
                return;
            }

            await WriteAsync(browserOutput, message, cancellationToken);
        }
    }

    private static async Task<int> ReadAtMostAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
