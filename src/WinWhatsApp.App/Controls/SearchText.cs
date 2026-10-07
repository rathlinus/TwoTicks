using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

/// <summary>A search result's text with WhatsApp's emoji, and what matched the search in the match brush.</summary>
public static class SearchText
{
    // Registered as object: XAML knows no type for a plain .NET class.
    public static readonly DependencyProperty SnippetProperty = DependencyProperty.RegisterAttached(
        "Snippet", typeof(object), typeof(SearchText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty MatchBrushProperty = DependencyProperty.RegisterAttached(
        "MatchBrush", typeof(Brush), typeof(SearchText), new PropertyMetadata(null, OnChanged));

    public static SearchSnippet? GetSnippet(RichTextBlock block) => (SearchSnippet?)block.GetValue(SnippetProperty);
    public static void SetSnippet(RichTextBlock block, SearchSnippet? value) => block.SetValue(SnippetProperty, value);

    public static Brush? GetMatchBrush(RichTextBlock block) => (Brush?)block.GetValue(MatchBrushProperty);
    public static void SetMatchBrush(RichTextBlock block, Brush? value) => block.SetValue(MatchBrushProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBlock block)
        {
            return;
        }
        if (GetSnippet(block) is not { } snippet)
        {
            Emoji.Fill(block, null);
            return;
        }
        Brush? brush = GetMatchBrush(block);
        Emoji.Fill(block, inlines =>
        {
            int end = snippet.MatchStart + snippet.MatchLength;
            Emoji.AddText(inlines, snippet.Text[..snippet.MatchStart], block.FontSize);
            Emoji.AddText(inlines, snippet.Text[snippet.MatchStart..end], block.FontSize, run =>
            {
                run.FontWeight = FontWeights.SemiBold;
                if (brush is not null)
                {
                    run.Foreground = brush;
                }
            });
            Emoji.AddText(inlines, snippet.Text[end..], block.FontSize);
        });
    }
}
