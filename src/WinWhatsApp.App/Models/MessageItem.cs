using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Models;

/// <summary>The label between the messages of two days.</summary>
public sealed class DayItem(DateTime day)
{
    public DateTime Day { get; } = day.Date;
    public string Label => Formatting.DayLabel(Day, DateTime.Now);
}

/// <summary>The line above the first unread message when a chat opens.</summary>
public sealed class UnreadItem(int count)
{
    public string Label { get; } = count == 1 ? "1 unread message" : $"{count} unread messages";
}

/// <summary>One message in the conversation.</summary>
public sealed class MessageItem : Observable
{
    // Largest size of a photo or video in a bubble, in device-independent pixels.
    private const double MaxVisualWidth = 300;
    private const double MaxVisualHeight = 340;
    private const double MinVisualSide = 110;
    private const double StickerSide = 140;

    // Colours for the names of people in groups, readable on light and dark bubbles.
    private static readonly Color[] s_senderColors =
    [
        Color.FromArgb(255, 0xE2, 0x6A, 0x3B), Color.FromArgb(255, 0xC9, 0x4E, 0x92), Color.FromArgb(255, 0x5E, 0x75, 0xD6),
        Color.FromArgb(255, 0x1F, 0x9C, 0x86), Color.FromArgb(255, 0xB0, 0x82, 0x1E), Color.FromArgb(255, 0x8E, 0x5C, 0xD0),
        Color.FromArgb(255, 0xCC, 0x4B, 0x4B), Color.FromArgb(255, 0x1E, 0x90, 0xC8), Color.FromArgb(255, 0x4F, 0x9E, 0x38),
        Color.FromArgb(255, 0xD3, 0x5C, 0x78),
    ];

    private static readonly Dictionary<Color, SolidColorBrush> s_brushes = [];

    private MessageData _data;
    private bool _isFirstInRun = true;
    private ImageSource? _visual;
    private bool _visualIsFull;
    private bool _isBusy;
    private bool _isPlaying;
    private double _progress;
    private string _audioTime = "";
    private bool _isHighlighted;

    public MessageItem(MessageData data, bool isGroup)
    {
        _data = data;
        IsGroup = isGroup;
        Spans = [];
        Apply(data);
    }

    public MessageData Data => _data;
    public string Id => _data.Id;
    public string Chat => _data.Chat;
    public bool FromMe => _data.FromMe;
    public string Kind => _data.Kind;
    public long Ts => _data.Ts;
    public bool IsGroup { get; }
    public DateTime Day => Formatting.ToLocal(_data.Ts).Date;

    /// <summary>Which template shows the message. A change means the row has to be replaced.</summary>
    public string TemplateKey => _data.Kind == "system" ? "System"
        : _data.Kind == "sticker" || IsJumbo ? "Bare"
        : _data.FromMe ? "Outgoing" : "Incoming";

    public void Update(MessageData data)
    {
        string? oldPath = _data.Media?.Path;
        _data = data;
        Apply(data);
        if (_visualIsFull && data.Media?.Path != oldPath)
        {
            _visualIsFull = false;
        }
        if (_realized)
        {
            LoadVisual();
        }
    }

    private void Apply(MessageData data)
    {
        Spans = WhatsAppText.Parse(DisplayText(data), data.Mentions);
        IsJumbo = data.Kind == "text" && data.Quote is null && WhatsAppText.IsJumboEmoji(data.Text);
        TimeText = Formatting.MessageTime(data.Ts);

        // Everything else is computed from the data; tell the bindings it changed.
        OnPropertyChanged(string.Empty);
    }

    private static string DisplayText(MessageData data) => data.Kind switch
    {
        "revoked" => data.FromMe ? "You deleted this message" : "This message was deleted",
        "pending" => "Waiting for this message. This may take a while.",
        "viewonce" => "View once message. Open it on your phone.",
        "unsupported" => "This message type isn't supported yet. Open it on your phone.",
        "poll" => data.Text ?? "",
        _ => data.Text ?? "",
    };

    public IReadOnlyList<TextSpan> Spans { get; private set; }
    public bool IsJumbo { get; private set; }
    public string TimeText { get; private set; } = "";

    public bool HasText => Spans.Count > 0;
    public bool IsNotice => _data.Kind is "revoked" or "pending" or "viewonce" or "unsupported";
    public Visibility NoticeVisibility => IsNotice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => HasText && !IsNotice ? Visibility.Visible : Visibility.Collapsed;
    public string NoticeGlyph => _data.Kind switch
    {
        "revoked" => MessagePreview.DeletedGlyph,
        "pending" => MessagePreview.WaitingGlyph,
        "viewonce" => MessagePreview.ViewOnceGlyph,
        _ => MessagePreview.UnsupportedGlyph,
    };
    public string NoticeText => DisplayText(_data);
    public double JumboFontSize => _data.Text?.Length switch { <= 2 => 48, <= 5 => 40, _ => 32 };

    // ---- Runs of messages by the same sender ----

    /// <summary>The first message after another sender's, which gets a gap above it and the sender's name.</summary>
    public bool IsFirstInRun
    {
        get => _isFirstInRun;
        set
        {
            if (Set(ref _isFirstInRun, value))
            {
                OnPropertyChanged(nameof(RowPadding));
                OnPropertyChanged(nameof(SenderVisibility));
                OnPropertyChanged(nameof(BubbleCorners));
                OnPropertyChanged(nameof(TailInVisibility));
                OnPropertyChanged(nameof(TailOutVisibility));
            }
        }
    }

    public Thickness RowPadding => new(0, _isFirstInRun ? 10 : 2, 0, HasReactions ? 16 : 0);

    /// <summary>The first bubble of a run has WhatsApp's tail at its top corner, which is square there.</summary>
    public CornerRadius BubbleCorners => !_isFirstInRun ? new CornerRadius(8) : FromMe ? new CornerRadius(8, 0, 8, 8) : new CornerRadius(0, 8, 8, 8);

    public Visibility TailInVisibility => _isFirstInRun && !FromMe ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TailOutVisibility => _isFirstInRun && FromMe ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SenderVisibility => IsGroup && !FromMe && _isFirstInRun && !string.IsNullOrEmpty(_data.SenderName)
        ? Visibility.Visible : Visibility.Collapsed;

    public string SenderName => _data.SenderName ?? "";
    public Brush SenderBrush => BrushFor(_data.Sender);

    private static Brush BrushFor(string jid)
    {
        int hash = 0;
        foreach (char c in jid)
        {
            hash = hash * 31 + c;
        }
        Color color = s_senderColors[(int)((uint)hash % s_senderColors.Length)];
        if (!s_brushes.TryGetValue(color, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            s_brushes[color] = brush;
        }
        return brush;
    }

    public HorizontalAlignment Alignment => FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Thickness BubbleMargin => FromMe ? new Thickness(80, 0, 24, 0) : new Thickness(24, 0, 80, 0);
    public Visibility IncomingVisibility => FromMe ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OutgoingVisibility => FromMe ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Briefly set when the conversation jumps to this message.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (Set(ref _isHighlighted, value)) { OnPropertyChanged(nameof(HighlightOpacity)); } }
    }

    public double HighlightOpacity => _isHighlighted ? 1 : 0;

    /// <summary>
    /// The band behind a highlighted message reaches 2 pixels above and below
    /// the bubble, the gap between two bubbles of a run, and down past its reactions.
    /// </summary>
    public Thickness HighlightMargin => new(0, -2, 0, HasReactions ? -18 : -2);

    // ---- Ticks and time ----

    public int Status => _data.Status;
    public Visibility TicksVisibility => FromMe ? Visibility.Visible : Visibility.Collapsed;
    public bool IsFailed => FromMe && _data.Status == MessageStatus.Failed;
    public Visibility EditedVisibility => _data.Edited ? Visibility.Visible : Visibility.Collapsed;

    // ---- Reply ----

    public bool HasQuote => _data.Quote is not null;
    public string QuoteName => _data.Quote?.SenderName ?? "";
    public Brush QuoteBrush => _data.Quote is { FromMe: true } ? BrushFor("me") : BrushFor(_data.Quote?.Sender ?? "");
    public string QuoteText
    {
        get
        {
            QuoteData? quote = _data.Quote;
            if (quote is null)
            {
                return "";
            }
            return MessagePreview.Describe(quote.Kind, quote.Text, quote.Text, 0, quote.FromMe).Text;
        }
    }

    public string QuoteGlyph => _data.Quote is { } quote ? MessagePreview.Describe(quote.Kind, quote.Text, null, 0, false).Glyph ?? "" : "";
    public Visibility QuoteGlyphVisibility => QuoteGlyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    // ---- Reactions ----

    public bool HasReactions => _data.Reactions is { Count: > 0 };

    public string ReactionsText
    {
        get
        {
            if (_data.Reactions is not { Count: > 0 } reactions)
            {
                return "";
            }
            string emoji = string.Concat(reactions.Select(r => r.Emoji).Distinct().Take(3));
            return reactions.Count > 1 ? $"{emoji} {reactions.Count}" : emoji;
        }
    }

    public string ReactionsTooltip => _data.Reactions is { } reactions
        ? string.Join("\n", reactions.Select(r => $"{r.Emoji}  {r.Name ?? r.Sender}"))
        : "";

    public string? OwnReaction => _data.Reactions?.FirstOrDefault(r => r.FromMe)?.Emoji;

    // ---- Photos, videos and stickers ----

    public bool HasVisual => _data.Kind is "image" or "video" or "gif" || (_data.Kind == "location" && _data.Media?.Thumb is not null);
    public bool IsSticker => _data.Kind == "sticker";
    public bool IsPlayable => _data.Kind is "video" or "gif";
    public Visibility PlayVisibility => IsPlayable && !_isBusy ? Visibility.Visible : Visibility.Collapsed;
    public string DurationText => _data.Kind == "gif" ? "GIF" : _data.Media is { Seconds: > 0 } m ? Formatting.Duration(TimeSpan.FromSeconds(m.Seconds)) : "";
    public Visibility DurationVisibility => IsPlayable && DurationText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A photo or video without a caption has its time over the picture.</summary>
    public bool TimeOverVisual => (HasVisual || IsSticker) && !HasText;

    public Visibility TimeInTextVisibility => TimeOverVisual ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TimeOverVisualVisibility => TimeOverVisual ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Visual { get => _visual; private set => Set(ref _visual, value); }

    public double VisualWidth => VisualSize().Width;
    public double VisualHeight => VisualSize().Height;

    private (double Width, double Height) VisualSize()
    {
        if (IsSticker)
        {
            return (StickerSide, StickerSide);
        }
        double width = _data.Media?.Width ?? 0;
        double height = _data.Media?.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            return (MaxVisualWidth, MaxVisualWidth * 0.66);
        }
        double scale = Math.Min(MaxVisualWidth / width, MaxVisualHeight / height);
        width *= scale;
        height *= scale;
        return (Math.Max(width, MinVisualSide), Math.Max(height, MinVisualSide));
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(PlayVisibility));
                OnPropertyChanged(nameof(DownloadVisibility));
            }
        }
    }

    public bool IsDownloaded => !string.IsNullOrEmpty(_data.Media?.Path) && File.Exists(_data.Media.Path);
    public Visibility DownloadVisibility => !_isBusy && !IsDownloaded && (HasDocument || HasAudio) ? Visibility.Visible : Visibility.Collapsed;

    private bool _realized;

    /// <summary>Loads the picture when the row comes into view.</summary>
    public void Realize()
    {
        _realized = true;
        LoadVisual();
    }

    /// <summary>Lets go of the full-size picture when the row leaves the view.</summary>
    public void Unrealize()
    {
        _realized = false;
        if (_visualIsFull)
        {
            _visualIsFull = false;
            Visual = null;
        }
    }

    private void LoadVisual()
    {
        if (!HasVisual && !IsSticker && !HasLinkThumb)
        {
            return;
        }
        if (HasLinkThumb && LinkThumb is null)
        {
            LinkThumb = Images.FromBytes(_data.Link!.Thumb);
        }
        if (!HasVisual && !IsSticker)
        {
            return;
        }
        string? path = _data.Media?.Path;
        bool isImageFile = _data.Kind is "image" or "sticker" && !string.IsNullOrEmpty(path) && File.Exists(path);
        if (isImageFile && !_visualIsFull)
        {
            Visual = Images.FromFile(path, (int)VisualWidth);
            _visualIsFull = true;
        }
        else if (_visual is null && !_visualIsFull)
        {
            Visual = Images.FromBytes(_data.Media?.Thumb);
            if (Visual is null && IsPlayable && IsDownloaded)
            {
                _ = LoadVideoFrameAsync(path!);
            }
        }
    }

    /// <summary>Shows a frame of a downloaded video that came without a preview.</summary>
    private async Task LoadVideoFrameAsync(string path)
    {
        try
        {
            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using Windows.Storage.FileProperties.StorageItemThumbnail frame =
                await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 480);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(frame);
            if (_visual is null)
            {
                Visual = image;
            }
        }
        catch (Exception)
        {
            // Windows could not read the video; the box stays plain.
        }
    }

    // ---- Documents ----

    public bool HasDocument => _data.Kind == "document";
    public string DocumentName => _data.Media?.Name ?? "Document";

    public string DocumentInfo
    {
        get
        {
            MediaData? media = _data.Media;
            if (media is null)
            {
                return "";
            }
            var parts = new List<string>();
            string extension = Path.GetExtension(media.Name ?? "").TrimStart('.').ToUpperInvariant();
            if (extension.Length is > 0 and <= 5)
            {
                parts.Add(extension);
            }
            if (media.Pages > 0)
            {
                parts.Add(media.Pages == 1 ? "1 page" : $"{media.Pages} pages");
            }
            if (media.Size > 0)
            {
                parts.Add(Formatting.FileSize(media.Size));
            }
            return string.Join(" · ", parts);
        }
    }

    // ---- Voice messages and audio ----

    public bool HasAudio => _data.Kind is "voice" or "audio";
    public bool IsVoice => _data.Kind == "voice";
    public string AudioGlyph => IsVoice ? MessagePreview.MicrophoneGlyph : MessagePreview.AudioGlyph;

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) { OnPropertyChanged(nameof(PlayGlyph)); } }
    }

    public string PlayGlyph => _isPlaying ? "Pause" : "Play";

    /// <summary>How far the recording has played, from 0 to 100.</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    /// <summary>The length, or the position while it plays.</summary>
    public string AudioTime
    {
        get => _audioTime.Length > 0 ? _audioTime : _data.Media is { Seconds: > 0 } m ? Formatting.Duration(TimeSpan.FromSeconds(m.Seconds)) : "";
        set => Set(ref _audioTime, value);
    }

    // ---- Link previews and locations ----

    public bool HasLink => _data.Link is not null;
    public bool HasLinkThumb => _data.Link?.Thumb is { Length: > 0 };
    private ImageSource? _linkThumb;
    public ImageSource? LinkThumb { get => _linkThumb; private set => Set(ref _linkThumb, value); }
    public Visibility LinkThumbVisibility => HasLinkThumb ? Visibility.Visible : Visibility.Collapsed;
    public string LinkTitle => _data.Link?.Title ?? "";
    public string LinkDescription => _data.Link?.Description ?? "";
    public Visibility LinkDescriptionVisibility => string.IsNullOrEmpty(LinkDescription) ? Visibility.Collapsed : Visibility.Visible;

    public string LinkHost => Formatting.LinkHost(_data.Link?.Url ?? "");

    public bool IsLocation => _data.Kind == "location";

    public string? MapUrl => _data.Media is { } m && IsLocation
        ? FormattableString.Invariant($"https://www.google.com/maps/search/?api=1&query={m.Lat},{m.Lng}")
        : null;

    public Visibility LocationVisibility => IsLocation ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The text that Copy puts on the clipboard.</summary>
    public string CopyText => string.Concat(Spans.Select(s => s.Text));

    public bool CanEdit => FromMe && _data.Kind == "text" && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(_data.Ts) < TimeSpan.FromMinutes(20);
    public bool CanRevoke => FromMe && _data.Kind is not ("revoked" or "system" or "pending");
    public bool CanReact => _data.Kind is not ("revoked" or "system" or "pending" or "viewonce") && _data.Status != MessageStatus.Failed;
    public bool HasMedia => _data.Kind is "image" or "video" or "gif" or "voice" or "audio" or "document" or "sticker";
}
