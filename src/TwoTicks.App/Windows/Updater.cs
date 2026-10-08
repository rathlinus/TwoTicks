using System.Diagnostics;
using Microsoft.Win32;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>Installing a new version with its setup program, which only Windows has.</summary>
internal sealed partial class Updater
{
    // The AppId in packaging\TwoTicks.iss; Inno Setup files the uninstall entry under it.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C4E7B2A-5D18-4F63-A0E9-3B7C1D6F8A42}_is1";


    private UpdateWindow? _window;

    /// <summary>Whether this copy is the one setup installed, which a new setup program can replace.</summary>
    public static bool CanInstall { get; } = IsInstalledCopy();

    /// <summary>Setup's log of the last update, for when one went wrong.</summary>
    private static string SetupLog => Path.Combine(AppPaths.DataFolder, "update.log");

    private partial void ShowWindow()
    {
        if (_window is null)
        {
            _window = new UpdateWindow(this, _settings.WhatsAppIcon);
            _window.Closed += (_, _) => _window = null;
            _window.ShowCentered();
        }
        else
        {
            _window.Activate();
        }
    }

    public partial void CloseWindow() => _window?.Close();

    /// <summary>
    /// Starts the downloaded setup program without its wizard and quits, so
    /// setup can replace the files. Setup starts the new version afterwards
    /// when asked to; see the [Run] section of packaging\TwoTicks.iss.
    /// </summary>
    private partial bool Install(bool relaunch, bool background)
    {
        if (_installing || _setupPath is null || !File.Exists(_setupPath))
        {
            return false;
        }
        string mode = !relaunch ? "no" : background ? "background" : "window";
        // Setup waits for this process to end; see the [Code] section of the script.
        string arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /NOCLOSEAPPLICATIONS /SP- /RELAUNCH={mode} " +
            $"/WAITPID={Environment.ProcessId} /LANG={Loc.Language} /LOG=\"{SetupLog}\"";
        // Setup shows its copy of the window only when it starts the new version afterwards.
        UpdateWindow? window = relaunch && !background ? _window : null;
        if (window is not null && window.SaveLogoForSetup(DownloadFolder) is { } logo)
        {
            (Windows.Graphics.RectInt32 bounds, uint dpi) = window.Placement;
            arguments += $" /UPDATEWINDOW={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height},{dpi} /LOGO=\"{logo}\"";
        }
        else
        {
            window = null;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_setupPath)
            {
                Arguments = arguments,
                UseShellExecute = false,
            });
        }
        catch (Exception e)
        {
            Log.Error("Failed to start the setup program", e);
            Error = Loc.T("updater.setupFailed");
            _setupPath = null;
            SetState(UpdateState.Available);
            return false;
        }
        _installing = true;
        Log.Info($"Installing TwoTicks {Update?.Version}");
        Changed?.Invoke();
        if (relaunch)
        {
            _ = QuitForSetupAsync(handOver: window is not null);
        }
        return true;
    }

    /// <summary>
    /// Quits so setup can replace the files. With the update window open, it
    /// first waits until setup shows its copy of the window on top, so the
    /// window never goes away in between.
    /// </summary>
    private async Task QuitForSetupAsync(bool handOver)
    {
        if (handOver)
        {
            var waited = Stopwatch.StartNew();
            // Starting setup can take a while: Windows looks through a program it has not seen before.
            while (Native.FindWindow(UpdateWindow.SetupWindowClass, UpdateWindow.SetupWindowTitle) == 0 && waited.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(50);
            }
        }
        // Queued, so whatever called Install finishes before the app shuts down.
        _ui.TryEnqueue(App.Current.Quit);
    }

    private static bool IsInstalledCopy()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
            {
                return false;
            }
            string installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            string running = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            return string.Equals(installed, running, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }
}
