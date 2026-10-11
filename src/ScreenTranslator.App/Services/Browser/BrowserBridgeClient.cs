using System.IO.Pipes;
using System.Security.Principal;

namespace ScreenTranslator.App.Services.Browser;

public sealed class BrowserBridgeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    public BrowserBridgeClient(string? pipeName = null)
    {
        _pipe = new NamedPipeClientStream(
            ".",
            pipeName ?? BrowserBridgeServer.GetPipeName(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await _pipe.ConnectAsync(timeout.Token);
        _pipe.ReadMode = PipeTransmissionMode.Byte;
    }

    public Task<string?> ReadAsync(CancellationToken cancellationToken) =>
        NativeMessagingHost.ReadAsync(_pipe, cancellationToken);

    public Task WriteAsync(string json, CancellationToken cancellationToken) =>
        NativeMessagingHost.WriteAsync(_pipe, json, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
    }
}
