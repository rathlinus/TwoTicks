using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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

        // The app has no console; without this a failure would close the window
        // and leave no trace. A chat app should rather stay open.
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };
    }

    public static new App Current => (App)Application.Current;

    public Session Session { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public nint WindowHandle => WindowHandleOf(Window);

    public nint WindowHandleOf(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

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
        _notifier.Register(AppIcon.Folder(settings.WhatsAppIcon));

        // The helper starts first: it is ready with the chats by the time the
        // window has been built.
        Session = new Session(_ui, settings, _notifier);
        Session.Start();
        Window = new MainWindow();

        _tray = new TrayIcon(AppIcon.Folder(settings.WhatsAppIcon));
        _tray.OpenRequested += ShowWindow;
        _tray.QuitRequested += Quit;
        _badge = new TaskbarBadge(WindowHandle);
        Session.UnreadChanged += unread =>
        {
            _tray?.SetUnread(unread);
            _badge?.Set(unread);
            Window.SetUnread(unread);
        };

        // A second start of the app, or a click on a notification while it runs,
        // arrives here from Program.
        AppInstance.GetCurrent().Activated += (_, e) => _ui.TryEnqueue(() => OnActivated(e));

        Startup.Refresh();
        // A new install writes the Start menu entry again, with the logo.
        _ = Task.Run(() => AppIcon.UpdateShortcuts(settings.WhatsAppIcon));

        bool background = Environment.GetCommandLineArgs().Contains(Program.BackgroundSwitch);
        if (!background)
        {
            Window.ShowAndActivate();
        }
    }

    private void OnActivated(AppActivationArguments arguments) => ShowWindow();

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
        _quitting = true;
        Log.Info("Quitting");
        AudioPlayer.Stop();
        _tray?.Dispose();
        _tray = null;
        _notifier.ClearAll();
        _badge?.Dispose();
        Session.Client.Dispose();
        Window.CloseForGood();
        Exit();
    }
}
