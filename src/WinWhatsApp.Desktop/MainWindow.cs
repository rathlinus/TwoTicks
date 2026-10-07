using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>What the window does with the system: it keeps the system's own title bar.</summary>
public sealed partial class MainWindow
{
    private bool _hidden;

    private double DisplayScale() => Content?.XamlRoot?.RasterizationScale ?? 1;

    private partial void BringToFront() => SystemWindow.BringToFront(this);

    /// <summary>The system draws the title bar, with its own buttons to minimize, maximize and close.</summary>
    private void ExtendTitleBar()
    {
        CaptionButtons.Visibility = Visibility.Collapsed;
        Controls.EmojiClipboard.Attach(Root);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ApplyTitleBarTheme()
    {
    }

    private void ShowWindow()
    {
        if (_hidden)
        {
            _hidden = false;
            SystemWindow.Show(this);
        }
        AppWindow.Show();
    }

    /// <summary>
    /// Off the screen while the app keeps running. Minimized instead where the
    /// app has no icon to bring the window back with, or a window cannot be hidden.
    /// </summary>
    private void HideWindow()
    {
        _hidden = App.Current.HasTray && SystemWindow.Hide(this);
        if (!_hidden && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    /// <summary>Opens the window minimized, for a start in the background on a desktop without a notification area.</summary>
    public void ShowMinimized()
    {
        AppWindow.Show(false);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void RestorePlacement()
    {
        WindowPlacement? placement = Session.Settings.Window;
        if (placement is { Width: > 0, Height: > 0 })
        {
            AppWindow.Resize(new SizeInt32 { Width = placement.Width, Height = placement.Height });
            AppWindow.Move(new PointInt32 { X = placement.X, Y = placement.Y });
            _normalBounds = new RectInt32 { X = placement.X, Y = placement.Y, Width = placement.Width, Height = placement.Height };
            if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        else
        {
            // Where it goes is left to the system.
            AppWindow.Resize(new SizeInt32 { Width = (int)(1180 * DisplayScale()), Height = (int)(780 * DisplayScale()) });
        }
    }
}
