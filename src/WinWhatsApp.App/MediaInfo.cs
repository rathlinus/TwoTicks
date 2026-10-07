using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>What Windows can tell about a file to send, which the helper cannot find out itself.</summary>
internal static class MediaInfo
{
    private static readonly HashSet<string> s_audioTypes = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".amr" };
    private static readonly HashSet<string> s_videoTypes = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".3gp" };

    public static bool IsVideo(string path) => s_videoTypes.Contains(Path.GetExtension(path));

    public static async Task<OutgoingMedia> DescribeAsync(string chat, string path, string? caption, string? replyTo, bool asDocument)
    {
        if (!asDocument && s_audioTypes.Contains(Path.GetExtension(path)))
        {
            // The length shows in the chat before the recording is played.
            int length = 0;
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                MusicProperties music = await file.Properties.GetMusicPropertiesAsync();
                length = (int)Math.Round(music.Duration.TotalSeconds);
            }
            catch (Exception e)
            {
                Log.Error($"Could not read the audio file {path}", e);
            }
            return new OutgoingMedia { Chat = chat, Path = path, Caption = caption, ReplyTo = replyTo, Seconds = length };
        }
        if (asDocument || !IsVideo(path))
        {
            return new OutgoingMedia { Chat = chat, Path = path, Caption = caption, ReplyTo = replyTo, AsDocument = asDocument };
        }

        int width = 0, height = 0, seconds = 0;
        byte[]? thumbnail = null;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            VideoProperties properties = await file.Properties.GetVideoPropertiesAsync();
            width = (int)properties.Width;
            height = (int)properties.Height;
            if (properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270)
            {
                (width, height) = (height, width);
            }
            seconds = (int)Math.Round(properties.Duration.TotalSeconds);
            using StorageItemThumbnail frame = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 320, ThumbnailOptions.ResizeThumbnail);
            thumbnail = await ToJpegAsync(frame);
        }
        catch (Exception e)
        {
            Log.Error($"Could not read the video {path}", e);
        }
        return new OutgoingMedia
        {
            Chat = chat, Path = path, Caption = caption, ReplyTo = replyTo,
            Width = width, Height = height, Seconds = seconds, Thumbnail = thumbnail,
        };
    }

    private static async Task<byte[]> ToJpegAsync(IRandomAccessStream source)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
            [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(0.7, Windows.Foundation.PropertyType.Single))]);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Saves a pasted image as a PNG file to send, and returns the file.</summary>
    public static async Task<string?> SaveClipboardImageAsync(DataPackageView content)
    {
        try
        {
            RandomAccessStreamReference reference = await content.GetBitmapAsync();
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            Directory.CreateDirectory(AppPaths.OutgoingFolder);
            string path = Path.Combine(AppPaths.OutgoingFolder, $"Pasted {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(AppPaths.OutgoingFolder);
            StorageFile file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.GenerateUniqueName);
            using (IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            return file.Path;
        }
        catch (Exception e)
        {
            Log.Error("Could not read the pasted image", e);
            return null;
        }
    }
}
