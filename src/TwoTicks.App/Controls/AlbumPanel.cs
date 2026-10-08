using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using TwoTicks.App.Models;

namespace TwoTicks.App.Controls;

/// <summary>
/// Lays out the photos of an album as WhatsApp does: two side by side, three
/// with the first across the top, four in a square.
/// </summary>
public sealed class AlbumPanel : Panel
{
    private const double Gap = 2;
    private const double Side = (MessageItem.AlbumWidth - Gap) / 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        Rect bounds = default;
        for (int i = 0; i < Children.Count; i++)
        {
            Rect slot = Slot(i, Children.Count);
            Children[i].Measure(new Size(slot.Width, slot.Height));
            bounds.Width = Math.Max(bounds.Width, slot.Right);
            bounds.Height = Math.Max(bounds.Height, slot.Bottom);
        }
        return new Size(bounds.Width, bounds.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(Slot(i, Children.Count));
        }
        return finalSize;
    }

    private static Rect Slot(int index, int count)
    {
        // With three, the first has the top row to itself.
        if (count == 3)
        {
            return index == 0
                ? new Rect(0, 0, MessageItem.AlbumWidth, Side)
                : new Rect((index - 1) * (Side + Gap), Side + Gap, Side, Side);
        }
        if (count == 1)
        {
            return new Rect(0, 0, MessageItem.AlbumWidth, Side);
        }
        return new Rect(index % 2 * (Side + Gap), index / 2 * (Side + Gap), Side, Side);
    }
}
