using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using TwoTicks.Core;

namespace TwoTicks.App.Calls;

/// <summary>
/// A call in a window of its own: who it is with, how long it runs, and the
/// buttons to answer, mute and hang up. It stays on top of other windows and
/// shows also while the main window is closed to the notification area.
/// </summary>
public sealed partial class CallWindow : Window
{
    private const int Width = 340;
    private const int Height = 500;

    private readonly CallManager _call;
    private bool _closing;

    public CallWindow(CallManager call)
    {
        InitializeComponent();
        _call = call;
        AppWindow.SetIcon(Path.Combine(AppIcon.Folder(App.Current.Session.Settings.WhatsAppIcon), "AppIcon.ico"));
#if !HAS_UNO
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
#endif
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.Closing += OnClosing;
        _call.PropertyChanged += OnCallChanged;
        Refresh();
    }

    /// <summary>Shows the window in the bottom right corner, over the taskbar's notification area.</summary>
    public void ShowCall(bool activate)
    {
        if (!AppWindow.IsVisible)
        {
            double scale = App.Current.ScaleOf(this);
            int width = (int)(Width * scale);
            int height = (int)(Height * scale);
#if HAS_UNO
            // Where it goes is left to the system.
            AppWindow.Resize(new SizeInt32 { Width = width, Height = height });
            SystemWindow.Show(this);
#else
            RectInt32 work = DisplayArea.GetFromWindowId(App.Current.Window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int margin = (int)(16 * scale);
            AppWindow.MoveAndResize(new RectInt32 { X = work.X + work.Width - width - margin, Y = work.Y + work.Height - height - margin, Width = width, Height = height });
#endif
        }
        Refresh();
        AppWindow.Show(activate);
        if (activate)
        {
            Activate();
        }
    }

#if HAS_UNO
    public void Hide()
    {
        if (!SystemWindow.Hide(this) && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }
#else
    public void Hide() => AppWindow.Hide();
#endif

    /// <summary>Lets the window close for real, when the app quits.</summary>
    public void CloseForGood()
    {
        _closing = true;
        _call.PropertyChanged -= OnCallChanged;
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing)
        {
            return;
        }
        // Closing the window ends the call; the window is kept for the next one.
        args.Cancel = true;
        _call.HangUp();
        Hide();
    }

    private void OnCallChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Title = _call.Name.Length > 0 ? _call.Name : Loc.T("calls.windowTitle");
        NameText.Text = _call.Name;
        StatusText.Text = _call.Status;
        Picture.Source = _call.Avatar;

        CallPhase phase = _call.Phase;
        bool ringing = phase == CallPhase.Incoming;
        bool ended = phase is CallPhase.Ended or CallPhase.Idle;
        AnswerButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
        MuteButton.Visibility = ringing ? Visibility.Collapsed : Visibility.Visible;
        MuteButton.IsEnabled = !ended;
        DeclineButton.IsEnabled = !ended;

        string hangUp = ringing ? Loc.T("calls.decline") : Loc.T("calls.hangUp");
        AutomationProperties.SetName(DeclineButton, hangUp);
        ToolTipService.SetToolTip(DeclineButton, hangUp);

        string mute = _call.IsMuted ? Loc.T("calls.unmute") : Loc.T("calls.mute");
        AutomationProperties.SetName(MuteButton, mute);
        ToolTipService.SetToolTip(MuteButton, mute);
        // Muted shows as a light button, as in WhatsApp.
        MuteButton.Background = new SolidColorBrush(_call.IsMuted ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xFF, 0x23, 0x31, 0x38));
        MuteIcon.Foreground = new SolidColorBrush(_call.IsMuted ? Windows.UI.Color.FromArgb(0xFF, 0x0B, 0x14, 0x1A) : Microsoft.UI.Colors.White);
    }

    private void OnAnswerClick(object sender, RoutedEventArgs e) => _call.Answer();

    private void OnHangUpClick(object sender, RoutedEventArgs e) => _call.HangUp();

    private void OnMuteClick(object sender, RoutedEventArgs e) => _call.ToggleMute();
}
