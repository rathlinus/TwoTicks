using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using TwoTicks.Core;

namespace TwoTicks.App.Calls;

/// <summary>
/// WhatsApp's calling engine, the one WhatsApp Web uses: a WebAssembly program
/// with the page around it in Assets\Voip, run in a browser that is never
/// shown: WebView2 on Windows, a browser of the system on macOS and Linux.
/// Messages go to the page as JSON and come back the same way; see host.js.
/// </summary>
/// <remarks>
/// The engine runs on threads that share memory, which a page may only do when
/// it is isolated from other sites; <see cref="PageServer"/> serves the page with
/// the headers that make it so. The engine's binary is WhatsApp's
/// and not shipped with the app: it is downloaded from WhatsApp the first
/// time and checked against the hash its script was built for.
/// </remarks>
internal sealed partial class VoipEngine : IDisposable
{
    private const string WasmPath = "wa-voip.wasm";

    private static readonly string s_assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Voip");

    private readonly DispatcherQueue _ui;
    private PageServer? _server;
    private TaskCompletionSource? _loaded;
    private string? _wasmFile;
    private bool _disposed;

    public VoipEngine(DispatcherQueue ui) => _ui = ui;

    /// <summary>A message from the page. Raised on the UI thread.</summary>
    public event Action<JsonElement>? MessageReceived;

    /// <summary>Whether the page is open.</summary>
    public partial bool IsRunning { get; }

    public void Post(JsonObject message) => PostToPage(message.ToJsonString());

    /// <summary>
    /// Opens the page where it runs on this system and waits until it loaded.
    /// The page's messages go to <see cref="OnMessage(string)"/>.
    /// </summary>
    private partial Task OpenPageAsync();

    private partial void PostToPage(string json);

    /// <summary>Closes the page and what it ran in.</summary>
    private partial void ClosePage();

    /// <summary>Serves the page and waits for it to say that it loaded.</summary>
    /// <param name="channelKey">
    /// For a browser without web messages: the key that lets the page exchange
    /// its messages with the app through the server; see <see cref="PageServer"/>.
    /// </param>
    private async Task ServeAndWaitAsync(Action<string> navigate, string? channelKey = null)
    {
        _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server = new PageServer(path => path == WasmPath ? _wasmFile : SafeAsset(path), channelKey);
        _server.MessageReceived += OnChannelMessage;
        _server.ChannelClosed += OnChannelClosed;
        navigate(_server.Origin + "/host.html" + (channelKey is null ? "" : "?key=" + channelKey));
        Task finished = await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (finished != _loaded.Task)
        {
            throw new TimeoutException("The calling engine's page did not load.");
        }
    }

    private void OnChannelMessage(string json) => _ui.TryEnqueue(() => OnMessage(json));

    private void OnChannelClosed() => OnPageFailed("the page closed");

    /// <summary>The page or what it runs in stopped by itself.</summary>
    private void OnPageFailed(string reason)
    {
        Log.Error($"The calling engine's browser process failed: {reason}");
        var failed = JsonDocument.Parse("""{"type":"failed","message":"The calling engine stopped."}""").RootElement.Clone();
        _ui.TryEnqueue(() => MessageReceived?.Invoke(failed));
    }

    /// <summary>Opens the page and starts the engine in it, downloading the engine first when needed.</summary>
    public async Task StartAsync(CallIdentityData me, string? countryCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _wasmFile ??= await EnsureWasmAsync();
        if (!IsRunning)
        {
            await OpenPageAsync();
        }
        var init = new JsonObject
        {
            ["type"] = "init",
            ["pn"] = me.Pn,
            ["pnUser"] = me.PnUser,
            ["lid"] = me.Lid,
            ["countryCode"] = countryCode ?? "",
            ["debug"] = Environment.GetEnvironmentVariable("TWOTICKS_DEBUG") == "1",
        };
        Post(init);
    }

    /// <summary>Takes a message of the page, as JSON.</summary>
    private void OnMessage(string json)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        string? type = message.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
        switch (type)
        {
            case "loaded":
                if (message.TryGetProperty("isolated", out JsonElement isolated) && isolated.ValueKind == JsonValueKind.False)
                {
                    Log.Error("The calling engine's page is not isolated; its threads cannot share memory");
                }
                _loaded?.TrySetResult();
                return;
            case "log":
                Log.Info(message.TryGetProperty("message", out JsonElement text) ? text.GetString() ?? "" : "");
                return;
        }
        MessageReceived?.Invoke(message);
    }

    /// <summary>A file of Assets\Voip, and nothing outside it.</summary>
    private static string? SafeAsset(string path)
    {
        if (path.Length == 0 || path.Contains("..") || path.Contains('/') || path.Contains('\\'))
        {
            return null;
        }
        return Path.Combine(s_assets, path);
    }

    private static Task<string>? s_download;

    /// <summary>Downloads the engine ahead of the first call, so that a call does not wait for it.</summary>
    public static void Prefetch()
    {
        _ = DownloadOnceAsync().ContinueWith(t => Log.Error("Failed to download the calling engine", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
    }

    private static Task<string> EnsureWasmAsync() => DownloadOnceAsync();

    private static Task<string> DownloadOnceAsync()
    {
        Task<string>? download = s_download;
        if (download is null || download.IsFaulted || download.IsCanceled)
        {
            download = s_download = Task.Run(DownloadWasmAsync);
        }
        return download;
    }

    /// <summary>The engine's binary, downloaded once into the data folder.</summary>
    private static async Task<string> DownloadWasmAsync()
    {
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(s_assets, "manifest.json")));
        JsonElement wasm = manifest.RootElement.GetProperty("wasm");
        string url = wasm.GetProperty("url").GetString()!;
        string sha = wasm.GetProperty("sha256").GetString()!;

        string folder = Path.Combine(AppPaths.DataFolder, "Calls");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, sha + ".wasm");
        if (File.Exists(file))
        {
            return file;
        }

        Log.Info("Downloading the calling engine from " + url);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // WhatsApp's servers answer requests that look like a browser's.
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:143.0) Gecko/20100101 Firefox/143.0");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Referer", "https://web.whatsapp.com/");
        using HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        byte[] data = await response.Content.ReadAsByteArrayAsync();
        string actual = Convert.ToHexStringLower(SHA256.HashData(data));
        if (actual != sha)
        {
            throw new InvalidDataException("The calling engine WhatsApp sent is not the expected one. TwoTicks needs an update.");
        }
        foreach (string old in Directory.GetFiles(folder, "*.wasm"))
        {
            File.Delete(old);
        }
        string temporary = file + ".tmp";
        await File.WriteAllBytesAsync(temporary, data);
        File.Move(temporary, file, overwrite: true);
        return file;
    }

    /// <summary>Closes the page and the engine with it, which frees the memory a call takes.</summary>
    public void Stop()
    {
        _loaded?.TrySetCanceled();
        _loaded = null;
        PageServer? server = _server;
        _server = null;
        if (server is not null)
        {
            // Closing the page is not the page failing.
            server.ChannelClosed -= OnChannelClosed;
            server.MessageReceived -= OnChannelMessage;
        }
        ClosePage();
        server?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

/// <summary>The calling engine cannot run here, for a reason the person can do something about; the message says what.</summary>
internal sealed class VoipSetupException(string message) : Exception(message);
