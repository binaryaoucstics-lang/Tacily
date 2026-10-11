using System.Buffers.Binary;
using System.Text;
using System.Diagnostics;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.IntegrationTests.TestInfrastructure;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class NativeMessagingHostTests
{
    [Fact]
    public async Task ReadAsync_Reads_Little_Endian_Length_Prefixed_Json()
    {
        const string json = """{"type":"hello","browser":"chrome"}""";
        await using var stream = CreateMessageStream(json);

        var result = await NativeMessagingHost.ReadAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task ReadAsync_Returns_Null_For_Clean_End_Of_Stream()
    {
        await using var stream = new MemoryStream();

        var result = await NativeMessagingHost.ReadAsync(
            stream,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadAsync_Rejects_Incomplete_Length_Prefix()
    {
        await using var stream = new MemoryStream([0x01, 0x02]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => NativeMessagingHost.ReadAsync(
                stream,
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_Rejects_Messages_Over_One_Mebibyte()
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            NativeMessagingHost.MaximumMessageBytes + 1);
        await using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => NativeMessagingHost.ReadAsync(
                stream,
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_Uses_Expected_Native_Message_Frame()
    {
        const string json = """{"type":"pong"}""";
        await using var stream = new MemoryStream();

        await NativeMessagingHost.WriteAsync(
            stream,
            json,
            CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(
            Encoding.UTF8.GetByteCount(json),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(json, Encoding.UTF8.GetString(bytes.AsSpan(4)));
    }

    [Theory]
    [InlineData("chrome-extension://plpgmkbadcfnkmolbeecggbbopilajed/", true)]
    [InlineData("--parent-window=123", false)]
    [InlineData("", false)]
    public void IsBrowserInvocation_Recognizes_Extension_Origin(
        string argument,
        bool expected)
    {
        Assert.Equal(
            expected,
            NativeMessagingHost.IsBrowserInvocation([argument]));
    }

    [Fact]
    public async Task Bridge_RoundTrips_Messages_Between_Host_And_Desktop()
    {
        var pipeName = "ScreenTranslator.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        var received = new TaskCompletionSource<BrowserBridgeMessageEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, args) => received.TrySetResult(args);

        await using var client = new BrowserBridgeClient(pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync("""{"type":"hello"}""", timeout.Token);

        var message = await received.Task.WaitAsync(timeout.Token);
        Assert.Equal("""{"type":"hello"}""", message.Json);

        var readTask = client.ReadAsync(timeout.Token);
        await server.SendAsync(
            message.ConnectionId,
            """{"type":"ready"}""",
            timeout.Token);
        Assert.Equal(
            """{"type":"ready"}""",
            await readTask);
    }

    [Fact]
    public void Bridge_Dispose_Does_Not_Deadlock_A_Shutting_Down_Ui_Thread()
    {
        NonPumpingContextTest.Run(
            () =>
            {
                var server = new BrowserBridgeServer("ScreenTranslator.Tests." + Guid.NewGuid().ToString("N"));
                server.Start();
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            },
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RunAsync_Exits_After_Desktop_Eof_When_Console_Read_Ignores_Cancellation()
    {
        var pipeName = "ScreenTranslator.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        using var input = new NonCancellingInputStream();
        await using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var host = NativeMessagingHost.RunAsync(input, output, timeout.Token, pipeName);
        await input.ReadStarted.Task.WaitAsync(timeout.Token);
        var clock = Stopwatch.StartNew();
        await server.DisposeAsync();
        await host.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange(NativeMessagingHost.ShutdownGraceMilliseconds, 1, 1000);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(input.PendingRead.Task.IsCompleted); // It really ignored cancellation.
        input.PendingRead.TrySetResult(0);
    }

    [Fact]
    public async Task Forwarder_Shutdown_Preserves_An_Already_Observed_Protocol_Failure()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidDataException("Invalid native frame.");
        var result = NativeMessagingHost.CompleteForwardersAsync(Task.FromException(failure), pending.Task,
            cancellation, TimeSpan.FromMilliseconds(20));
        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => result.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(failure, thrown);
        pending.TrySetCanceled();
    }

    [Fact]
    public async Task Forwarder_Shutdown_Observes_A_Deferred_Read_Fault()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await NativeMessagingHost.CompleteForwardersAsync(Task.CompletedTask, pending.Task, cancellation,
            TimeSpan.FromMilliseconds(20), error => observed.TrySetResult(error)).WaitAsync(TimeSpan.FromSeconds(1));
        var failure = new IOException("Deferred console read failed.");
        pending.TrySetException(failure);
        Assert.Same(failure, await observed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Forwarder_Shutdown_Is_Bounded_On_Caller_Cancel_Even_If_Both_Reads_Ignore_It()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await NativeMessagingHost.CompleteForwardersAsync(first.Task, second.Task, cancellation,
            TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(first.Task.IsCompleted);
        Assert.False(second.Task.IsCompleted);
        first.TrySetCanceled(); second.TrySetCanceled();
    }

    private sealed class NonCancellingInputStream : Stream
    {
        internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<int> PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadStarted.TrySetResult(); return new ValueTask<int>(PendingRead.Task); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static MemoryStream CreateMessageStream(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }
}
