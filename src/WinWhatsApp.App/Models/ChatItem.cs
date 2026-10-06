using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Models;

/// <summary>One row of the chat list.</summary>
public sealed class ChatItem : Observable
{
    private ChatData _data;
    private string _name = "";
    private string _time = "";
    private string? _previewGlyph;
    private string _previewText = "";
    private string _previewPrefix = "";
    private int _status = -2;
    private int _unread;
    private bool _markedUnread;
    private bool _isMuted;
    private bool _isPinned;
    private string? _typing;
    private ImageSource? _avatar;
    private string? _avatarPath;
    private string? _draft;

    public ChatItem(ChatData data)
    {
        _data = data;
        Jid = data.Jid;
        Update(data);
    }

    public string Jid { get; }
    public ChatData Data => _data;
    public bool IsGroup => _data.Group;
    public bool IsArchived => _data.Archived;
    public bool IsReadOnly => _data.ReadOnly;
    public long Ts => _data.Ts;
    public long Pinned => _data.Pinned;

    /// <summary>Whether the avatar was asked for in this session.</summary>
    public bool AvatarRequested { get; set; }

    public string Name { get => _name; private set { if (Set(ref _name, value)) { OnPropertyChanged(nameof(Initials)); } } }
    public string Initials => Formatting.Initials(_name);
    public string Time { get => _time; private set => Set(ref _time, value); }

    public string? PreviewGlyph
    {
        get => _previewGlyph;
        private set { if (Set(ref _previewGlyph, value)) { OnPropertyChanged(nameof(PreviewGlyphVisibility)); } }
    }

    public Visibility PreviewGlyphVisibility => _previewGlyph is null || _typing is not null ? Visibility.Collapsed : Visibility.Visible;
    public string PreviewText { get => _previewText; private set { if (Set(ref _previewText, value)) { OnPropertyChanged(nameof(PreviewLine)); } } }
    public string PreviewPrefix { get => _previewPrefix; private set { if (Set(ref _previewPrefix, value)) { OnPropertyChanged(nameof(PreviewLine)); } } }

    /// <summary>The line under the name: who wrote last, in groups, and what.</summary>
    public string PreviewLine => _previewPrefix + _previewText;

    /// <summary>The ticks of the last message when it is one's own, or -2 for none.</summary>
    public int Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) { OnPropertyChanged(nameof(TicksVisibility)); } }
    }

    public Visibility TicksVisibility => _status >= -1 && _typing is null ? Visibility.Visible : Visibility.Collapsed;

    public int Unread
    {
        get => _unread;
        private set
        {
            if (Set(ref _unread, value))
            {
                OnPropertyChanged(nameof(UnreadText));
                OnBadgeChanged();
            }
        }
    }

    public bool MarkedUnread { get => _markedUnread; private set { if (Set(ref _markedUnread, value)) { OnBadgeChanged(); } } }
    public bool HasUnread => _unread > 0 || _markedUnread;
    public string UnreadText => _unread > 0 ? (_unread > 999 ? "999+" : _unread.ToString()) : "";
    public bool IsMuted { get => _isMuted; private set { if (Set(ref _isMuted, value)) { OnBadgeChanged(); OnPropertyChanged(nameof(MutedVisibility)); } } }
    public Visibility MutedVisibility => _isMuted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => HasUnread && !_isMuted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MutedBadgeVisibility => HasUnread && _isMuted ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPinned { get => _isPinned; private set { if (Set(ref _isPinned, value)) { OnPropertyChanged(nameof(PinVisibility)); } } }
    public Visibility PinVisibility => _isPinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The time and name go bold and green while the chat is unread.</summary>
    public Windows.UI.Text.FontWeight NameWeight => HasUnread ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    private void OnBadgeChanged()
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(BadgeVisibility));
        OnPropertyChanged(nameof(MutedBadgeVisibility));
        OnPropertyChanged(nameof(NameWeight));
        OnPropertyChanged(nameof(TimeUnreadVisibility));
        OnPropertyChanged(nameof(TimeReadVisibility));
    }

    public Visibility TimeUnreadVisibility => HasUnread && !_isMuted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TimeReadVisibility => HasUnread && !_isMuted ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"typing…" while someone types, shown instead of the last message.</summary>
    public string? Typing
    {
        get => _typing;
        set
        {
            if (Set(ref _typing, value))
            {
                OnPropertyChanged(nameof(TypingVisibility));
                OnPropertyChanged(nameof(PreviewVisibility));
                OnPropertyChanged(nameof(PreviewGlyphVisibility));
                OnPropertyChanged(nameof(TicksVisibility));
            }
        }
    }

    public Visibility TypingVisibility => _typing is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PreviewVisibility => _typing is null ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Avatar { get => _avatar; private set => Set(ref _avatar, value); }

    public string? AvatarPath
    {
        get => _avatarPath;
        set
        {
            if (_avatarPath != value)
            {
                _avatarPath = value;
                Avatar = Images.Avatar(value);
            }
        }
    }

    /// <summary>A message typed but not sent, kept while switching chats.</summary>
    public string? Draft
    {
        get => _draft;
        set
        {
            if (Set(ref _draft, value))
            {
                Refresh();
            }
        }
    }

    public void Update(ChatData data)
    {
        _data = data;
        Name = string.IsNullOrEmpty(data.Name) ? data.Jid.Split('@')[0] : data.Name;
        Unread = data.Unread;
        MarkedUnread = data.MarkedUnread;
        IsMuted = data.IsMuted(DateTimeOffset.Now);
        IsPinned = data.Pinned > 0;
        if (!string.IsNullOrEmpty(data.Avatar))
        {
            AvatarPath = data.Avatar;
        }
        Refresh();
    }

    /// <summary>Updates what depends on the clock, such as "Yesterday".</summary>
    public void Refresh()
    {
        Time = Formatting.ChatListTime(_data.Ts, DateTime.Now);
        if (!string.IsNullOrEmpty(_draft))
        {
            PreviewPrefix = "Draft: ";
            PreviewGlyph = null;
            PreviewText = _draft.ReplaceLineEndings(" ");
            Status = -2;
            return;
        }

        LastMessageData? last = _data.Last;
        if (last is null)
        {
            PreviewPrefix = "";
            PreviewGlyph = null;
            PreviewText = "";
            Status = -2;
            return;
        }
        Preview preview = MessagePreview.Describe(last.Kind, last.Text, last.Name, last.Seconds, last.FromMe);
        PreviewGlyph = preview.Glyph;
        PreviewText = preview.Text;
        bool showSender = last.Kind is not ("system" or "revoked");
        PreviewPrefix = showSender && _data.Group && !last.FromMe && !string.IsNullOrEmpty(last.SenderName) ? last.SenderName + ": " : "";
        Status = last.FromMe && showSender ? last.Status : -2;
    }
}
