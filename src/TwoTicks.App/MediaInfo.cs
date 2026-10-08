using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>
/// What the app finds out about a file to send, which the helper cannot find
/// out itself: how long a recording is, how large a video's picture is and
/// what it shows. The system answers on Windows; elsewhere the app reads the
/// file and asks the tools the system has. Each has its part of this class.
/// </summary>
internal static partial class MediaInfo
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
                length = await AudioSecondsAsync(path);
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
            (width, height, seconds, thumbnail) = await DescribeVideoAsync(path);
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

    /// <summary>How long a recording is, in seconds.</summary>
    private static partial Task<int> AudioSecondsAsync(string path);

    /// <summary>The size of a video's picture as it is shown, its length, and a small JPEG of a frame.</summary>
    private static partial Task<(int Width, int Height, int Seconds, byte[]? Thumbnail)> DescribeVideoAsync(string path);

    /// <summary>A frame of a video as a picture about that many pixels large. Throws or returns null when there is none to be had.</summary>
    public static partial Task<IRandomAccessStream?> VideoFrameAsync(string path, uint size);

    /// <summary>The size of a photo as it is shown, turned the way its camera held it.</summary>
    public static partial Task<(uint Width, uint Height)> PictureSizeAsync(string path);

    /// <summary>Whether what was copied is a picture, as from a screenshot or an image in a browser.</summary>
    public static partial bool HasClipboardImage(DataPackageView content);

    /// <summary>Saves a pasted image as a PNG file to send, and returns the file.</summary>
    public static partial Task<string?> SaveClipboardImageAsync(DataPackageView content);
}
