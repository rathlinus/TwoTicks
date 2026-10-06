using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Calls;

/// <summary>
/// WhatsApp's calling engine, the one WhatsApp Web uses: a WebAssembly program
/// with the page around it in Assets\Voip, run in a WebView2 that is never shown.
/// Messages go to the page as JSON and come back the same way; see host.js.
/// </summary>
/// <remarks>
/// The engine runs on threads that share memory, which a page may only do when
/// it is isolated from other sites; <see cref="PageServer"/> serves the page with
/// the headers that make it so. The engine's binary is WhatsApp's
/// and not shipped with the app: it is downloaded from WhatsApp the first
/// time and checked against the hash its script was built for.
/// </remarks>
internal sealed class VoipEngine : IDisposable
{
    private const string WasmPath = "wa-voip.wasm";

    private static readonly string s_assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Voip");

    private readonly DispatcherQueue _ui;
    private nint _window;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private PageServer? _server;
    private TaskCompletionSource? _loaded;
    private string? _wasmFile;
    private bool _disposed;

    public VoipEngine(DispatcherQueue ui) => _ui = ui;

    /// <summary>A message from the page. Raised on the UI thread.</summary>
    public event Action<JsonElement>? MessageReceived;

    public bool IsRunning => _controller is not null;

    /// <summary>Opens the page and starts the engine in it, downloading the engine first when needed.</summary>
    public async Task StartAsync(CallIdentityData me, string? countryCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _wasmFile ??= await EnsureWasmAsync();
        if (_controller is null)
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
            ["debug"] = Environment.GetEnvironmentVariable("WINWHATSAPP_DEBUG") == "1",
        };
        Post(init);
    }

    public void Post(JsonObject message)
    {
        _controller?.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());
    }

    private async Task OpenPageAsync()
    {
        _window = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, "STATIC", "WinWhatsApp calls", Native.WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
        if (_window == 0)
        {
            throw new InvalidOperationException("Could not create the window of the calling engine.");
        }

        var options = new CoreWebView2EnvironmentOptions
        {
            // The page is never visible, so the browser must not slow it down as
            // it does background tabs, and it plays sound without a click.
            AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding " +
                "--disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required",
        };
        string profile = Path.Combine(AppPaths.DataFolder, "Calls");
        Log.Info("Opening the calling engine's page");
        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, options);
        _controller = await _environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_window));
        _controller.IsVisible = true;

        CoreWebView2 web = _controller.CoreWebView2;
        web.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("WINWHATSAPP_DEBUG") == "1";
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.PermissionRequested += (_, e) =>
        {
            // The microphone, for calls; nothing else.
            e.State = e.PermissionKind == CoreWebView2PermissionKind.Microphone ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        };
        web.WebMessageReceived += OnMessage;
        web.ProcessFailed += (_, e) =>
        {
            Log.Error($"The calling engine's browser process failed: {e.ProcessFailedKind}");
            var failed = JsonDocument.Parse("""{"type":"failed","message":"The calling engine stopped."}""").RootElement.Clone();
            _ui.TryEnqueue(() => MessageReceived?.Invoke(failed));
        };

        Log.Info("The calling engine's browser is up");
        _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server = new PageServer(path => path == WasmPath ? _wasmFile : SafeAsset(path));
        web.Navigate(_server.Origin + "/host.html");
        Task finished = await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (finished != _loaded.Task)
        {
            throw new TimeoutException("The calling engine's page did not load.");
        }
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(args.WebMessageAsJson).RootElement.Clone();
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
            throw new InvalidDataException("The calling engine WhatsApp sent is not the expected one. WinWhatsApp needs an update.");
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
        if (_controller is not null)
        {
            _controller.Close();
            _controller = null;
        }
        _environment = null;
        _server?.Dispose();
        _server = null;
        if (_window != 0)
        {
            Native.DestroyWindow(_window);
            _window = 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
