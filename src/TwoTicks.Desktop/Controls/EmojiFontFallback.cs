using Microsoft.UI.Xaml.Documents.TextFormatting;
using Windows.UI.Text;

namespace TwoTicks.App.Controls;

/// <summary>
/// Tells the text engine to draw emoji with WhatsApp's emoji font wherever the
/// font of a text has none: in the boxes to write in, whose text is one font,
/// and in text that does not go through <see cref="Emoji.AddText"/>.
/// </summary>
/// <remarks>
/// It is the only font the app adds this way. Characters neither font has
/// come from the fonts of the system, never from the network.
/// </remarks>
internal sealed class EmojiFontFallback : IFontFallbackService
{
    private HashSet<int>? _emoji;

    public Task<string?> GetFontFamilyForCodepoint(int codepoint) =>
        Task.FromResult(Covers(codepoint) ? Emoji.FontName : null);

    public Task<Stream?> GetFontStreamForFontFamily(string fontFamily, FontWeight weight, FontStretch stretch, FontStyle style) =>
        Task.FromResult<Stream?>(fontFamily == Emoji.FontName && File.Exists(Emoji.FontFile) ? File.OpenRead(Emoji.FontFile) : null);

    private bool Covers(int codepoint)
    {
        if (Emoji.Set is not { } set)
        {
            return false;
        }
        if (Emoji.IsPrivate(codepoint))
        {
            return true;
        }
        if (_emoji is null)
        {
            // What emoji are written with, apart from the digits and signs of keycaps.
            var points = new HashSet<int>();
            for (int i = 0; i < set.Count; i++)
            {
                string text = set.TextOf(i);
                for (int at = 0; at < text.Length; at += char.IsSurrogatePair(text, at) ? 2 : 1)
                {
                    int point = char.ConvertToUtf32(text, at);
                    if (point >= 0x2000)
                    {
                        points.Add(point);
                    }
                }
            }
            _emoji = points;
        }
        return _emoji.Contains(codepoint);
    }
}
