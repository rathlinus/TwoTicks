using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using TwoTicks.Core;

namespace TwoTicks.App.Calls;

/// <summary>The calling engine's page in a WebView2 that is never shown.</summary>
internal sealed partial class VoipEngine
{
    private nint _window;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;

    // Held for as long as the page lives: when the garbage collector takes the
    // last reference, WebView2 destroys the object its events are wired to and
    // crashes on the next message from the page.
    private CoreWebView2? _web;

    public partial bool IsRunning => _controller is not null;

    private partial void PostToPage(string json) => _web?.PostWebMessageAsJson(json);

    private async partial Task OpenPageAsync()
    {
        _window = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, "STATIC", "TwoTicks calls", Native.WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
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

        CoreWebView2 web = _web = _controller.CoreWebView2;
        web.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("TWOTICKS_DEBUG") == "1";
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.PermissionRequested += (_, e) =>
        {
            // The microphone, for calls; nothing else.
            e.State = e.PermissionKind == CoreWebView2PermissionKind.Microphone ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            // Kept, so that the page also sees the names of the microphones and speakers.
            e.SavesInProfile = true;
        };
        web.WebMessageReceived += OnWebMessage;
        web.ProcessFailed += (_, e) => OnPageFailed(e.ProcessFailedKind.ToString());

        Log.Info("The calling engine's browser is up");
        await ServeAndWaitAsync(web.Navigate);
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) => OnMessage(args.WebMessageAsJson);

    /// <summary>
    /// Pauses the page's thread with the browser's debugger for a moment and
    /// reads where it is. A thread that runs script, even in a loop, stops for
    /// the debugger; one that waits inside the browser does not answer it.
    /// </summary>
    private async partial Task<string> DescribeStallAsync()
    {
        if (_web is not { } web)
        {
            return "the page is closed";
        }
        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2DevToolsProtocolEventReceiver receiver = web.GetDevToolsProtocolEventReceiver("Debugger.paused");
        void OnPaused(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e) => paused.TrySetResult(e.ParameterObjectAsJson);
        receiver.DevToolsProtocolEventReceived += OnPaused;
        try
        {
            Task<string> enabling = web.CallDevToolsProtocolMethodAsync("Debugger.enable", "{}").AsTask();
            if (await Task.WhenAny(enabling, Task.Delay(TimeSpan.FromSeconds(3))) != enabling)
            {
                return "its thread does not answer the debugger, so it waits inside the browser rather than running script";
            }
            _ = web.CallDevToolsProtocolMethodAsync("Debugger.pause", "{}");
            if (await Task.WhenAny(paused.Task, Task.Delay(TimeSpan.FromSeconds(3))) != paused.Task)
            {
                return "its thread did not stop for the debugger";
            }
            return DescribeFrames(await paused.Task);
        }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= OnPaused;
            // Turning the debugger off lets the page go on.
            _ = web.CallDevToolsProtocolMethodAsync("Debugger.disable", "{}");
        }
    }

    private static string DescribeFrames(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var text = new StringBuilder();
        int count = 0;
        foreach (JsonElement frame in document.RootElement.GetProperty("callFrames").EnumerateArray())
        {
            if (count++ == 30)
            {
                text.Append(Environment.NewLine).Append("    …");
                break;
            }
            string name = frame.GetProperty("functionName").GetString() is { Length: > 0 } function ? function : "(anonymous)";
            string url = frame.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
            JsonElement location = frame.GetProperty("location");
            text.Append(Environment.NewLine)
                .Append("    at ").Append(name)
                .Append(" (").Append(url[(url.LastIndexOf('/') + 1)..])
                .Append(':').Append(location.GetProperty("lineNumber").GetInt32() + 1)
                .Append(':').Append(location.TryGetProperty("columnNumber", out JsonElement c) ? c.GetInt32() + 1 : 0)
                .Append(')');
        }
        return count == 0 ? "no script on its thread" : text.ToString();
    }


    private partial void ClosePage()
    {
        if (_controller is not null)
        {
            if (_web is not null)
            {
                _web.WebMessageReceived -= OnWebMessage;
                _web = null;
            }
            _controller.Close();
            _controller = null;
        }
        _environment = null;
        if (_window != 0)
        {
            Native.DestroyWindow(_window);
            _window = 0;
        }
    }
}
