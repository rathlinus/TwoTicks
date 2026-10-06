using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Text;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// One line of text with WhatsApp's emoji, cut off with an ellipsis where it
/// runs out of room: names and the last message in the chat list.
/// </summary>
/// <remarks>
/// A RichTextBlock cut to one line still draws the emoji of the part it cut
/// off, at the start of the line. This lays the pieces out itself instead and
/// leaves out everything after the piece that ends in the ellipsis.
/// </remarks>
public sealed partial class EmojiLabel : Panel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(EmojiLabel), new PropertyMetadata(null, (d, _) => ((EmojiLabel)d).Build()));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(EmojiLabel), new PropertyMetadata(14.0, (d, _) => ((EmojiLabel)d).Build()));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(EmojiLabel), new PropertyMetadata(FontWeights.Normal, (d, _) => ((EmojiLabel)d).Restyle()));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(EmojiLabel), new PropertyMetadata(null, (d, _) => ((EmojiLabel)d).Restyle()));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private double EmojiSize => Math.Round(FontSize * 1.33);

    private void Build()
    {
        Children.Clear();
        string text = (Text ?? "").ReplaceLineEndings(" ");
        if (text.Length == 0)
        {
            return;
        }
        if (Emoji.Set is null || !Emoji.Set.ContainsEmoji(text))
        {
            Children.Add(NewText(text));
        }
        else
        {
            foreach (EmojiSegment segment in Emoji.Set.Split(text))
            {
                if (segment.IsEmoji)
                {
                    FrameworkElement emoji = Emoji.Create(segment.Emoji, EmojiSize);
                    emoji.Margin = new Thickness(1, 0, 1, 0);
                    Children.Add(emoji);
                }
                else
                {
                    Children.Add(NewText(segment.Text));
                }
            }
        }
        Restyle();
    }

    private TextBlock NewText(string text) => new()
    {
        Text = text,
        FontSize = FontSize,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsTextScaleFactorEnabled = false,
    };

    private void Restyle()
    {
        foreach (UIElement child in Children)
        {
            if (child is TextBlock text)
            {
                text.FontWeight = FontWeight;
                if (Foreground is not null)
                {
                    text.Foreground = Foreground;
                }
            }
        }
    }

    // How many pieces fit, decided when measuring, and whether the last of them is cut.
    private int _shown;

    protected override Size MeasureOverride(Size available)
    {
        double width = 0;
        double height = 0;
        _shown = Children.Count;
        for (int i = 0; i < Children.Count; i++)
        {
            UIElement child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
            double room = available.Width - width;
            if (child.DesiredSize.Width <= room)
            {
                width += child.DesiredSize.Width;
                continue;
            }
            // The piece that does not fit: text gets an ellipsis, an emoji is left out.
            _shown = i;
            if (child is TextBlock && room > 0)
            {
                child.Measure(new Size(room, double.PositiveInfinity));
                width += child.DesiredSize.Width;
                _shown = i + 1;
            }
            break;
        }
        return new Size(Math.Min(width, available.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            UIElement child = Children[i];
            Size size = child.DesiredSize;
            if (i < _shown)
            {
                double width = Math.Min(size.Width, Math.Max(0, finalSize.Width - x));
                child.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, width, size.Height));
                x += width;
            }
            else
            {
                // Out of sight: past the end, where the clip hides it.
                child.Arrange(new Rect(finalSize.Width + 100, 0, size.Width, size.Height));
            }
        }
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) };
        return finalSize;
    }
}
