using System.Diagnostics;
using Microsoft.UI.Dispatching;
using TwoTicks.App.Calls;
using TwoTicks.Core;

namespace TwoTicks.App;

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
/// Keeps TwoTicks up to date from its releases on GitHub. It looks a minute
/// after start and then every few hours. With automatic installing on, a new
/// version is downloaded and installed while the window is closed and no call
/// is going on, or when the app quits; setup then starts the new version the
/// way the old one ran. Otherwise a notification tells about it once.
/// Installing by hand shows <see cref="UpdateWindow"/> until the new version runs.
/// </summary>
/// <remarks>
/// Only a copy that setup installed can update itself. A copy unpacked from the
/// zip, or a build, only says that there is a new version.
/// </remarks>
internal sealed partial class Updater
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    // While a downloaded update waits for the window to close.
    private static readonly TimeSpan InstallRetry = TimeSpan.FromMinutes(10);

    private readonly DispatcherQueue _ui;
    private readonly AppSettings _settings;
    private readonly Notifier _notifier;
    private readonly DispatcherQueueTimer _timer;
    private readonly HttpClient _http;
    private DateTime _lastCheck;
    private bool _installing;
    // The check or download going on, which a second one waits for instead of starting its own.
    private Task? _work;

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

    private static string DownloadFolder => Path.Combine(AppPaths.DataFolder, "updates");

    public UpdateState State { get; private set; }

    /// <summary>The newer version, once one was found.</summary>
    public UpdateInfo? Update { get; private set; }

    /// <summary>What went wrong in the last check or download.</summary>
    public string? Error { get; private set; }

    /// <summary>How much of the new version is downloaded, from 0 to 1.</summary>
    public double Progress { get; private set; }

    /// <summary>Setup runs, and the app is about to quit for it.</summary>
    public bool Installing => _installing;

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
        if (_installing)
        {
            return;
        }
        if (_work is { IsCompleted: false })
        {
            await _work;
            return;
        }
        _work = CheckCoreAsync(manual);
        await _work;
    }

    private async Task CheckCoreAsync(bool manual)
    {
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
                Log.Info($"TwoTicks {update.Version} is available");
            }
            SetState(_setupPath is not null ? UpdateState.Ready : UpdateState.Available);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Info($"Could not look for updates: {e.Message}");
            Error = Loc.T("updater.unreachable");
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
        Progress = 0;
        SetState(UpdateState.Downloading);
        // Created here, on the UI thread, so the reports arrive there.
        var progress = new Progress<double>(value =>
        {
            // A step per percent is enough for the window.
            if (State == UpdateState.Downloading && (int)(value * 100) != (int)(Progress * 100))
            {
                Progress = value;
                Changed?.Invoke();
            }
        });
        try
        {
            _setupPath = await Task.Run(() => Updates.DownloadAsync(_http, update, DownloadFolder, progress, CancellationToken.None));
            Log.Info($"Downloaded TwoTicks {update.Version}");
            SetState(UpdateState.Ready);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error($"Failed to download TwoTicks {update.Version}", e);
            Error = Loc.T("updater.downloadFailed");
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

    /// <summary>
    /// The Install button in the settings, the menu of the tray icon or a
    /// notification. The update window shows how far it is.
    /// </summary>
    public async Task InstallNowAsync()
    {
        if (!CanInstall)
        {
            OpenReleasePage();
            return;
        }
        if (_installing)
        {
            return;
        }
        ShowWindow();
        // A check or a download that runs already is waited for, not started again.
        if (_work is { IsCompleted: false })
        {
            await _work;
        }
        if (State is UpdateState.Unknown or UpdateState.UpToDate or UpdateState.Failed)
        {
            await CheckAsync(manual: true);
        }
        if (State == UpdateState.Available)
        {
            Task<bool> download = DownloadAsync();
            _work = download;
            if (!await download)
            {
                return;
            }
        }
        if (State == UpdateState.Ready)
        {
            Install(relaunch: true, background: false);
        }
    }

    /// <summary>Shows the window that tells how far an update by hand is.</summary>
    private partial void ShowWindow();

    /// <summary>Called when the app quits.</summary>
    public partial void CloseWindow();

    /// <summary>Starts installing the downloaded version; false when that did not work.</summary>
    private partial bool Install(bool relaunch, bool background);

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
        UpdateState.Checking => Loc.T("updater.checking"),
        UpdateState.UpToDate => Loc.T("updater.upToDate"),
        UpdateState.Available when Error is not null => Error,
        UpdateState.Available => Loc.T("updater.available", ("version", Update!.Version)),
        UpdateState.Downloading => Loc.T("updater.downloading", ("version", Update!.Version)),
        UpdateState.Ready when _settings.InstallUpdates => Loc.T("updater.readyAuto", ("version", Update!.Version)),
        UpdateState.Ready => Loc.T("updater.ready", ("version", Update!.Version)),
        UpdateState.Failed => Error ?? Loc.T("updater.failed"),
        _ => "",
    };
}
