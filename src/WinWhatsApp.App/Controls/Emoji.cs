using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// Emoji as WhatsApp draws them, from its sprite sheets, in place of the
/// emoji of the Windows font.
/// </summary>
internal static partial class Emoji
{
    private static BitmapImage?[] s_sheets = [];

    /// <summary>WhatsApp's emoji, or null when they cannot be shown and the font's are used.</summary>
    public static EmojiSet? Set { get; private set; }

    /// <summary>Reads the emoji list, when this system can draw the sheets.</summary>
    public static void Load()
    {
        try
        {
            if (!CanDrawSheets())
            {
                return;
            }
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsApp", "emoji.json");
            using FileStream stream = File.OpenRead(path);
            Set = EmojiSet.Load(stream);
            s_sheets = new BitmapImage?[Set.CellOf(Set.Count - 1).Sheet + 1];
            string names = Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsApp", "emoji-names.json");
            if (File.Exists(names))
            {
                using FileStream namesStream = File.OpenRead(names);
                Set.LoadNames(namesStream);
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not load the emoji", e);
            Set = null;
        }
    }

    /// <summary>Whether the sprite sheets, which are WebP, can be shown here.</summary>
    private static partial bool CanDrawSheets();

    /// <summary>
    /// Adds text with its emoji drawn as WhatsApp's. styleRun styles the runs of
    /// plain text; emoji are a third taller than the text and sit on the line like a letter.
    /// </summary>
    public static partial void AddText(InlineCollection inlines, string text, double fontSize, Action<Run>? styleRun = null);

    /// <summary>Text taken from what a block shows, such as a selection, as it is written: with its emoji as emoji.</summary>
    public static partial string ToPlain(string? text);

    /// <summary>Replaces what a block of text shows with what fill adds; with no fill, with nothing.</summary>
    public static partial void Fill(RichTextBlock block, Action<InlineCollection>? fill);

    private static BitmapImage Sheet(int number)
    {
        BitmapImage? sheet = s_sheets[number];
        if (sheet is null)
        {
            sheet = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsApp", "Emoji", $"{number}.webp")));
            s_sheets[number] = sheet;
        }
        return sheet;
    }

    /// <summary>One emoji, size pixels square: its sheet, moved so the emoji's cell shows through a clip.</summary>
    public static FrameworkElement Create(int index, double size)
    {
        EmojiCell cell = Set!.CellOf(index);
        double scale = size / EmojiSet.CellSize;
        var image = new Image
        {
            Source = Sheet(cell.Sheet),
            Width = cell.SheetWidth * scale,
            Height = cell.SheetHeight * scale,
            Stretch = Stretch.Fill,
        };
        Canvas.SetLeft(image, -cell.X * scale);
        Canvas.SetTop(image, -cell.Y * scale);
        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, size, size) },
            IsHitTestVisible = false,
        };
        canvas.Children.Add(image);
        return canvas;
    }

}

/// <summary>Plain text with WhatsApp's emoji, for names and single lines such as the last message in the chat list.</summary>
public static class EmojiText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(EmojiText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(RichTextBlock block) => (string?)block.GetValue(TextProperty);

    public static void SetText(RichTextBlock block, string? value) => block.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBlock block)
        {
            return;
        }
        Emoji.Fill(block, inlines => Emoji.AddText(inlines, (string?)e.NewValue ?? "", block.FontSize));
    }
}
