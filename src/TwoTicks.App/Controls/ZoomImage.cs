using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace TwoTicks.App.Controls;

/// <summary>
/// A photo that fits the space it has and zooms: with the mouse wheel towards
/// the pointer, with a double click, by pinching, and with the zoom buttons of
/// the viewer. A zoomed photo moves by dragging. Swiping a photo that is not
/// zoomed asks for the next or previous one.
/// </summary>
public sealed partial class ZoomImage : Grid
{
    private const double Room = 24;
    private const double WheelStep = 1.25;
    private const double ButtonStep = 1.5;

    // A canvas neither measures nor clips the photo, which can be far larger than the view.
    private readonly Canvas _canvas = new();
    private readonly Grid _stage = new();
    private readonly Image _preview = new() { Stretch = Stretch.Fill };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly CompositeTransform _transform = new();
    private readonly RectangleGeometry _clip = new();
    private readonly Stopwatch _clock = new();

    // The photo at 100 %, in view pixels, and whether that is its real size or a guess from the preview.
    private Size _content;
    private bool _sizeKnown;

    // Where the photo is: its scale and the position of its top left corner.
    private double _scale = 1;
    private double _x;
    private double _y;
    private bool _fitted = true;

    // A zoom on its way: the scale it goes to, and the point of the photo that stays under the pointer.
    private double _targetScale;
    private Point _anchorView;
    private Point _anchorContent;
    private bool _animating;

    private double _swipe;
    private bool _grabCursor;

    public ZoomImage()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Clip = _clip;
        _stage.RenderTransform = _transform;
        _stage.Children.Add(_preview);
        _stage.Children.Add(_image);
        _canvas.Children.Add(_stage);
        Children.Add(_canvas);

        _image.ImageOpened += OnImageOpened;
        SizeChanged += (_, _) => OnSizeChanged();
        Unloaded += (_, _) => StopAnimation();

        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale | ManipulationModes.TranslateInertia;
        ManipulationStarted += OnManipulationStarted;
        ManipulationDelta += OnManipulationDelta;
        ManipulationCompleted += OnManipulationCompleted;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        Tapped += OnTapped;
    }

    /// <summary>The scale or position changed; <see cref="Zoom"/> has the new zoom.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>A swipe on a photo that is not zoomed: +1 for the next one, -1 for the previous.</summary>
    public event EventHandler<int>? Swiped;

    /// <summary>A click beside the photo, where there is nothing to see.</summary>
    public event EventHandler? EmptyTapped;

    /// <summary>The zoom against the photo's real size: 1 shows one pixel of the photo as one pixel of the screen.</summary>
    public double Zoom => _sizeKnown ? _scale * RasterScale : 0;

    /// <summary>Whether the photo is larger than it is when it fits.</summary>
    public bool IsZoomed => _scale > FitScale * 1.001;

    public bool CanZoomIn => _sizeKnown && _scale < MaxScale * 0.999;

    private double RasterScale => XamlRoot?.RasterizationScale ?? 1;

    private double FitScale
    {
        get
        {
            if (_content.Width <= 0 || _content.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return 1;
            }
            double room = Math.Min(Room, Math.Min(ActualWidth, ActualHeight) / 8);
            double fit = Math.Min((ActualWidth - 2 * room) / _content.Width, (ActualHeight - 2 * room) / _content.Height);
            // A small photo stays at its size rather than turning blurry; a preview stands in for a photo that is large.
            return _sizeKnown ? Math.Min(fit, 1) : fit;
        }
    }

    private double MaxScale => Math.Max(FitScale * 4, 8 / RasterScale);

    /// <summary>
    /// Shows a photo. The preview, a small image, shows until the file is
    /// there; width and height, when known, give the photo its shape before.
    /// </summary>
    public void Show(ImageSource? preview, string? path, int width, int height)
    {
        StopAnimation();
        _preview.Source = preview;
        _preview.Visibility = Visibility.Visible;
        _image.Source = null;
        _image.Opacity = 0;

        double raster = RasterScale;
        _sizeKnown = width > 0 && height > 0;
        _content = _sizeKnown ? new Size(width / raster, height / raster) : SizeOf(preview);
        Layout();
        Fit();

        if (path is not null)
        {
            SetFile(path);
        }
    }

    /// <summary>Puts the photo's file in place of its preview, once it is downloaded.</summary>
    public void SetFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.ImageFailed += (_, _) => _preview.Visibility = Visibility.Visible;
        bitmap.UriSource = new Uri(path);
        _image.Source = bitmap;
    }

    public void Clear()
    {
        StopAnimation();
        _preview.Source = null;
        _image.Source = null;
        _content = default;
        _sizeKnown = false;
    }

    public void ZoomIn() => ZoomAround(Center, TargetScale * ButtonStep);

    public void ZoomOut() => ZoomAround(Center, TargetScale / ButtonStep);

    /// <summary>Back to the whole photo in view.</summary>
    public void Fit()
    {
        StopAnimation();
        _scale = FitScale;
        _fitted = true;
        Place();
    }

    public void ZoomToFit() => ZoomAround(Center, FitScale);

    private double TargetScale => _animating ? _targetScale : _scale;

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    private static Size SizeOf(ImageSource? source) => source is BitmapSource { PixelWidth: > 0 } bitmap
        ? new Size(bitmap.PixelWidth, bitmap.PixelHeight)
        : new Size(4, 3);

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (_image.Source is not BitmapSource { PixelWidth: > 0 } bitmap)
        {
            return;
        }
        double raster = RasterScale;
        var real = new Size(bitmap.PixelWidth / raster, bitmap.PixelHeight / raster);
        // The size the message gave can be off, or there was none: the photo keeps the size it has on screen.
        StopAnimation();
        _scale *= _content.Width > 0 ? _content.Width / real.Width : 1;
        _content = real;
        _sizeKnown = true;
        Layout();
        if (_fitted)
        {
            _scale = FitScale;
        }
        _image.Opacity = 1;
        _preview.Visibility = Visibility.Collapsed;
        Place();
    }

    private void Layout()
    {
        _stage.Width = _content.Width;
        _stage.Height = _content.Height;
    }

    private void OnSizeChanged()
    {
        _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_fitted)
        {
            StopAnimation();
            _scale = FitScale;
        }
        Place();
    }

    /// <summary>Keeps the photo in view: centred where it is smaller than the view, its edges at the view's edges where larger.</summary>
    private void Place()
    {
        double width = _content.Width * _scale;
        double height = _content.Height * _scale;
        _x = width <= ActualWidth ? (ActualWidth - width) / 2 : Math.Clamp(_x, ActualWidth - width, 0);
        _y = height <= ActualHeight ? (ActualHeight - height) / 2 : Math.Clamp(_y, ActualHeight - height, 0);
        _transform.ScaleX = _transform.ScaleY = _scale;
        _transform.TranslateX = _x;
        _transform.TranslateY = _y;
        if (IsZoomed != _grabCursor)
        {
            _grabCursor = IsZoomed;
            ProtectedCursor = _grabCursor ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll) : null;
        }
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Zooming smoothly towards a point ----

    private void ZoomAround(Point view, double scale)
    {
        if (_content.Width <= 0)
        {
            return;
        }
        _targetScale = Math.Clamp(scale, FitScale, MaxScale);
        _anchorView = view;
        _anchorContent = new Point((view.X - _x) / _scale, (view.Y - _y) / _scale);
        _fitted = _targetScale <= FitScale * 1.001;
        if (!_animating)
        {
            _animating = true;
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        // Eases out: covers most of the way in the first frames, in log scale so that every step feels alike.
        double t = 1 - Math.Exp(-seconds * 18);
        double log = Math.Log(_scale) + (Math.Log(_targetScale) - Math.Log(_scale)) * t;
        bool done = Math.Abs(Math.Log(_targetScale) - log) < 0.002;
        SetScale(done ? _targetScale : Math.Exp(log));
        if (done)
        {
            StopAnimation();
        }
    }

    private void SetScale(double scale)
    {
        _scale = scale;
        _x = _anchorView.X - _anchorContent.X * scale;
        _y = _anchorView.Y - _anchorContent.Y * scale;
        Place();
    }

    private void StopAnimation()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    // ---- Mouse, touch and pen ----

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || point.Properties.IsHorizontalMouseWheel)
        {
            return;
        }
        e.Handled = true;
        ZoomAround(point.Position, TargetScale * Math.Pow(WheelStep, delta / 120.0));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (IsZoomed || (_animating && _targetScale > FitScale * 1.001))
        {
            ZoomAround(Center, FitScale);
            return;
        }
        // Up to the photo's real size, or further when that is hardly larger than it is now.
        double real = 1 / RasterScale;
        ZoomAround(e.GetPosition(this), real > FitScale * 1.6 ? real : FitScale * 2.5);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        Point p = e.GetPosition(this);
        var shown = new Rect(_x, _y, _content.Width * _scale, _content.Height * _scale);
        if (!shown.Contains(p))
        {
            e.Handled = true;
            EmptyTapped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        StopAnimation();
        _swipe = 0;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        e.Handled = true;
        ManipulationDelta delta = e.Delta;
        if (delta.Scale is > 0 and not 1)
        {
            // A pinch: the photo grows around the fingers.
            double scale = Math.Clamp(_scale * delta.Scale, FitScale, MaxScale);
            _anchorContent = new Point((e.Position.X - _x) / _scale, (e.Position.Y - _y) / _scale);
            _anchorView = new Point(e.Position.X + delta.Translation.X, e.Position.Y + delta.Translation.Y);
            _fitted = scale <= FitScale * 1.001;
            SetScale(scale);
            return;
        }
        if (IsZoomed)
        {
            _x += delta.Translation.X;
            _y += delta.Translation.Y;
            Place();
        }
        else if (!e.IsInertial)
        {
            // Not zoomed, a drag sideways pulls the photo along and becomes a swipe to the next one.
            _swipe += delta.Translation.X;
            _transform.TranslateX = _x + _swipe;
        }
        else
        {
            e.Complete();
        }
    }

    private void OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (IsZoomed || _swipe == 0)
        {
            return;
        }
        double velocity = e.Velocities.Linear.X;
        int direction = 0;
        if (Math.Abs(_swipe) > Math.Min(120, ActualWidth / 5) || (Math.Abs(_swipe) > 24 && Math.Abs(velocity) > 0.6))
        {
            direction = _swipe < 0 ? 1 : -1;
        }
        _swipe = 0;
        Place();
        if (direction != 0)
        {
            Swiped?.Invoke(this, direction);
        }
    }
}
