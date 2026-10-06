using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Models;

/// <summary>A photo or video of a chat, as a square in its info.</summary>
public sealed class MediaTile(MessageData message)
{
    public MessageData Message { get; } = message;

    /// <summary>The photo itself once downloaded, otherwise the small preview that came with it.</summary>
    public ImageSource? Picture { get; } = message.Kind == "image" && File.Exists(message.Media?.Path)
        ? Images.FromFile(message.Media!.Path, 240)
        : Images.FromBytes(message.Media?.Thumb);

    public string DurationText { get; } = message.Kind == "gif" ? "GIF"
        : message.Media is { Seconds: > 0 } media ? Formatting.Duration(TimeSpan.FromSeconds(media.Seconds)) : "";

    public Visibility VideoVisibility => Message.Kind is "video" or "gif" ? Visibility.Visible : Visibility.Collapsed;
}
