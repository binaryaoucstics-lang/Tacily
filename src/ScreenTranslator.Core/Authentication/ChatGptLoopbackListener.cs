using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenTranslator.Core.Authentication;

/// <summary>Owns an ephemeral IPv4 loopback port before the browser opens. No HTTP URL reservation is needed.</summary>
internal sealed class ChatGptLoopbackListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    internal Uri RedirectUri { get; }
    internal ChatGptLoopbackListener()
    {
        _listener.Start(4);
        RedirectUri = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/auth/callback");
    }

    internal async Task<Dictionary<string, string>> ReceiveAsync(CancellationToken token)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            connection.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            try
            {
                var first = await reader.ReadLineAsync(connection.Token).ConfigureAwait(false);
                var fields = first?.Split(' ');
                var headerBytes = first?.Length ?? 0;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(connection.Token).ConfigureAwait(false)))
                {
                    if ((headerBytes += header.Length) > 32 * 1024) throw new IOException("Headers too large.");
                }
                if (fields is not { Length: 3 } || fields[0] != "GET" || fields[1].Length > 16 * 1024
                    || !fields[1].StartsWith("/auth/callback?", StringComparison.Ordinal))
                { await ReplyAsync(stream, "404 Not Found", "请回到翻译器继续登录。", connection.Token).ConfigureAwait(false); continue; }
                var callback = new Uri(RedirectUri, fields[1]);
                if (callback.AbsolutePath != RedirectUri.AbsolutePath || callback.Host != "127.0.0.1" || callback.Port != RedirectUri.Port)
                    throw new ChatGptException("登录回调地址无效，请重试。", "invalid_callback");
                var parameters = ParseQuery(callback.Query);
                await ReplyAsync(stream, "200 OK", "登录授权已返回，请回到翻译器查看结果。", connection.Token).ConfigureAwait(false);
                return parameters;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* A stalled local connection does not consume the attempt. */ }
            catch (IOException) when (!token.IsCancellationRequested) { }
        }
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!values.TryAdd(key, value)) throw new ChatGptException("登录回调包含重复参数，请重新登录。", "invalid_callback");
        }
        return values;
    }

    private static async Task ReplyAsync(Stream stream, string status, string message, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>ChatGPT 登录</title><p>" + message + "</p></html>");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}
