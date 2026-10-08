using System.Diagnostics;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// What the app finds out about a file to send on macOS and Linux. Lengths
/// and sizes it reads from the file itself; see <see cref="MediaFile"/>. A
/// frame of a video takes a decoder, which the app has none of, so it asks a
/// tool of the system: Quick Look on macOS, and on Linux whichever of
/// ffmpegthumbnailer, ffmpeg and GNOME's thumbnailer is installed. Without
/// one a video goes out without a preview.
/// </summary>
internal static partial class MediaInfo
{
    private static partial Task<int> AudioSecondsAsync(string path) =>
        Task.Run(() => (int)Math.Round(MediaFile.Read(path).Seconds));

    private static async partial Task<(int Width, int Height, int Seconds, byte[]? Thumbnail)> DescribeVideoAsync(string path)
    {
        MediaFacts facts = await Task.Run(() => MediaFile.Read(path));
        byte[]? thumbnail = await FrameJpegAsync(path, 320);
        return (facts.Width, facts.Height, (int)Math.Round(facts.Seconds), thumbnail);
    }

    public static async partial Task<IRandomAccessStream?> VideoFrameAsync(string path, uint size) =>
        await FrameJpegAsync(path, (int)size) is { } jpeg ? new MemoryStream(jpeg).AsRandomAccessStream() : null;

    public static partial Task<(uint Width, uint Height)> PictureSizeAsync(string path) => Task.Run(() =>
    {
        using SKCodec codec = SKCodec.Create(path) ?? throw new InvalidDataException("Not a picture: " + path);
        (uint width, uint height) = ((uint)codec.Info.Width, (uint)codec.Info.Height);
        // A camera held upright stores the photo on its side and says so.
        bool onItsSide = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        return onItsSide ? (height, width) : (width, height);
    });

    /// <summary>A frame of a video as a JPEG no larger than <paramref name="size"/> on its longer side, or null.</summary>
    private static async Task<byte[]?> FrameJpegAsync(string path, int size)
    {
        string folder = Path.Combine(Path.GetTempPath(), "winwhatsapp-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string? picture = OperatingSystem.IsMacOS() ? await QuickLookAsync(path, folder) : await LinuxFrameAsync(path, folder);
            if (picture is null)
            {
                return null;
            }
            return await Task.Run(() =>
            {
                using SKBitmap? frame = SKBitmap.Decode(picture);
                if (frame is null)
                {
                    return null;
                }
                double scale = Math.Min(1, (double)size / Math.Max(frame.Width, frame.Height));
                using SKBitmap small = frame.Resize(new SKImageInfo(Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale))), SKSamplingOptions.Default) ?? frame.Copy();
                using SKData jpeg = small.Encode(SKEncodedImageFormat.Jpeg, 70);
                return jpeg.ToArray();
            });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            Log.Info($"No frame of {path}: {e.Message}");
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Quick Look's own tool, which writes a PNG named after the file.</summary>
    private static async Task<string?> QuickLookAsync(string path, string folder)
    {
        await RunAsync("/usr/bin/qlmanage", "-t", "-s", "640", "-o", folder, path);
        return Directory.EnumerateFiles(folder, "*.png").FirstOrDefault();
    }

    private static async Task<string?> LinuxFrameAsync(string path, string folder)
    {
        string picture = Path.Combine(folder, "frame.png");
        // A second in, past the black a video often starts with; shorter ones give their first frame.
        string start = MediaFile.Read(path).Seconds > 2 ? "1" : "0";
        (string Program, string[] Arguments)[] tools =
        [
            ("ffmpegthumbnailer", ["-i", path, "-o", picture, "-s", "640", "-t", "10%"]),
            ("ffmpeg", ["-y", "-loglevel", "error", "-ss", start, "-i", path, "-frames:v", "1", picture]),
            ("totem-video-thumbnailer", ["-s", "640", path, picture]),
        ];
        foreach ((string program, string[] arguments) in tools)
        {
            try
            {
                await RunAsync(program, arguments);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed.
                continue;
            }
            if (File.Exists(picture) && new FileInfo(picture).Length > 0)
            {
                return picture;
            }
        }
        return null;
    }

    private static async Task RunAsync(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException(program + " did not start");
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            _ = process.StandardOutput.ReadToEndAsync(patience.Token);
            _ = process.StandardError.ReadToEndAsync(patience.Token);
            await process.WaitForExitAsync(patience.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
    }

    /// <summary>
    /// The bytes of what was copied, of one kind. Uno fetches them, but gives
    /// up on what is larger than the X server passes in one piece, which is
    /// most screenshots. Then xclip is asked, where it is installed.
    /// </summary>
    private static async Task<byte[]> CopiedFileAsync(DataPackageView content, string kind)
    {
        try
        {
            if (await content.GetDataAsync(kind) is byte[] file)
            {
                return file;
            }
        }
        catch (InvalidOperationException) when (OperatingSystem.IsLinux())
        {
        }
        var start = new ProcessStartInfo("xclip") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in (string[])["-selection", "clipboard", "-t", kind, "-o"])
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("xclip did not start");
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var output = new MemoryStream();
        _ = process.StandardError.ReadToEndAsync(patience.Token);
        await process.StandardOutput.BaseStream.CopyToAsync(output, patience.Token);
        await process.WaitForExitAsync(patience.Token);
        return output.ToArray();
    }

    // A copied picture is offered as a bitmap, or on Linux under the name of its kind of file.
    private static readonly string[] s_pictureKinds = ["image/png", "image/jpeg"];

    public static partial bool HasClipboardImage(DataPackageView content) =>
        content.Contains(StandardDataFormats.Bitmap) || s_pictureKinds.Any(content.Contains);

    public static async partial Task<string?> SaveClipboardImageAsync(DataPackageView content)
    {
        try
        {
            using var bytes = new MemoryStream();
            if (s_pictureKinds.FirstOrDefault(content.Contains) is { } kind)
            {
                bytes.Write(await CopiedFileAsync(content, kind));
            }
            else
            {
                RandomAccessStreamReference reference = await content.GetBitmapAsync();
                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
                await stream.AsStreamForRead().CopyToAsync(bytes);
            }
            bytes.Position = 0;
            using SKBitmap? bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
            {
                return null;
            }

            Directory.CreateDirectory(AppPaths.OutgoingFolder);
            string name = $"Pasted {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
            string path = Path.Combine(AppPaths.OutgoingFolder, name + ".png");
            for (int i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(AppPaths.OutgoingFolder, $"{name} ({i}).png");
            }
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, png.ToArray());
            return path;
        }
        catch (Exception e)
        {
            Log.Error("Could not read the pasted image", e);
            return null;
        }
    }
}
