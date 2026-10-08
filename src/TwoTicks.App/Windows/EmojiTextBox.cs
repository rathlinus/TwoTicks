using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using TwoTicks.Core;

namespace TwoTicks.App.Controls;

/// <summary>
/// A box to write in that shows WhatsApp's emoji, as WhatsApp Web's does. A
/// TextBox can only draw the emoji of the Windows font, so this is a
/// RichEditBox that swaps each emoji for its picture, with the emoji as the
/// picture's text. Text goes in and comes out plain: formatting is WhatsApp's
/// *stars* and _underscores_, typed.
/// </summary>
public sealed partial class EmojiTextBox : RichEditBox
{
    private bool _converting;

    public EmojiTextBox()
    {
        FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["WhatsAppFont"];
        DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
        ClipboardCopyFormat = RichEditClipboardFormat.PlainText;
        TextChanged += OnTextChanged;
        Paste += OnPaste;
    }

    /// <summary>The text, with the emoji pictures as emoji and line breaks as \n.</summary>
    public string Text
    {
        get
        {
            Document.GetText(TextGetOptions.UseObjectText, out string text);
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        }
        set
        {
            // RichEdit ends paragraphs with \r.
            Document.SetText(TextSetOptions.None, (value ?? "").Replace('\n', '\r'));
            MoveToEnd();
        }
    }

    public void MoveToEnd() => Document.Selection.EndOf(TextRangeUnit.Story, false);

    /// <summary>Puts text where the cursor is, in place of what is selected, as if typed.</summary>
    public void Insert(string text) => Document.Selection.TypeText(text);

    private async void OnTextChanged(object sender, RoutedEventArgs e)
    {
        if (_converting || Emoji.Set is not { } set)
        {
            return;
        }
        // Pictures already in the text read as U+FFFC here; what is left are emoji typed or pasted.
        Document.GetText(TextGetOptions.None, out string raw);
        if (!set.ContainsEmoji(raw))
        {
            return;
        }

        var found = new List<(int Start, int Length, int Emoji, string Text)>();
        int position = 0;
        foreach (EmojiSegment segment in set.Split(raw))
        {
            if (segment.IsEmoji)
            {
                found.Add((position, segment.Text.Length, segment.Emoji, segment.Text));
            }
            position += segment.Text.Length;
        }
        var pictures = new Dictionary<int, byte[]>();
        try
        {
            foreach (int emoji in found.Select(f => f.Emoji).Distinct())
            {
                pictures[emoji] = await Emoji.PictureAsync(emoji);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not draw an emoji in the text box", ex);
            return;
        }
        // Typing went on while the pictures were made: the next change does it.
        Document.GetText(TextGetOptions.None, out string now);
        if (now != raw)
        {
            return;
        }

        _converting = true;
        try
        {
            int start = Document.Selection.StartPosition;
            int end = Document.Selection.EndPosition;
            int size = (int)Math.Round(FontSize * 1.33);
            // From the end, so the places of the ones before stay right.
            for (int i = found.Count - 1; i >= 0; i--)
            {
                (int at, int length, int emoji, string text) = found[i];
                ITextRange range = Document.GetRange(at, at + length);
                range.SetText(TextSetOptions.None, "");
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(pictures[emoji].AsBuffer());
                stream.Seek(0);
                range.InsertImage(size, size, (int)Math.Round(size * 0.8), VerticalCharacterAlignment.Baseline, text, stream);
                int shrink = length - 1;
                start = Shift(start, at, length, shrink);
                end = Shift(end, at, length, shrink);
            }
            Document.Selection.SetRange(start, end);
        }
        finally
        {
            _converting = false;
        }
    }

    /// <summary>Where a position goes when the text from at, length long, becomes one character.</summary>
    private static int Shift(int position, int at, int length, int shrink) =>
        position >= at + length ? position - shrink : position > at ? at + 1 : position;

    /// <summary>Pasted text comes in plain; files and pictures are left to whoever else handles the paste.</summary>
    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content = Clipboard.GetContent();
        if (e.Handled || !content.Contains(StandardDataFormats.Text) ||
            content.Contains(StandardDataFormats.StorageItems) || content.Contains(StandardDataFormats.Bitmap))
        {
            return;
        }
        e.Handled = true;
        string text = await content.GetTextAsync();
        Document.Selection.SetText(TextSetOptions.None, text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'));
        Document.Selection.Collapse(false);
    }
}
