using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using ScreenTranslator.App.Services.Browser;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserBridgeFramingTests
{
    [Fact]
    public async Task CancellationAfterLengthPrefixCannotTruncateTheFrameOrCorruptTheNextMessage()
    {
        var pipe = "ScreenTranslator.Framing.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new BrowserBridgeServer(pipe);
        var opened = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionOpened += (_, args) => opened.TrySetResult(args.ConnectionId);
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(deadline.Token);
        var connection = await opened.Task.WaitAsync(deadline.Token);
        using var caller = new CancellationTokenSource();
        var payload = "{\"data\":\"" + new string('x', 900_000) + "\"}";
        var sending = server.SendAsync(connection, payload, caller.Token);
        var prefix = new byte[4];
        await client.ReadExactlyAsync(prefix, deadline.Token);
        // This is a protocol boundary, not a timing guess: a frame has begun,
        // while its large body still requires the receiving peer to drain it.
        caller.Cancel();
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
        await client.ReadExactlyAsync(body, deadline.Token);
        await sending;
        Assert.Equal(payload, Encoding.UTF8.GetString(body));

        var nextRead = NativeMessagingHost.ReadAsync(client, deadline.Token);
        await server.SendAsync(connection, "{\"type\":\"after-cancellation\"}", deadline.Token);
        Assert.Equal("{\"type\":\"after-cancellation\"}", await nextRead);
    }
}
