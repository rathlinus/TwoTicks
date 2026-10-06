using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Shapes;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// One of WhatsApp's icons, drawn in the foreground colour it inherits, as a
/// FontIcon would be.
/// </summary>
public sealed partial class WaIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(WaIcon), new PropertyMetadata(null, (d, _) => ((WaIcon)d).Build()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(WaIcon), new PropertyMetadata(20.0, (d, _) => ((WaIcon)d).Build()));

    private readonly Canvas _canvas = new();

    public WaIcon()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        Content = _canvas;
        Loaded += (_, _) => FollowParentForeground();
        Build();
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

    private void Build()
    {
        _canvas.Children.Clear();
        _canvas.Width = Size;
        _canvas.Height = Size;
        if (string.IsNullOrEmpty(Kind))
        {
            return;
        }
        foreach (IconPath part in WaIcons.Scaled(Kind, Size))
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path { Data = WaIcons.Geometry(part) };
            path.SetBinding(Shape.FillProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            _canvas.Children.Add(path);
        }
    }
}
