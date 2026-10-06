using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>Plays voice messages and audio, one at a time.</summary>
internal static class AudioPlayer
{
    private static MediaPlayer? s_player;
    private static MessageItem? s_current;
    private static DispatcherQueue? s_ui;

    public static MessageItem? Current => s_current;

    public static void Toggle(MessageItem item, string path, Action? onFirstPlay = null)
    {
        s_ui ??= DispatcherQueue.GetForCurrentThread();
        MediaPlayer player = Player();

        if (s_current == item)
        {
            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                player.Pause();
                item.IsPlaying = false;
            }
            else
            {
                player.Play();
                item.IsPlaying = true;
            }
            return;
        }

        Stop();
        s_current = item;
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        player.Play();
        item.IsPlaying = true;
        onFirstPlay?.Invoke();
    }

    /// <summary>Moves to a point in the playing recording, given as 0 to 1.</summary>
    public static void Seek(MessageItem item, double fraction)
    {
        if (s_current != item || s_player is null)
        {
            return;
        }
        TimeSpan length = s_player.PlaybackSession.NaturalDuration;
        if (length > TimeSpan.Zero)
        {
            s_player.PlaybackSession.Position = length * Math.Clamp(fraction, 0, 1);
        }
    }

    public static void Stop()
    {
        if (s_current is { } previous)
        {
            previous.IsPlaying = false;
            previous.Progress = 0;
            previous.AudioTime = "";
        }
        s_current = null;
        s_player?.Pause();
    }

    private static MediaPlayer Player()
    {
        if (s_player is not null)
        {
            return s_player;
        }
        s_player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Communications };
        s_player.PlaybackSession.PositionChanged += (session, _) =>
        {
            TimeSpan position = session.Position;
            TimeSpan length = session.NaturalDuration;
            s_ui?.TryEnqueue(() =>
            {
                if (s_current is { } item)
                {
                    item.Progress = length > TimeSpan.Zero ? position / length * 100 : 0;
                    item.AudioTime = Formatting.Duration(position);
                }
            });
        };
        s_player.MediaEnded += (_, _) => s_ui?.TryEnqueue(Stop);
        s_player.MediaFailed += (_, e) =>
        {
            Log.Error($"Playback failed: {e.Error} {e.ErrorMessage}");
            s_ui?.TryEnqueue(() =>
            {
                Stop();
                App.Current.Session?.ShowError("This recording can't be played. Windows may be missing the Web Media Extensions from the Microsoft Store.");
            });
        };
        return s_player;
    }
}
