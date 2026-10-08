using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using TwoTicks.Core;

namespace TwoTicks.App.Controls;

/// <summary>Emoji inside text on Windows: each one a picture in the line.</summary>
internal static partial class Emoji
{
    /// <summary>The sheets need the WebP codec that comes with Windows.</summary>
    private static partial bool CanDrawSheets()
    {
        bool canDecode = BitmapDecoder.GetDecoderInformationEnumerator().Any(d => d.CodecId == BitmapDecoder.WebpDecoderId);
        if (!canDecode)
        {
            Log.Info("No WebP codec: emoji come from the font");
        }
        return canDecode;
    }

    /// <summary>The emoji are pictures here, and what is around them is the text itself.</summary>
    public static partial string ToPlain(string? text) => text ?? "";

    public static partial void Fill(RichTextBlock block, Action<InlineCollection>? fill)
    {
        block.Blocks.Clear();
        if (fill is null)
        {
            return;
        }
        var paragraph = new Paragraph();
        fill(paragraph.Inlines);
        block.Blocks.Add(paragraph);
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

    public static partial void AddText(InlineCollection inlines, string text, double fontSize, Action<Run>? styleRun)
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
