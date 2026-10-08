using System.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using TwoTicks.Core;

namespace TwoTicks.App.Controls;

/// <summary>
/// Emoji inside text on macOS and Linux. Text cannot hold pictures here, so
/// WhatsApp's emoji are a font, made from the same sprite sheets; see
/// scripts/whatsapp-assets/emoji-font.py.
/// </summary>
/// <remarks>
/// An emoji is often several code points: a flag is two letters, a family is
/// people with joiners between them. The text engine shapes such parts one by
/// one, so the font's ligatures do not always join them. Shown text therefore
/// holds each emoji as one private code point, <see cref="PrivateUse"/> plus
/// its number in the list, which the font maps to the emoji. Text that leaves
/// the app, to the clipboard or to WhatsApp, is changed back with
/// <see cref="ToPlain"/>.
///
/// The text keeps its own font. The emoji font comes in through
/// <see cref="EmojiFontFallback"/>, as the font for what the text font lacks.
/// </remarks>
internal static partial class Emoji
{
    /// <summary>The first of the private code points that stand for emoji. The same number is in emoji-font.py.</summary>
    public const int PrivateUse = 0xF0000;

    public const string FontName = "WhatsApp Emoji";

    public static string FontFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "WhatsAppEmoji.ttf");

    /// <summary>Skia reads WebP itself. Without the font, text keeps the emoji of the system.</summary>
    private static partial bool CanDrawSheets()
    {
        if (!File.Exists(FontFile))
        {
            Log.Info("No emoji font: emoji in text come from the system");
            return false;
        }
        return true;
    }

    public static partial void Fill(TextBlock block, Action<InlineCollection>? fill)
    {
        block.Inlines.Clear();
        fill?.Invoke(block.Inlines);
    }

    public static partial void AddText(InlineCollection inlines, string text, double fontSize, Action<Run>? styleRun)
    {
        if (Set is null || !Set.ContainsEmoji(text))
        {
            var run = new Run { Text = text };
            styleRun?.Invoke(run);
            inlines.Add(run);
            return;
        }
        // Emoji that follow each other share a run.
        var emoji = new StringBuilder();
        void FlushEmoji()
        {
            if (emoji.Length > 0)
            {
                inlines.Add(new Run { Text = emoji.ToString() });
                emoji.Clear();
            }
        }
        foreach (EmojiSegment segment in Set.Split(text))
        {
            if (segment.IsEmoji)
            {
                emoji.Append(char.ConvertFromUtf32(PrivateUse + segment.Emoji));
            }
            else
            {
                FlushEmoji();
                var run = new Run { Text = segment.Text };
                styleRun?.Invoke(run);
                inlines.Add(run);
            }
        }
        FlushEmoji();
    }

    /// <summary>Whether a code point is one of the private ones that stand for emoji.</summary>
    public static bool IsPrivate(int codePoint) => Set is { } set && codePoint >= PrivateUse && codePoint < PrivateUse + set.Count;

    /// <summary>Text with each emoji WhatsApp has as its private code point, to show in a box to write in.</summary>
    public static string ToPrivate(string text)
    {
        if (Set is not { } set || !set.ContainsEmoji(text))
        {
            return text;
        }
        var result = new StringBuilder(text.Length);
        foreach (EmojiSegment segment in set.Split(text))
        {
            result.Append(segment.IsEmoji ? char.ConvertFromUtf32(PrivateUse + segment.Emoji) : segment.Text);
        }
        return result.ToString();
    }

    /// <summary>Shown text as it is written everywhere else: the private code points as the emoji they stand for.</summary>
    public static partial string ToPlain(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        if (Set is not { } set || !text.Contains('\uDB80'))
        {
            return text;
        }
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                && char.ConvertToUtf32(text[i], text[i + 1]) is int codePoint && IsPrivate(codePoint))
            {
                result.Append(set.TextOf(codePoint - PrivateUse));
                i++;
            }
            else
            {
                result.Append(text[i]);
            }
        }
        return result.ToString();
    }
}
