using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinWhatsApp.App.Calls;

/// <summary>
/// Serves the calling engine's page to its WebView2 over HTTP on the loopback
/// address, with the headers that isolate the page so its threads can share
/// memory. WebView2 could serve the files itself by intercepting requests,
/// but the engine's threads then hang loading their scripts.
/// </summary>
internal sealed class PageServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string?> _resolve;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="resolve">The file for a path of the page, or null when there is none.</param>
    public PageServer(Func<string, string?> resolve)
    {
        _resolve = resolve;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Origin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string? path = await ReadRequestPathAsync(stream);
                string? file = path is null ? null : _resolve(path);
                if (file is null || !File.Exists(file))
                {
                    await WriteHeadAsync(stream, "404 Not Found", "text/plain", 0);
                    return;
                }
                await using FileStream content = File.OpenRead(file);
                await WriteHeadAsync(stream, "200 OK", ContentType(file), content.Length);
                await content.CopyToAsync(stream, _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The page went away.
            }
        }
    }

    /// <summary>The path of a GET request, without the leading slash and the query.</summary>
    private static async Task<string?> ReadRequestPathAsync(NetworkStream stream)
    {
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0 || head.Length > 64 * 1024)
            {
                return null;
            }
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        string[] requestLine = head.ToString()[..head.ToString().IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
        if (requestLine.Length < 2 || requestLine[0] != "GET")
        {
            return null;
        }
        string path = requestLine[1];
        int query = path.IndexOf('?');
        return Uri.UnescapeDataString(query >= 0 ? path[..query] : path).TrimStart('/');
    }

    private static async Task WriteHeadAsync(NetworkStream stream, string status, string contentType, long length)
    {
        string head =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {length}\r\n" +
            // These three isolate the page, so its threads can share memory.
            "Cross-Origin-Opener-Policy: same-origin\r\n" +
            "Cross-Origin-Embedder-Policy: require-corp\r\n" +
            "Cross-Origin-Resource-Policy: same-origin\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".wasm" => "application/wasm",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
