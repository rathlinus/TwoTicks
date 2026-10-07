using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>What the window does with Windows itself: its title bar and its place on the taskbar.</summary>
public sealed partial class MainWindow
{
    private bool _titleBarPending;
    private RectInt32[] _captionRects = [];
    private RectInt32[] _passthroughRects = [];

    private double DisplayScale() => Content?.XamlRoot?.RasterizationScale ?? Native.GetDpiForWindow(App.Current.WindowHandleOf(this)) / 96.0;

    private partial void BringToFront() => Native.SetForegroundWindow(App.Current.WindowHandleOf(this));

    private void ShowWindow() => AppWindow.Show();

    private void HideWindow() => AppWindow.Hide();

    private void ApplyTitleBarTheme()
    {
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredTheme = Session.Settings.Theme switch
            {
                "Light" => TitleBarTheme.Light,
                "Dark" => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };
        }
    }

    private void RestorePlacement()
    {
        WindowPlacement? placement = Session.Settings.Window;
        if (placement is { Width: > 0, Height: > 0 })
        {
            var bounds = new RectInt32 { X = placement.X, Y = placement.Y, Width = placement.Width, Height = placement.Height };
            // Only where a screen still is.
            DisplayArea area = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;
            bounds.Width = Math.Min(bounds.Width, work.Width);
            bounds.Height = Math.Min(bounds.Height, work.Height);
            bounds.X = Math.Clamp(bounds.X, work.X, work.X + work.Width - bounds.Width);
            bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - bounds.Height);
            AppWindow.MoveAndResize(bounds);
            _normalBounds = bounds;
            if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        else
        {
            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            int width = Math.Min((int)(1180 * DisplayScale()), work.Width);
            int height = Math.Min((int)(780 * DisplayScale()), work.Height);
            AppWindow.MoveAndResize(new RectInt32 { X = work.X + (work.Width - width) / 2, Y = work.Y + (work.Height - height) / 2, Width = width, Height = height });
            _normalBounds = new RectInt32 { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height };
        }
    }

    // ---- The title bar ----

    /// <summary>
    /// Lets the app draw up to the top of the window, and hides the window's own
    /// minimize, maximize and close: Windows draws them at most 48 px tall, so the app
    /// draws its own, as tall as the headers. The headers move the window.
    /// </summary>
    private void ExtendTitleBar()
    {
        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        Root.LayoutUpdated += (_, _) => QueueTitleBarUpdate();
        Root.ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, SetCaptionColor);
        SetCaptionColor();
        AppWindow.Changed += (_, _) => QueueTitleBarUpdate();
    }

    /// <summary>
    /// Windows keeps a line of its frame above the app when the window is maximized,
    /// in the caption colour, which is light by default: it takes the colour of the headers.
    /// </summary>
    private void SetCaptionColor()
    {
        if (ChatPane.Background is SolidColorBrush { Color: var color })
        {
            uint colorRef = color.R | (uint)color.G << 8 | (uint)color.B << 16;
            Native.DwmSetWindowAttribute(App.Current.WindowHandleOf(this), Native.DWMWA_CAPTION_COLOR, ref colorRef, sizeof(uint));
        }
    }

    /// <summary>Updates the title bar once after the layout settles, not on every pass.</summary>
    private void QueueTitleBarUpdate()
    {
        if (!_titleBarPending)
        {
            _titleBarPending = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateTitleBar);
        }
    }

    /// <summary>
    /// Makes the headers on top move the window, as a title bar does, while their
    /// buttons stay buttons, and keeps the buttons on the right clear of the window's own.
    /// </summary>
    private void UpdateTitleBar()
    {
        _titleBarPending = false;
        // Queued before the window closed for good, when its content is gone;
        // a throw here would end the app with a crash instead of quitting.
        if (_quitting || Content?.XamlRoot is not { } root)
        {
            return;
        }
        double scale = root.RasterizationScale;
        double inset = CaptionButtons.ActualWidth;
        double strip = CaptionButtons.ActualHeight;

        bool viewer = Viewer.IsOpen;
        bool login = LoginPane.Visibility == Visibility.Visible;
        bool chat = ConversationPane.Visibility == Visibility.Visible;
        ConversationPane.CaptionInset = ProfilePane.IsOpen ? 0 : inset;
        ProfilePane.CaptionInset = inset;
        // When the chat beside the list is narrower than the window's buttons.
        SetPadding(PaneHeader, new Thickness(20, 0, 10 + Math.Max(0, inset - ChatArea.ActualWidth), 0));
        Viewer.CaptionInset = inset;
        // Light on the dark viewer.
        ElementTheme captionTheme = viewer ? ElementTheme.Dark : ElementTheme.Default;
        if (CaptionButtons.RequestedTheme != captionTheme)
        {
            CaptionButtons.RequestedTheme = captionTheme;
        }

        var caption = new List<RectInt32>();
        var passthrough = new List<RectInt32>();
        void Add(FrameworkElement area, double height)
        {
            if (area.ActualWidth <= 0 || area.ActualHeight <= 0 || !IsShown(area))
            {
                return;
            }
            caption.Add(ToWindow(area, Math.Min(height, area.ActualHeight)));
            // The buttons in a header stay clickable.
            foreach (ButtonBase button in Descendants<ButtonBase>(area))
            {
                if (button.ActualWidth > 0 && IsShown(button))
                {
                    passthrough.Add(ToWindow(button, button.ActualHeight));
                }
            }
        }
        RectInt32 ToWindow(FrameworkElement element, double height)
        {
            Rect bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, height));
            return new RectInt32(
                (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
        }

        if (viewer)
        {
            Add(Viewer.TitleArea, double.MaxValue);
        }
        else if (login)
        {
            Add(LoginPane, strip);
        }
        else
        {
            Add(PaneHeader, PaneHeader.ActualHeight);
            Add(chat ? ConversationPane.TitleArea : EmptyState, chat ? double.MaxValue : strip);
            if (ProfilePane.IsOpen)
            {
                Add(ProfilePane.TitleArea, double.MaxValue);
            }
        }

        foreach (Button button in (Button[])[MinimizeButton, MaximizeButton, CloseButton])
        {
            passthrough.Add(ToWindow(button, button.ActualHeight));
        }

        if (!caption.SequenceEqual(_captionRects) || !passthrough.SequenceEqual(_passthroughRects))
        {
            _captionRects = [.. caption];
            _passthroughRects = [.. passthrough];
            InputNonClientPointerSource source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, _captionRects);
            source.SetRegionRects(NonClientRegionKind.Passthrough, _passthroughRects);
        }
    }

    /// <summary>Closes as the window's own close button does, so closing to the tray still applies.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        Native.PostMessage(App.Current.WindowHandleOf(this), Native.WM_CLOSE, 0, 0);
}
