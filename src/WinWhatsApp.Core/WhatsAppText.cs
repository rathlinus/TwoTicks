using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinWhatsApp.Core;

[Flags]
public enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Mono = 8,
}

/// <summary>A run of message text with one style.</summary>
/// <param name="Link">The address to open, for a link.</param>
/// <param name="IsMention">An "@name" that refers to a person.</param>
public readonly record struct TextSpan(string Text, TextStyle Style, string? Link = null, bool IsMention = false);

/// <summary>
/// WhatsApp's text formatting: *bold*, _italic_, ~strikethrough~, `code` and
/// ```monospace``` blocks, plus links and mentions.
/// </summary>
/// <remarks>
/// A marker only counts at the edge of a word: it opens after a space,
/// punctuation or the start, before a character that is not a space, and closes
/// the same way round. That keeps snake_case_names and 2*3*4 as they are.
/// Markers other than ``` do not span lines.
/// </remarks>
public static partial class WhatsAppText
{
    private const string CodeFence = "```";

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"@(\d{5,})")]
    private static partial Regex MentionPattern();

    public static IReadOnlyList<TextSpan> Parse(string? text, IReadOnlyDictionary<string, string>? mentions = null)
    {
        var spans = new List<TextSpan>();
        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        int position = 0;
        while (position < text.Length)
        {
            int start = text.IndexOf(CodeFence, position, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }
            int end = text.IndexOf(CodeFence, start + CodeFence.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }
            ParseInline(text[position..start], TextStyle.None, spans, mentions);
            string code = text[(start + CodeFence.Length)..end];
            if (code.Length > 0)
            {
                spans.Add(new TextSpan(code, TextStyle.Mono));
            }
            else
            {
                spans.Add(new TextSpan(CodeFence + CodeFence, TextStyle.None));
            }
            position = end + CodeFence.Length;
        }
        ParseInline(text[position..], TextStyle.None, spans, mentions);
        return Merge(spans);
    }

    private readonly record struct Atom(int Start, int Length, string Text, string? Link, bool IsMention);

    private static void ParseInline(string text, TextStyle style, List<TextSpan> spans, IReadOnlyDictionary<string, string>? mentions)
    {
        if (text.Length == 0)
        {
            return;
        }

        List<Atom> atoms = FindAtoms(text, mentions);
        int atomIndex = 0;
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length > 0)
            {
                spans.Add(new TextSpan(plain.ToString(), style));
                plain.Clear();
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            while (atomIndex < atoms.Count && atoms[atomIndex].Start < i)
            {
                atomIndex++;
            }
            if (atomIndex < atoms.Count && atoms[atomIndex].Start == i)
            {
                Atom atom = atoms[atomIndex];
                Flush();
                spans.Add(new TextSpan(atom.Text, style, atom.Link, atom.IsMention));
                i += atom.Length;
                continue;
            }

            char c = text[i];
            TextStyle flag = MarkerStyle(c);
            if (flag != TextStyle.None && (style & flag) == 0 && CanOpen(text, i))
            {
                int close = FindClose(text, i, atoms);
                if (close > 0)
                {
                    Flush();
                    TextStyle inner = style | flag;
                    if (flag == TextStyle.Mono)
                    {
                        // Inline code is taken as it is, without formatting inside.
                        spans.Add(new TextSpan(text[(i + 1)..close], inner));
                    }
                    else
                    {
                        ParseInline(text[(i + 1)..close], inner, spans, mentions);
                    }
                    i = close + 1;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }
        Flush();
    }

    private static TextStyle MarkerStyle(char c) => c switch
    {
        '*' => TextStyle.Bold,
        '_' => TextStyle.Italic,
        '~' => TextStyle.Strike,
        '`' => TextStyle.Mono,
        _ => TextStyle.None,
    };

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool CanOpen(string text, int i)
    {
        char marker = text[i];
        if (i > 0 && (!IsBoundary(text[i - 1]) || text[i - 1] == marker))
        {
            return false;
        }
        return i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != marker;
    }

    private static int FindClose(string text, int open, List<Atom> atoms)
    {
        char marker = text[open];
        int j = open + 2;
        while (j < text.Length)
        {
            int atom = atoms.FindIndex(a => a.Start == j);
            if (atom >= 0)
            {
                j += atoms[atom].Length;
                continue;
            }
            char c = text[j];
            if (c == '\n')
            {
                return -1;
            }
            if (c == marker && !char.IsWhiteSpace(text[j - 1]) && (j + 1 == text.Length || IsBoundary(text[j + 1])))
            {
                return j;
            }
            j++;
        }
        return -1;
    }

    private static List<Atom> FindAtoms(string text, IReadOnlyDictionary<string, string>? mentions)
    {
        var atoms = new List<Atom>();
        foreach (Match match in UrlPattern().Matches(text))
        {
            string url = TrimUrl(match.Value);
            if (url.Length < 5)
            {
                continue;
            }
            if (match.Index > 0 && (char.IsLetterOrDigit(text[match.Index - 1]) || text[match.Index - 1] == '@'))
            {
                continue;
            }
            string target = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            atoms.Add(new Atom(match.Index, url.Length, url, target, false));
        }
        if (mentions is { Count: > 0 })
        {
            foreach (Match match in MentionPattern().Matches(text))
            {
                if (mentions.TryGetValue(match.Groups[1].Value, out string? name) && !atoms.Exists(a => Overlaps(a, match.Index, match.Length)))
                {
                    atoms.Add(new Atom(match.Index, match.Length, "@" + name, null, true));
                }
            }
        }
        atoms.Sort((a, b) => a.Start.CompareTo(b.Start));
        return atoms;
    }

    private static bool Overlaps(Atom atom, int start, int length) =>
        start < atom.Start + atom.Length && atom.Start < start + length;

    /// <summary>Leaves punctuation that ends a sentence out of a link, but keeps a closing parenthesis that has its opening one in the link.</summary>
    private static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            char last = url[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*' or '_' or '~')
            {
                url = url[..^1];
            }
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
            {
                url = url[..^1];
            }
            else
            {
                break;
            }
        }
        return url;
    }

    private static List<TextSpan> Merge(List<TextSpan> spans)
    {
        var merged = new List<TextSpan>(spans.Count);
        foreach (TextSpan span in spans)
        {
            if (merged.Count > 0)
            {
                TextSpan last = merged[^1];
                if (last.Style == span.Style && last.Link is null && span.Link is null && !last.IsMention && !span.IsMention)
                {
                    merged[^1] = last with { Text = last.Text + span.Text };
                    continue;
                }
            }
            merged.Add(span);
        }
        return merged;
    }

    /// <summary>
    /// Whether a message is only a few emoji, which WhatsApp shows large and
    /// without a bubble.
    /// </summary>
    public static bool IsJumboEmoji(string? text, int maxCount = 3)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            return false;
        }
        int count = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text.Trim());
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            if (element.Length == 1 && char.IsWhiteSpace(element[0]))
            {
                continue;
            }
            if (!IsEmojiElement(element) || ++count > maxCount)
            {
                return false;
            }
        }
        return count > 0;
    }

    private static bool IsEmojiElement(string element)
    {
        bool sawPictograph = false;
        foreach (Rune rune in element.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is 0x200D or 0xFE0F or 0x20E3 || value is >= 0x1F3FB and <= 0x1F3FF || value is >= 0xE0020 and <= 0xE007F)
            {
                // Joiners, variation selectors, keycaps, skin tones and tag characters.
                continue;
            }
            if (value is >= 0x1F000 and <= 0x1FAFF || value is >= 0x2600 and <= 0x27BF || value is >= 0x2300 and <= 0x23FF ||
                value is >= 0x2B00 and <= 0x2BFF || value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 ||
                value is >= 0x2190 and <= 0x21FF || value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                sawPictograph = true;
                continue;
            }
            if (value is >= '0' and <= '9' or '#' or '*' && element.Contains('⃣'))
            {
                continue;
            }
            return false;
        }
        return sawPictograph;
    }
}
