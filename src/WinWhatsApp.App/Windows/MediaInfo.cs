using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>What Windows can tell about a file to send.</summary>
internal static partial class MediaInfo
{
    private static async partial Task<int> AudioSecondsAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        MusicProperties music = await file.Properties.GetMusicPropertiesAsync();
        return (int)Math.Round(music.Duration.TotalSeconds);
    }

    private static async partial Task<(int Width, int Height, int Seconds, byte[]? Thumbnail)> DescribeVideoAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        VideoProperties properties = await file.Properties.GetVideoPropertiesAsync();
        int width = (int)properties.Width;
        int height = (int)properties.Height;
        if (properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270)
        {
            (width, height) = (height, width);
        }
        int seconds = (int)Math.Round(properties.Duration.TotalSeconds);
        byte[]? thumbnail = null;
        try
        {
            using StorageItemThumbnail frame = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 320, ThumbnailOptions.ResizeThumbnail);
            thumbnail = await ToJpegAsync(frame);
        }
        catch (Exception e)
        {
            // The size and length still go along.
            Log.Error($"Could not make a preview of the video {path}", e);
        }
        return (width, height, seconds, thumbnail);
    }

    public static async partial Task<IRandomAccessStream?> VideoFrameAsync(string path, uint size)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        return await file.GetThumbnailAsync(ThumbnailMode.VideosView, size);
    }

    public static async partial Task<(uint Width, uint Height)> PictureSizeAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
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

    public static partial bool HasClipboardImage(DataPackageView content) => content.Contains(StandardDataFormats.Bitmap);

    public static async partial Task<string?> SaveClipboardImageAsync(DataPackageView content)
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
