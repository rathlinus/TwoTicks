using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// A box to write in that shows WhatsApp's emoji, as WhatsApp Web's does. The
/// emoji are a font here, so this is a TextBox that keeps each emoji as the
/// private code point the font draws; see <see cref="Emoji"/>. Text goes in
/// and comes out plain.
/// </summary>
public sealed partial class EmojiTextBox : TextBox
{
    private bool _converting;

    public EmojiTextBox()
    {
        TextChanged += OnTextChanged;
        // A class of its own does not get the look the app gives every
        // TextBox: Uno falls back to an older one, with a fill of its own that
        // the XAML around the box cannot take away.
        if (Application.Current.Resources.TryGetValue("DefaultTextBoxStyle", out object? style) && style is Style look)
        {
            Style = look;
        }
    }

    /// <summary>The text, with the emoji as emoji and line breaks as \n.</summary>
    public new string Text
    {
        get => Emoji.ToPlain(base.Text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        set
        {
            _converting = true;
            try
            {
                base.Text = Emoji.ToPrivate(value ?? "");
            }
            finally
            {
                _converting = false;
            }
            MoveToEnd();
        }
    }

    public void MoveToEnd()
    {
        SelectionStart = base.Text.Length;
        SelectionLength = 0;
    }

    /// <summary>Puts text where the cursor is, in place of what is selected, as if typed.</summary>
    public void Insert(string text)
    {
        string shown = base.Text;
        int start = Math.Clamp(SelectionStart, 0, shown.Length);
        int length = Math.Clamp(SelectionLength, 0, shown.Length - start);
        string inserted = Emoji.ToPrivate(text);
        _converting = true;
        try
        {
            base.Text = string.Concat(shown.AsSpan(0, start), inserted, shown.AsSpan(start + length));
        }
        finally
        {
            _converting = false;
        }
        SelectionStart = start + inserted.Length;
        SelectionLength = 0;
    }

    /// <summary>Emoji typed or pasted arrive as they are written; they become the font's code points.</summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_converting)
        {
            return;
        }
        string shown = base.Text;
        string converted = Emoji.ToPrivate(shown);
        if (converted == shown)
        {
            return;
        }
        int caret = Math.Clamp(SelectionStart, 0, shown.Length);
        int newCaret = Emoji.ToPrivate(shown[..caret]).Length;
        _converting = true;
        try
        {
            base.Text = converted;
        }
        finally
        {
            _converting = false;
        }
        SelectionStart = Math.Min(newCaret, converted.Length);
        SelectionLength = 0;
    }
}
