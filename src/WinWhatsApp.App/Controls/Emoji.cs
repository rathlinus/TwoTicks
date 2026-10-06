using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// Emoji as WhatsApp draws them, from its sprite sheets, in place of the
/// emoji of the Windows font.
/// </summary>
internal static class Emoji
{
    private static BitmapImage?[] s_sheets = [];

    /// <summary>WhatsApp's emoji, or null when they cannot be shown and the font's are used.</summary>
    public static EmojiSet? Set { get; private set; }

    /// <summary>Reads the emoji list. The sheets are WebP, which needs the WebP codec that comes with Windows.</summary>
    public static void Load()
    {
        try
        {
            bool canDecode = BitmapDecoder.GetDecoderInformationEnumerator().Any(d => d.CodecId == BitmapDecoder.WebpDecoderId);
            if (!canDecode)
            {
                Log.Info("No WebP codec: emoji come from the font");
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

    private static readonly Dictionary<int, byte[]> s_pictures = [];

    /// <summary>One emoji as a PNG of its own, cut from its sheet, for places that take a picture file: the box to write in.</summary>
    public static async Task<byte[]> PictureAsync(int index)
    {
        if (s_pictures.TryGetValue(index, out byte[]? known))
        {
            return known;
        }
        EmojiCell cell = Set!.CellOf(index);
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsApp", "Emoji", $"{cell.Sheet}.webp"));
        using IRandomAccessStream sheet = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(sheet);
        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds { X = (uint)cell.X, Y = (uint)cell.Y, Width = EmojiSet.CellSize, Height = EmojiSet.CellSize },
        };
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, EmojiSet.CellSize, EmojiSet.CellSize, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        byte[] png = new byte[output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(png);
        }
        s_pictures[index] = png;
        return png;
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

    /// <summary>
    /// Adds text with its emoji drawn as WhatsApp's. styleRun styles the runs of
    /// plain text; emoji are the given size and sit on the line like a letter.
    /// </summary>
    public static void AddText(InlineCollection inlines, string text, double fontSize, Action<Run>? styleRun = null)
    {
        if (Set is null || !Set.ContainsEmoji(text))
        {
            var run = new Run { Text = text };
            styleRun?.Invoke(run);
            inlines.Add(run);
            return;
        }
        double size = Math.Round(fontSize * 1.33);
        List<EmojiSegment> segments = Set.Split(text);
        // Only emoji: no letters to line up with, and moved down they hang out of their line.
        bool hasLetters = segments.Exists(s => !s.IsEmoji && !string.IsNullOrWhiteSpace(s.Text));
        foreach (EmojiSegment segment in segments)
        {
            if (segment.IsEmoji)
            {
                FrameworkElement emoji = Create(segment.Emoji, size);
                // The container puts it on the baseline; letters reach below that.
                if (hasLetters)
                {
                    emoji.RenderTransform = new TranslateTransform { Y = size * 0.2 };
                }
                emoji.Margin = new Thickness(1, 0, 1, 0);
                inlines.Add(new InlineUIContainer { Child = emoji });
            }
            else
            {
                var run = new Run { Text = segment.Text };
                styleRun?.Invoke(run);
                inlines.Add(run);
            }
        }
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
        block.Blocks.Clear();
        var paragraph = new Paragraph();
        Emoji.AddText(paragraph.Inlines, (string?)e.NewValue ?? "", block.FontSize);
        block.Blocks.Add(paragraph);
    }
}
