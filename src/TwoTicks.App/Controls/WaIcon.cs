using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace TwoTicks.App.Controls;

/// <summary>
/// One of WhatsApp's icons, drawn in the foreground colour it inherits, as a
/// FontIcon would be.
/// </summary>
/// <remarks>
/// Every message row has a few of these, so they are cheap to make: the
/// paths are made once, when the icon is first measured, rather than at each
/// property XAML sets, and they take the colour directly instead of through a
/// binding each.
/// </remarks>
public sealed partial class WaIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(WaIcon), new PropertyMetadata(null, (d, _) => ((WaIcon)d).OnShapeChanged()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(WaIcon), new PropertyMetadata(20.0, (d, _) => ((WaIcon)d).OnShapeChanged()));

    private readonly Canvas _canvas = new() { Width = 20, Height = 20 };
    private string? _builtKind;
    private double _builtSize = -1;

    public WaIcon()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        Content = _canvas;
        Loaded += (_, _) => FollowParentForeground();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => ApplyFill());
    }

    /// <summary>
    /// Takes the text colour of the button or other control the icon is in, as
    /// a FontIcon does, unless the icon has a colour of its own. A control's
    /// Foreground is not passed down to controls inside it by itself.
    /// </summary>
    private void FollowParentForeground()
    {
        if (ReadLocalValue(ForegroundProperty) != DependencyProperty.UnsetValue)
        {
            return;
        }
        DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this);
        while (parent is not null)
        {
            if (parent is ContentPresenter or Control)
            {
                SetBinding(ForegroundProperty, new Binding { Source = parent, Path = new PropertyPath(nameof(Foreground)) });
                return;
            }
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        }
    }

    public string? Kind
    {
        get => (string?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void OnShapeChanged()
    {
        _canvas.Width = Size;
        _canvas.Height = Size;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Build();
        return base.MeasureOverride(availableSize);
    }

    private void Build()
    {
        string? kind = Kind;
        double size = Size;
        if (kind == _builtKind && size == _builtSize)
        {
            return;
        }
        _builtKind = kind;
        _builtSize = size;
        _canvas.Children.Clear();
        if (string.IsNullOrEmpty(kind))
        {
            return;
        }
        var fill = Foreground;
        foreach (IconPath part in WaIcons.Scaled(kind, size))
        {
            _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = WaIcons.Geometry(part), Fill = fill });
        }
    }

    private void ApplyFill()
    {
        var fill = Foreground;
        foreach (UIElement child in _canvas.Children)
        {
            ((Microsoft.UI.Xaml.Shapes.Path)child).Fill = fill;
        }
    }
}
