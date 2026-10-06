using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// Lays out the text of a message with its time in the bottom right corner, as
/// WhatsApp does: beside the last line when the text leaves room for it, on a
/// line of its own when not.
/// </summary>
/// <remarks>
/// The last child is the time. Of the others, the first visible one is the text.
/// Whether the last line has room is found by measuring the text again in the
/// width left beside the time: if it then takes no more lines, every line
/// leaves that room.
/// </remarks>
public sealed partial class MessageBodyPanel : Panel
{
    private bool _timeOnOwnLine;

    private UIElement? Text()
    {
        for (int i = 0; i < Children.Count - 1; i++)
        {
            if (Children[i].Visibility == Visibility.Visible)
            {
                return Children[i];
            }
        }
        return null;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count == 0)
        {
            return default;
        }
        UIElement time = Children[^1];
        time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size timeSize = time.Visibility == Visibility.Visible ? time.DesiredSize : default;

        UIElement? text = Text();
        if (text is null)
        {
            _timeOnOwnLine = true;
            return timeSize;
        }

        text.Measure(available);
        Size full = text.DesiredSize;
        if (timeSize.Width == 0)
        {
            _timeOnOwnLine = false;
            return full;
        }
        if (double.IsInfinity(available.Width) || full.Width + timeSize.Width <= available.Width)
        {
            // Every line leaves room for the time, so it goes beside the last
            // without measuring the text again. That is most messages.
            _timeOnOwnLine = false;
            return new Size(full.Width + timeSize.Width, Math.Max(full.Height, timeSize.Height));
        }

        double narrowWidth = available.Width - timeSize.Width;
        if (narrowWidth > 0)
        {
            text.Measure(new Size(narrowWidth, available.Height));
            Size narrow = text.DesiredSize;
            if (narrow.Height <= full.Height + 0.5)
            {
                _timeOnOwnLine = false;
                return new Size(Math.Min(available.Width, Math.Max(full.Width, narrow.Width + timeSize.Width)), Math.Max(full.Height, timeSize.Height));
            }
            text.Measure(available);
        }
        _timeOnOwnLine = true;
        return new Size(Math.Max(full.Width, timeSize.Width), full.Height + timeSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }
        UIElement time = Children[^1];
        Size timeSize = time.Visibility == Visibility.Visible ? time.DesiredSize : default;

        UIElement? text = Text();
        for (int i = 0; i < Children.Count - 1; i++)
        {
            if (Children[i] != text)
            {
                Children[i].Arrange(default);
            }
        }
        if (text is not null)
        {
            double width = _timeOnOwnLine ? finalSize.Width : finalSize.Width - timeSize.Width;
            double height = _timeOnOwnLine ? finalSize.Height - timeSize.Height : finalSize.Height;
            text.Arrange(new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
        }
        time.Arrange(new Rect(Math.Max(0, finalSize.Width - timeSize.Width), Math.Max(0, finalSize.Height - timeSize.Height), timeSize.Width, timeSize.Height));
        return finalSize;
    }
}
