using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// Fills a RichTextBlock with formatted message text: bold, italic,
/// strikethrough, monospace, links, mentions and WhatsApp's emoji.
/// </summary>
public static class RichText
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(IReadOnlyList<TextSpan>), typeof(RichText), new PropertyMetadata(null, OnChanged));

    private static readonly FontFamily s_mono = (FontFamily)Application.Current.Resources["MonoFont"];
    private static readonly SolidColorBrush s_mention = new(Color.FromArgb(255, 0x1F, 0x9B, 0xD1));

    public static IReadOnlyList<TextSpan>? GetSpans(RichTextBlock block) => (IReadOnlyList<TextSpan>?)block.GetValue(SpansProperty);
    public static void SetSpans(RichTextBlock block, IReadOnlyList<TextSpan>? value) => block.SetValue(SpansProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBlock block)
        {
            Build(block, GetSpans(block));
        }
    }

    private static void Build(RichTextBlock block, IReadOnlyList<TextSpan>? spans)
    {
        Emoji.Fill(block, spans is null ? null : inlines => Build(inlines, spans, block.FontSize));
    }

    private static void Build(InlineCollection inlines, IReadOnlyList<TextSpan> spans, double fontSize)
    {
        foreach (TextSpan span in spans)
        {
            void Style(Run run)
            {
                if (span.Style.HasFlag(TextStyle.Bold))
                {
                    run.FontWeight = FontWeights.SemiBold;
                }
                if (span.Style.HasFlag(TextStyle.Italic))
                {
                    run.FontStyle = FontStyle.Italic;
                }
                if (span.Style.HasFlag(TextStyle.Strike))
                {
                    run.TextDecorations = TextDecorations.Strikethrough;
                }
                if (span.Style.HasFlag(TextStyle.Mono))
                {
                    run.FontFamily = s_mono;
                }
                if (span.IsMention)
                {
                    run.Foreground = s_mention;
                    run.FontWeight = FontWeights.SemiBold;
                }
            }

            if (span.Link is not null && Uri.TryCreate(span.Link, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
            {
                // A link holds only runs of text, so its emoji stay those of the font.
                var run = new Run { Text = span.Text };
                Style(run);
                var link = new Hyperlink { NavigateUri = uri, UnderlineStyle = UnderlineStyle.None };
                ToolTipService.SetToolTip(link, uri.ToString());
                link.Inlines.Add(run);
                inlines.Add(link);
            }
            else
            {
                Emoji.AddText(inlines, span.Text, fontSize, Style);
            }
        }
    }
}
