using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace WinWhatsApp.App.Calls;

/// <summary>
/// Serves the calling engine's page to its browser over HTTP on the loopback
/// address, with the headers that isolate the page so its threads can share
/// memory. WebView2 could serve the files itself by intercepting requests,
/// but the engine's threads then hang loading their scripts.
/// </summary>
/// <remarks>
/// Where the page runs in a browser that has no web messages, which is
/// everywhere but in WebView2, the server also carries the messages between
/// the app and the page, over a WebSocket. Other programs on the computer can
/// reach the server too, so the page has to show a key to open it: the one
/// the app put into the page's address.
/// </remarks>
internal sealed class PageServer : IDisposable
{
    private const string ChannelPath = "channel";
    private const int MaxMessage = 16 * 1024 * 1024;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string?> _resolve;
    private readonly string? _channelKey;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private int _channelTaken;

    /// <param name="resolve">The file for a path of the page, or null when there is none.</param>
    /// <param name="channelKey">What the page has to show to exchange messages with the app here; null where it does that another way.</param>
    public PageServer(Func<string, string?> resolve, string? channelKey = null)
    {
        _resolve = resolve;
        _channelKey = channelKey;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Origin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>A message of the page, as JSON. Raised on a background thread.</summary>
    public event Action<string>? MessageReceived;

    /// <summary>The page closed its end, or went away. Raised on a background thread.</summary>
    public event Action? ChannelClosed;

    /// <summary>Sends a message to the page, after those sent before it.</summary>
    public void Send(string json) => _outgoing.Writer.TryWrite(json);

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
                Request? request = await ReadRequestAsync(stream);
                if (request is { Path: ChannelPath } && _channelKey is not null)
                {
                    await ServeChannelAsync(stream, request);
                    return;
                }
                string? file = request is null ? null : _resolve(request.Path);
                if (file is null || !File.Exists(file))
                {
                    await WriteHeadAsync(stream, "404 Not Found", "text/plain", 0);
                    return;
                }
                await using FileStream content = File.OpenRead(file);
                await WriteHeadAsync(stream, "200 OK", ContentType(file), content.Length);
                await content.CopyToAsync(stream, _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
            {
                // The page went away.
            }
        }
    }

    /// <summary>What the server reads of a request: a GET, its path without the leading slash, its query and its headers.</summary>
    private sealed record Request(string Path, string Query, Dictionary<string, string> Headers);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream)
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
        string[] lines = head.ToString()[..head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2 || requestLine[0] != "GET")
        {
            return null;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
        string path = requestLine[1];
        int query = path.IndexOf('?');
        return new Request(
            Uri.UnescapeDataString(query >= 0 ? path[..query] : path).TrimStart('/'),
            query >= 0 ? path[(query + 1)..] : "",
            headers);
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

    /// <summary>
    /// Opens the WebSocket for the page and carries messages until one side
    /// closes it. Only the app's own page gets it: it comes from this server,
    /// knows the key, and is the first to ask.
    /// </summary>
    private async Task ServeChannelAsync(NetworkStream stream, Request request)
    {
        bool allowed = request.Headers.TryGetValue("Upgrade", out string? upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && request.Headers.TryGetValue("Sec-WebSocket-Key", out _)
            && request.Headers.TryGetValue("Origin", out string? origin) && origin == Origin
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Query), Encoding.ASCII.GetBytes("key=" + _channelKey));
        if (!allowed || Interlocked.Exchange(ref _channelTaken, 1) != 0)
        {
            await WriteHeadAsync(stream, "403 Forbidden", "text/plain", 0);
            return;
        }

        // The answer a WebSocket asks for: the hash of its key and this fixed text.
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.Headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));

        using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Task sending = SendLoopAsync(socket, closed.Token);
        try
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                if (part.MessageType == WebSocketMessageType.Close || message.Length + part.Count > MaxMessage)
                {
                    break;
                }
                message.Write(buffer, 0, part.Count);
                if (part.EndOfMessage)
                {
                    if (part.MessageType == WebSocketMessageType.Text)
                    {
                        MessageReceived?.Invoke(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                    }
                    message.SetLength(0);
                }
            }
        }
        finally
        {
            closed.Cancel();
            try
            {
                await sending;
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // It was told to stop.
            }
            if (!_stop.IsCancellationRequested)
            {
                ChannelClosed?.Invoke();
            }
        }
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken stop)
    {
        await foreach (string json in _outgoing.Reader.ReadAllAsync(stop))
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, stop);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
