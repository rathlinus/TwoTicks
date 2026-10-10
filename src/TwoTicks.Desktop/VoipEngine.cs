using System.Diagnostics;
using System.Security.Cryptography;
using TwoTicks.Core;

namespace TwoTicks.App.Calls;

/// <summary>
/// The calling engine's page in a browser of the system that runs without a
/// window; see <see cref="CallBrowser"/>. The page's messages go through the
/// server that serves the page.
/// </summary>
internal sealed partial class VoipEngine
{
    private Process? _browser;
    private string? _profile;
    private bool _open;

    public partial bool IsRunning => _open;

    private partial void PostToPage(string json) => _server?.Send(json);

    // The browser takes no commands from the app; see CallBrowser.
    private partial Task<string> DescribeStallAsync() => Task.FromResult("this browser cannot be asked");

    private async partial Task OpenPageAsync()
    {
        CallBrowser browser = CallBrowser.Find() ?? throw new VoipSetupException(Loc.T("calls.noBrowser"));
        Log.Info("Opening the calling engine's page in " + browser.Name);
        // Only the page that gets this key in its address may talk to the app.
        string key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        try
        {
            await ServeAndWaitAsync(url => (_browser, _profile) = browser.Start(url), key);
            _open = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private partial void ClosePage()
    {
        _open = false;
        if (_browser is { } browser && _profile is { } profile)
        {
            _browser = null;
            _profile = null;
            CallBrowser.Stop(browser, profile, wait: _disposed);
        }
    }
}
