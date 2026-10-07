using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// A small window that shows an update being downloaded and installed, with
/// the app's logo hopping, as Discord's updater does. It opens when an update
/// is installed by hand. Once setup runs, setup shows the same window at the
/// same place until the new version starts; see packaging\WinWhatsApp.iss.
/// </summary>
public sealed partial class UpdateWindow : Window
{
    /// <summary>The window's width and height, in DIPs.</summary>
    private const int Size = 300;
    private const int LogoSize = 96;

    /// <summary>The window class and title of setup's copy of the window.</summary>
    internal const string SetupWindowClass = "TSetupForm", SetupWindowTitle = "WinWhatsApp update";

    // The logo hops this high for HopTime out of every HopPeriod; setup's copy
    // of the window does the same.
    private const float HopHeight = 14;
    private const double HopTime = 700;
    private const double HopPeriod = 1100;

    private readonly Updater _updater;
    private readonly string _logo;
    private readonly DispatcherQueueTimer _closeTimer;
    private bool _closed;

    internal UpdateWindow(Updater updater, bool whatsAppIcon)
    {
        InitializeComponent();
        _updater = updater;
        string assets = AppIcon.Folder(whatsAppIcon);
        _logo = Path.Combine(assets, "AppIcon.png");
        Title = Loc.T("updater.windowTitle");
        AppWindow.SetIcon(Path.Combine(assets, "AppIcon.ico"));

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        // Without a frame Windows 11 would draw the corners square.
        uint round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(uint));

        Logo.Source = new BitmapImage(new Uri(_logo));
        StartHopping();

        _closeTimer = DispatcherQueue.CreateTimer();
        _closeTimer.IsRepeating = false;
        _closeTimer.Interval = TimeSpan.FromSeconds(2);
        _closeTimer.Tick += (_, _) => Close();
        _updater.Changed += Refresh;
        Closed += (_, _) =>
        {
            _closed = true;
            _updater.Changed -= Refresh;
            _closeTimer.Stop();
        };
        Refresh();
    }

    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Where the window is, in pixels, and the DPI of its display.</summary>
    public (RectInt32 Bounds, uint Dpi) Placement
    {
        get
        {
            PointInt32 position = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;
            return (new RectInt32(position.X, position.Y, size.Width, size.Height), Native.GetDpiForWindow(Handle));
        }
    }

    /// <summary>Shows the window in the middle of the main window, or of the screen while that is closed.</summary>
    public void ShowCentered()
    {
        MainWindow main = App.Current.Window;
        double scale = Native.GetDpiForWindow(App.Current.WindowHandle) / 96.0;
        int size = (int)Math.Round(Size * scale);
        RectInt32 area;
        if (main.AppWindow.IsVisible && !Native.IsIconic(App.Current.WindowHandle))
        {
            area = new RectInt32(main.AppWindow.Position.X, main.AppWindow.Position.Y, main.AppWindow.Size.Width, main.AppWindow.Size.Height);
        }
        else
        {
            area = DisplayArea.GetFromWindowId(main.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        }
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size));
        AppWindow.Show();
        Activate();
    }

    /// <summary>
    /// Writes the logo for setup's copy of the window, at the size it has
    /// there in pixels, as setup draws it without scaling.
    /// </summary>
    public string? SaveLogoForSetup(string folder)
    {
        int pixels = (int)Math.Round(LogoSize * Placement.Dpi / 96.0);
        string path = Path.Combine(folder, "logo.png");
        try
        {
            Directory.CreateDirectory(folder);
            using var source = new System.Drawing.Bitmap(_logo);
            using var scaled = new System.Drawing.Bitmap(pixels, pixels);
            using (var graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, 0, 0, pixels, pixels);
            }
            scaled.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("Failed to write the logo for setup", e);
            return null;
        }
    }

    /// <summary>
    /// A parabola, as a thrown object flies, then a rest on the ground. Two
    /// quadratic curves make the parabola exactly.
    /// </summary>
    private void StartHopping()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(Logo, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(Logo);
        Compositor compositor = visual.Compositor;
        var rise = compositor.CreateCubicBezierEasingFunction(new Vector2(1 / 3f, 2 / 3f), new Vector2(2 / 3f, 1));
        var fall = compositor.CreateCubicBezierEasingFunction(new Vector2(1 / 3f, 0), new Vector2(2 / 3f, 1 / 3f));
        float top = (float)(HopTime / 2 / HopPeriod);
        float land = (float)(HopTime / HopPeriod);

        ScalarKeyFrameAnimation hop = compositor.CreateScalarKeyFrameAnimation();
        hop.InsertKeyFrame(0, 0);
        hop.InsertKeyFrame(top, -HopHeight, rise);
        hop.InsertKeyFrame(land, 0, fall);
        hop.InsertKeyFrame(1, 0);
        hop.Duration = TimeSpan.FromMilliseconds(HopPeriod);
        hop.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation.Y", hop);
    }

    private void Refresh()
    {
        if (_closed)
        {
            return;
        }
        UpdateState state = _updater.State;
        string version = Loc.T("updater.windowVersion", ("version", _updater.Update?.Version ?? Updater.Current));
        bool failed = state == UpdateState.Failed || (state == UpdateState.Available && _updater.Error is not null);
        double? progress = null;
        if (_updater.Installing || state == UpdateState.Ready)
        {
            TitleText.Text = Loc.T("updater.windowInstalling");
            progress = 0;
        }
        else if (failed)
        {
            TitleText.Text = Loc.T("updater.windowFailed");
            version = _updater.Error ?? Loc.T("updater.failed");
        }
        else if (state is UpdateState.Available or UpdateState.Downloading)
        {
            TitleText.Text = Loc.T("updater.windowDownloading");
            progress = state == UpdateState.Downloading ? _updater.Progress : 0;
        }
        else if (state == UpdateState.UpToDate)
        {
            TitleText.Text = Loc.T("updater.windowUpToDate");
        }
        else
        {
            TitleText.Text = Loc.T("updater.windowChecking");
            version = "";
        }
        DetailText.Text = version;

        Bar.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        Fill.Width = Bar.Width * Math.Clamp(progress ?? 0, 0, 1);
        CloseButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        // Up to date needs no answer; the window goes by itself.
        if (state == UpdateState.UpToDate && !_closeTimer.IsRunning)
        {
            _closeTimer.Start();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
