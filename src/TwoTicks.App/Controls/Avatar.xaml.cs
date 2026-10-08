using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TwoTicks.App.Controls;

public sealed partial class Avatar : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty IsGroupProperty = DependencyProperty.Register(
        nameof(IsGroup), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(48.0, (d, _) => ((Avatar)d).Refresh()));

    public Avatar()
    {
        InitializeComponent();
        Refresh();
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsGroup
    {
        get => (bool)GetValue(IsGroupProperty);
        set => SetValue(IsGroupProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Refresh()
    {
        Root.Width = Size;
        Root.Height = Size;
        PersonIcon.Size = Size;
        GroupIcon.Size = Math.Round(Size * 0.55);
        bool hasPhoto = Source is not null;
        PhotoImage.Source = Source;
        Photo.CornerRadius = new CornerRadius(Size / 2);
        Photo.Visibility = hasPhoto ? Visibility.Visible : Visibility.Collapsed;
        PersonIcon.Visibility = !hasPhoto && !IsGroup ? Visibility.Visible : Visibility.Collapsed;
        GroupIcon.Visibility = !hasPhoto && IsGroup ? Visibility.Visible : Visibility.Collapsed;
    }
}
