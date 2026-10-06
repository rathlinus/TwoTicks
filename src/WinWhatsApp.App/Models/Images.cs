using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace WinWhatsApp.App.Models;

/// <summary>Turns the images the helper hands over into something XAML can show.</summary>
internal static class Images
{
    /// <summary>An image from bytes, such as the small preview inside a message.</summary>
    public static BitmapImage? FromBytes(byte[]? data, int decodeWidth = 0)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }
        var image = new BitmapImage();
        if (decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
            image.DecodePixelType = DecodePixelType.Logical;
        }
        _ = SetSourceAsync(image, data);
        return image;
    }

    private static async Task SetSourceAsync(BitmapImage image, byte[] data)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(data);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }
        catch (Exception)
        {
            // A broken preview just stays empty.
        }
    }

    /// <summary>An image file, decoded at about the size it is shown.</summary>
    public static BitmapImage? FromFile(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }
        var image = new BitmapImage();
        if (decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
            image.DecodePixelType = DecodePixelType.Logical;
        }
        image.UriSource = new Uri(path);
        return image;
    }

    private static readonly Dictionary<string, ImageSource> s_avatars = [];

    /// <summary>A profile picture; the same file gives the same image.</summary>
    public static ImageSource? Avatar(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (!s_avatars.TryGetValue(path, out ImageSource? image))
        {
            image = FromFile(path, 96);
            if (image is null)
            {
                return null;
            }
            s_avatars[path] = image;
        }
        return image;
    }
}
