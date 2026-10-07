using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

public partial class App : Application
{
    private Notifier _notifier = null!;
    private TrayIcon? _tray;
    private TaskbarBadge? _badge;
    private DispatcherQueue _ui = null!;
    private bool _quitting;

    public App()
    {
        InitializeComponent();
        AdaptResources();

        // The app has no console; without this a failure would close the window
        // and leave no trace. A chat app should rather stay open.
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Changes what in App.xaml only holds on Windows, where the app runs elsewhere.</summary>
    partial void AdaptResources();

    public static new App Current => (App)Application.Current;

    public Session Session { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    internal Updater Updater { get; private set; } = null!;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception e)
        {
            Log.Error("Failed to start", e);
            throw;
        }
    }

    private void Launch()
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        Directory.CreateDirectory(AppPaths.DataFolder);
        Log.Info($"Starting WinWhatsApp {typeof(App).Assembly.GetName().Version?.ToString(3)}");

        AppSettings settings = SettingsStore.Load();
        Controls.Emoji.Load();
        _notifier = new Notifier();
        _notifier.Opened += chat => _ui.TryEnqueue(() =>
        {
            ShowWindow();
            if (chat is not null)
            {
                _ = Window.OpenChatAsync(chat);
            }
        });
        _notifier.Replied += (chat, text) => _ui.TryEnqueue(() => _ = Session.ReplyFromNotificationAsync(chat, text));
        _notifier.MarkedRead += chat => _ui.TryEnqueue(() => _ = Session.MarkReadAsync(chat));
        _notifier.CallAnswered += () => _ui.TryEnqueue(() =>
        {
            Session.Calls.Answer();
            Session.Calls.ShowWindow();
        });
        _notifier.CallDeclined += () => _ui.TryEnqueue(() => Session.Calls.Decline());
        _notifier.CallOpened += () => _ui.TryEnqueue(() => Session.Calls.ShowWindow());
        _notifier.UpdateRequested += () => _ui.TryEnqueue(() => _ = Updater.InstallNowAsync());
        _notifier.Register(AppIcon.Folder(settings.WhatsAppIcon));

        // The helper starts first: it is ready with the chats by the time the
        // window has been built.
        Session = new Session(_ui, settings, _notifier);
        Session.Start();
        Window = new MainWindow();

        _tray = new TrayIcon(AppIcon.Folder(settings.WhatsAppIcon));
        _tray.OpenRequested += ShowWindow;
        _tray.QuitRequested += Quit;
        _tray.UpdateRequested += () => _ = Updater.InstallNowAsync();
        _badge = new TaskbarBadge(Window);
        Session.UnreadChanged += unread =>
        {
            _tray?.SetUnread(unread);
            _badge?.Set(unread);
            Window.SetUnread(unread);
        };

        // A second start of the app, or a click on a notification while it runs,
        // arrives here from Program.
        ListenForSecondStart(() => _ui.TryEnqueue(ShowWindow));

        Updater = new Updater(_ui, settings, _notifier);
        Updater.Changed += () =>
        {
            if (_tray is not null)
            {
                _tray.UpdateVersion = Updater.CanInstall && Updater.State is UpdateState.Available or UpdateState.Ready
                    ? Updater.Update?.Version.ToString()
                    : null;
            }
        };
        Updater.Start();

        Startup.Refresh();
        // A new install writes the Start menu entry again, with the logo.
        _ = Task.Run(() => AppIcon.UpdateShortcuts(settings.WhatsAppIcon));

        bool background = Environment.GetCommandLineArgs().Contains(Program.BackgroundSwitch);
        if (!background)
        {
            Window.ShowAndActivate();
        }
        Launched();
    }

    /// <summary>The app is up: what a system does once everything else is.</summary>
    partial void Launched();

    /// <summary>Calls back, on any thread, when the app is started while it runs already.</summary>
    private partial void ListenForSecondStart(Action started);

    public void ShowWindow() => Window.ShowAndActivate();

    /// <summary>Shows the icon picked in the settings everywhere the app has one.</summary>
    public void ApplyIcon()
    {
        bool whatsApp = Session.Settings.WhatsAppIcon;
        string assets = AppIcon.Folder(whatsApp);
        Window.SetIcon(assets);
        _tray?.SetIcons(assets);
        try
        {
            _notifier.SetIcon(assets);
        }
        catch (Exception e)
        {
            Log.Error("Failed to change the notification icon", e);
        }
        _ = Task.Run(() => AppIcon.UpdateShortcuts(whatsApp));
    }

    public void Quit()
    {
        if (_quitting)
        {
            return;
        }
        Log.Info("Quitting");
        Updater.InstallOnQuit();
        Shutdown();
        Exit();
    }

    /// <summary>Quits and starts again, as after picking another language.</summary>
    public void Restart()
    {
        if (_quitting)
        {
            return;
        }
        Log.Info("Restarting");
        Shutdown();
        RestartProcess();
        Exit();
    }

    /// <summary>Starts the app again; this process ends when that works, or right after.</summary>
    private partial void RestartProcess();

    private void Shutdown()
    {
        _quitting = true;
        Updater.CloseWindow();
        AudioPlayer.Stop();
        Session.Calls.Shutdown();
        _tray?.Dispose();
        _tray = null;
        _notifier.ClearAll();
        _badge?.Dispose();
        Session.Client.Dispose();
        Window.CloseForGood();
    }
}
