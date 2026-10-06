using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// Selects the paragraph under the pointer on a triple click in a
/// RichTextBlock, as browsers and Word do. RichTextBlock only selects
/// words on a double click. A paragraph is the text between two line breaks.
/// </summary>
public static class ParagraphSelection
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ParagraphSelection), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly PointerEventHandler s_pressed = OnPointerPressed;
    private static readonly PointerEventHandler s_released = OnPointerReleased;

    // One pointer at a time, so the click count is shared by all blocks.
    private static RichTextBlock? s_block;
    private static ulong s_lastTime;
    private static Point s_lastPoint;
    private static int s_clicks;
    private static TextPointer? s_pending;

    public static bool GetIsEnabled(RichTextBlock block) => (bool)block.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(RichTextBlock block, bool value) => block.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBlock block)
        {
            return;
        }
        // RichTextBlock handles the press and release itself for its own selection.
        block.RemoveHandler(UIElement.PointerPressedEvent, s_pressed);
        block.RemoveHandler(UIElement.PointerReleasedEvent, s_released);
        if ((bool)e.NewValue)
        {
            block.AddHandler(UIElement.PointerPressedEvent, s_pressed, true);
            block.AddHandler(UIElement.PointerReleasedEvent, s_released, true);
        }
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var block = (RichTextBlock)sender;
        PointerPoint point = e.GetCurrentPoint(block);
        if (!point.Properties.IsLeftButtonPressed || !block.IsTextSelectionEnabled)
        {
            s_clicks = 0;
            return;
        }

        ulong maxGap = Native.GetDoubleClickTime() * 1000UL;
        double maxX = Native.GetSystemMetrics(Native.SM_CXDOUBLECLK) / 2.0;
        double maxY = Native.GetSystemMetrics(Native.SM_CYDOUBLECLK) / 2.0;
        bool repeated = block == s_block
            && point.Timestamp - s_lastTime <= maxGap
            && Math.Abs(point.Position.X - s_lastPoint.X) <= maxX
            && Math.Abs(point.Position.Y - s_lastPoint.Y) <= maxY;
        s_clicks = repeated ? s_clicks + 1 : 1;
        s_block = block;
        s_lastTime = point.Timestamp;
        s_lastPoint = point.Position;

        s_pending = s_clicks == 3 ? block.GetPositionFromPoint(point.Position) : null;
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var block = (RichTextBlock)sender;
        TextPointer? at = s_pending;
        s_pending = null;
        if (at is null || block != s_block)
        {
            return;
        }
        // RichTextBlock resets the selection when the button comes up, after
        // this handler; select once it is done.
        block.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => SelectParagraph(block, at));
    }

    private static void SelectParagraph(RichTextBlock block, TextPointer at)
    {
        // The paragraph of the block that holds the pointer, start and end
        // moved in to the nearest line breaks around it.
        Paragraph? paragraph = block.Blocks.OfType<Paragraph>()
            .FirstOrDefault(p => p.ContentStart.Offset <= at.Offset && at.Offset <= p.ContentEnd.Offset);
        if (paragraph is null)
        {
            return;
        }
        TextPointer start = paragraph.ContentStart;
        TextPointer end = paragraph.ContentEnd;
        bool endFound = false;
        foreach (Inline inline in Flatten(paragraph.Inlines))
        {
            if (inline is LineBreak lineBreak)
            {
                if (lineBreak.ContentStart.Offset < at.Offset)
                {
                    start = lineBreak.ContentEnd;
                }
                else
                {
                    end = lineBreak.ContentStart;
                    endFound = true;
                }
            }
            else if (inline is Run run)
            {
                string text = run.Text;
                for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
                {
                    if (run.ContentStart.Offset + i < at.Offset)
                    {
                        start = run.ContentStart.GetPositionAtOffset(i + 1, LogicalDirection.Forward);
                    }
                    else
                    {
                        end = run.ContentStart.GetPositionAtOffset(i, LogicalDirection.Backward);
                        endFound = true;
                        break;
                    }
                }
            }
            if (endFound)
            {
                break;
            }
        }
        block.Select(start, end);
    }

    private static IEnumerable<Inline> Flatten(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (Inline child in Flatten(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }
}
