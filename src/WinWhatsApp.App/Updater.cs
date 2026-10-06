using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using WinWhatsApp.App.Calls;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

public enum UpdateState
{
    /// <summary>Not looked yet in this session.</summary>
    Unknown,
    Checking,
    UpToDate,
    /// <summary>A newer version exists and is not downloaded.</summary>
    Available,
    Downloading,
    /// <summary>The setup program of the newer version is downloaded and checked.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Keeps WinWhatsApp up to date from its releases on GitHub. It looks a minute
/// after start and then every few hours. With automatic installing on, a new
/// version is downloaded and installed while the window is closed and no call
/// is going on, or when the app quits; setup then starts the new version the
/// way the old one ran. Otherwise a notification tells about it once.
/// </summary>
/// <remarks>
/// Only a copy that setup installed can update itself. A copy unpacked from the
/// zip, or a build, only says that there is a new version.
/// </remarks>
internal sealed class Updater
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    // While a downloaded update waits for the window to close.
    private static readonly TimeSpan InstallRetry = TimeSpan.FromMinutes(10);

    // The AppId in packaging\WinWhatsApp.iss; Inno Setup files the uninstall entry under it.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C4E7B2A-5D18-4F63-A0E9-3B7C1D6F8A42}_is1";

    private readonly DispatcherQueue _ui;
    private readonly AppSettings _settings;
    private readonly Notifier _notifier;
    private readonly DispatcherQueueTimer _timer;
    private readonly HttpClient _http;
    private DateTime _lastCheck;
    private bool _installing;

    public Updater(DispatcherQueue ui, AppSettings settings, Notifier notifier)
    {
        _ui = ui;
        _settings = settings;
        _notifier = notifier;
        _http = Updates.CreateClient(Current);
        _timer = ui.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = OnTimerAsync();
    }

    public static Version Current { get; } = Updates.Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Whether this copy is the one setup installed, which a new setup program can replace.</summary>
    public static bool CanInstall { get; } = IsInstalledCopy();

    private static string DownloadFolder => Path.Combine(AppPaths.DataFolder, "updates");

    public UpdateState State { get; private set; }

    /// <summary>The newer version, once one was found.</summary>
    public UpdateInfo? Update { get; private set; }

    /// <summary>What went wrong in the last check or download.</summary>
    public string? Error { get; private set; }

    private string? _setupPath;

    /// <summary>The state changed. Raised on the UI thread.</summary>
    public event Action? Changed;

    public void Start()
    {
        // Setup programs of versions that are installed by now.
        _ = Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(DownloadFolder))
                {
                    Directory.Delete(DownloadFolder, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Setup may still be finishing; the next start tries again.
            }
        });
        Schedule(FirstCheck);
    }

    private void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private async Task OnTimerAsync()
    {
        if (State == UpdateState.Ready)
        {
            if (!TryInstallInBackground())
            {
                Schedule(InstallRetry);
            }
            return;
        }
        if (_settings.CheckForUpdates)
        {
            await CheckAsync(manual: false);
        }
        if (State != UpdateState.Ready)
        {
            Schedule(CheckInterval);
        }
    }

    /// <summary>Looks for a new version, and with automatic installing on downloads it.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading || _installing)
        {
            return;
        }
        // Once found, a version stays found; there is no need to ask GitHub again soon.
        if (!manual && Update is not null && DateTime.UtcNow - _lastCheck < CheckInterval)
        {
            return;
        }

        SetState(UpdateState.Checking);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            UpdateInfo? update = await Updates.CheckAsync(_http, Current, timeout.Token);
            _lastCheck = DateTime.UtcNow;
            if (update is null)
            {
                Update = null;
                SetState(UpdateState.UpToDate);
                return;
            }
            bool newer = Update is null || update.Version > Update.Version;
            Update = update;
            if (newer)
            {
                _setupPath = null;
                Log.Info($"WinWhatsApp {update.Version} is available");
            }
            SetState(_setupPath is not null ? UpdateState.Ready : UpdateState.Available);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Info($"Could not look for updates: {e.Message}");
            Error = "Could not reach GitHub to look for updates.";
            SetState(Update is null ? UpdateState.Failed : _setupPath is not null ? UpdateState.Ready : UpdateState.Available);
            return;
        }

        if (State == UpdateState.Available && CanInstall && _settings.InstallUpdates)
        {
            if (await DownloadAsync())
            {
                TryInstallInBackground();
            }
        }
        else if (State == UpdateState.Available && !manual)
        {
            Announce(Update!);
        }
    }

    private async Task<bool> DownloadAsync()
    {
        if (Update is not { } update)
        {
            return false;
        }
        SetState(UpdateState.Downloading);
        try
        {
            _setupPath = await Task.Run(() => Updates.DownloadAsync(_http, update, DownloadFolder, CancellationToken.None));
            Log.Info($"Downloaded WinWhatsApp {update.Version}");
            SetState(UpdateState.Ready);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error($"Failed to download WinWhatsApp {update.Version}", e);
            Error = "The download of the new version failed.";
            SetState(UpdateState.Available);
            return false;
        }
    }

    /// <summary>Tells about a version once, for copies that do not install it by themselves.</summary>
    private void Announce(UpdateInfo update)
    {
        string version = update.Version.ToString();
        if (_settings.AnnouncedUpdate == version)
        {
            return;
        }
        _settings.AnnouncedUpdate = version;
        SettingsStore.Save(_settings);
        _notifier.ShowUpdate(version, CanInstall);
    }

    /// <summary>
    /// Installs a downloaded update while nobody would notice the restart: the
    /// window is closed and no call is going on.
    /// </summary>
    private bool TryInstallInBackground()
    {
        if (State != UpdateState.Ready || !_settings.InstallUpdates)
        {
            return false;
        }
        App app = App.Current;
        if (app.Window.AppWindow.IsVisible || app.Session.Calls.Phase != CallPhase.Idle)
        {
            Schedule(InstallRetry);
            return false;
        }
        return Install(relaunch: true, background: true);
    }

    /// <summary>The Install button in the settings, the menu of the tray icon or a notification.</summary>
    public async Task InstallNowAsync()
    {
        if (!CanInstall)
        {
            OpenReleasePage();
            return;
        }
        if (State is UpdateState.Unknown or UpdateState.UpToDate or UpdateState.Failed)
        {
            await CheckAsync(manual: true);
        }
        if (State == UpdateState.Available && !await DownloadAsync())
        {
            return;
        }
        Install(relaunch: true, background: false);
    }

    /// <summary>Called when the app quits: a downloaded update is installed without starting the app again.</summary>
    public void InstallOnQuit()
    {
        if (State == UpdateState.Ready && _settings.InstallUpdates)
        {
            Install(relaunch: false, background: false);
        }
    }

    public void OpenReleasePage()
    {
        string page = Update?.PageUrl ?? Updates.ReleasesPage;
        Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
    }

    /// <summary>
    /// Starts the downloaded setup program without its wizard and quits, so
    /// setup can replace the files. Setup starts the new version afterwards
    /// when asked to; see the [Run] section of packaging\WinWhatsApp.iss.
    /// </summary>
    private bool Install(bool relaunch, bool background)
    {
        if (_installing || _setupPath is null || !File.Exists(_setupPath))
        {
            return false;
        }
        string mode = !relaunch ? "no" : background ? "background" : "window";
        try
        {
            Process.Start(new ProcessStartInfo(_setupPath)
            {
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /RELAUNCH={mode}",
                UseShellExecute = false,
            });
        }
        catch (Exception e)
        {
            Log.Error("Failed to start the setup program", e);
            Error = "The setup program did not start.";
            _setupPath = null;
            SetState(UpdateState.Available);
            return false;
        }
        _installing = true;
        Log.Info($"Installing WinWhatsApp {Update?.Version}");
        if (relaunch)
        {
            _ui.TryEnqueue(App.Current.Quit);
        }
        return true;
    }

    private void SetState(UpdateState state)
    {
        if (state is not UpdateState.Failed and not UpdateState.Available)
        {
            Error = null;
        }
        State = state;
        Changed?.Invoke();
    }

    /// <summary>The text the settings show for the current state.</summary>
    public string Describe() => State switch
    {
        UpdateState.Checking => "Looking for updates...",
        UpdateState.UpToDate => "WinWhatsApp is up to date.",
        UpdateState.Available when Error is not null => Error,
        UpdateState.Available => $"WinWhatsApp {Update!.Version} is available.",
        UpdateState.Downloading => $"Downloading WinWhatsApp {Update!.Version}...",
        UpdateState.Ready when _settings.InstallUpdates => $"WinWhatsApp {Update!.Version} is ready. It installs when the window is closed, or when you restart now.",
        UpdateState.Ready => $"WinWhatsApp {Update!.Version} is ready to install.",
        UpdateState.Failed => Error ?? "Looking for updates failed.",
        _ => "",
    };

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
