using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinWhatsApp.App.Controls;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace WinWhatsApp.App.Views;

/// <summary>The open chat: its header, the messages and the box to write in.</summary>
public sealed partial class ConversationView : UserControl
{
    private static readonly string[] s_quickReactions = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    // How long a pin lasts, as WhatsApp offers it.
    private static readonly long[] s_pinDurations = [86_400, 604_800, 2_592_000];

    private readonly HashSet<string> _autoDownloads = [];
    private readonly DispatcherQueueTimer _highlightTimer;
    private readonly DispatcherQueueTimer _linkTimer;
    private MessageItem? _highlighted;
    private string? _linkUrl;
    private LinkData? _link;
    private string? _removedLinkUrl;
    private int _linkVersion;
    private string? _captionFromComposer;
    private ScrollViewer? _scroller;
    private Conversation? _shown;
    private MessageItem? _replyTo;
    private MessageItem? _editing;
    private bool _atBottom = true;
    private int _pinIndex;
    private int _newWhileAway;
    private bool _settingText;
    private Conversation? _statusFor;
    private bool _statusShown;
    private int _statusVersion;

    public ConversationView()
    {
        InitializeComponent();
        Session.ConversationOpened += OnConversationOpened;
        Session.MessageAppended += OnMessageAppended;
        EmojiPickerPanel.Picked += OnEmojiPicked;
        AttachPhotosItem.Icon = WaIcons.PathIcon("Media");
        AttachFilesItem.Icon = WaIcons.PathIcon("Document");

        _highlightTimer = DispatcherQueue.CreateTimer();
        _highlightTimer.Interval = TimeSpan.FromSeconds(2);
        _highlightTimer.IsRepeating = false;
        _highlightTimer.Tick += OnHighlightTimerTick;

        // A link is previewed once typing stops for a moment, not at every letter of it.
        _linkTimer = DispatcherQueue.CreateTimer();
        _linkTimer.Interval = TimeSpan.FromMilliseconds(500);
        _linkTimer.IsRepeating = false;
        _linkTimer.Tick += (_, _) => _ = UpdateLinkPreviewAsync();

        MediaPreview.SendRequested += OnMediaSendRequested;
        Session.PropertyChanged += OnSessionChanged;
        ElementCompositionPreview.SetIsTranslationEnabled(HeaderNameLabel, true);
        ElementCompositionPreview.SetIsTranslationEnabled(HeaderStatusText, true);
        HeaderStatusText.SizeChanged += OnHeaderStatusSizeChanged;
        ShowHeaderStatus();
    }

    /// <summary>
    /// The list's scroll viewer. It only exists once the list has been shown,
    /// which is after the first chat opened, so it is looked up on first use.
    /// </summary>
    private ScrollViewer? Scroller
    {
        get
        {
            if (_scroller is null && FindDescendant<ScrollViewer>(MessageList) is { } found)
            {
                _scroller = found;
                _scroller.ViewChanged += OnViewChanged;
                if (MessageList.ItemsPanelRoot is { } rows)
                {
                    rows.SizeChanged += OnRowsSizeChanged;
                }
            }
            return _scroller;
        }
    }

    public Session Session => App.Current.Session;

    /// <summary>The header, which moves the window like a title bar.</summary>
    public FrameworkElement TitleArea => HeaderBar;

    /// <summary>Room on the right of the header for the window's minimize, maximize and close.</summary>
    public double CaptionInset
    {
        set
        {
            var padding = new Thickness(16, 0, 10 + value, 0);
            if (HeaderBar.Padding != padding)
            {
                HeaderBar.Padding = padding;
            }
        }
    }

    /// <summary>Puts the cursor in the message box.</summary>
    public void FocusComposer() => MessageBox.Focus(FocusState.Programmatic);

    // ---- Opening a chat ----

    private void OnConversationOpened(string? aroundMessage)
    {
        Conversation? conversation = Session.Current;
        if (conversation is null)
        {
            SaveDraft();
            MediaPreview.Close();
            ClearLinkPreview();
            _shown = null;
            UpdatePinBar();
            MessageList.ItemsSource = null;
            return;
        }

        if (conversation == _shown && aroundMessage is null)
        {
            // The same chat got more messages from history sync.
            UpdateOlderPanel();
            return;
        }

        if (conversation != _shown)
        {
            SaveDraft();
            CancelReply();
            MediaPreview.Close();
            ClearLinkPreview();
            _removedLinkUrl = null;
            _pinIndex = 0;
            AudioPlayer.Stop();
            _newWhileAway = 0;
            UpdateScrollButton();

            _settingText = true;
            MessageBox.Text = conversation.Chat.Draft ?? "";
            _settingText = false;
            conversation.Chat.Draft = null;
            UpdateSendButton();
        }

        _shown = conversation;
        UpdatePinBar();
        MessageList.ItemsSource = conversation.Items;
        bool readOnly = conversation.Chat.IsReadOnly;
        Composer.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyNotice.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        UpdateOlderPanel();

        // Laying the list out now would make the rows at the top of the chat,
        // only to throw them away when it scrolls to the bottom. The list
        // brings a row into view by itself once it lays out; only the first
        // time does it need laying out here, so that its scroll viewer exists.
        if (_scroller is null)
        {
            MessageList.UpdateLayout();
            _ = Scroller;
        }
        // Until the view reports where it ended up, whether it follows the
        // newest messages down goes by where it was sent.
        if (aroundMessage is not null && conversation.Find(aroundMessage) is { } target)
        {
            _atBottom = false;
            ScrollTo(target);
        }
#if HAS_UNO
        // Uno's list only guesses how tall the rows it has not made are, and a
        // row brought to the top near the end of the chat lands off the mark.
        // A few unread messages fit on the screen with their line anyway.
        else if (conversation.UnreadLine is { } nearEnd && conversation.Items.Count - conversation.Items.IndexOf(nearEnd) <= 6)
        {
            _atBottom = true;
            ScrollToBottom();
        }
#endif
        else if (conversation.UnreadLine is { } line)
        {
            _atBottom = false;
            MessageList.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
#if HAS_UNO
            // Uno forgets what it is asked before the list is laid out; ask again after.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_shown == conversation)
                {
                    MessageList.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
                }
            });
#endif
        }
        else
        {
            _atBottom = true;
            ScrollToBottom();
        }
        // A chat too short to scroll reports no scrolling, so whether it is at
        // the bottom is worked out once its rows are laid out.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateAtBottom);
        FocusComposer();
    }

    private void SaveDraft()
    {
        if (_shown is not null && _editing is null)
        {
            string draft = MessageBox.Text;
            _shown.Chat.Draft = string.IsNullOrWhiteSpace(draft) ? null : draft;
        }
    }

    private void UpdateOlderPanel() =>
        OlderPanel.Visibility = _shown is { HasOlder: false } && _shown.Messages.Any() ? Visibility.Visible : Visibility.Collapsed;

    // ---- Scrolling ----

    private void ScrollToBottom(bool animate = false)
    {
        if (MessageList.Items.Count == 0)
        {
            return;
        }
        // Following a new message at the bottom only scrolls. Asking the list to
        // bring the row into view makes it drop and rebuild the rows it shows,
        // which blanks the chat for a moment.
        if (!animate || Scroller is null)
        {
            MessageList.ScrollIntoView(MessageList.Items[^1], ScrollIntoViewAlignment.Default);
        }
        // Rows get their real height only once they are shown, which changes
        // where the bottom is. Go there again after they have been laid out.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            Scroller?.ChangeView(null, Scroller.ScrollableHeight, null, !animate);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Scroller?.ChangeView(null, Scroller.ScrollableHeight, null, true));
        });
    }

    /// <summary>Scrolls to a message of the open chat and lights it up. False when it is not loaded.</summary>
    public bool ShowMessage(string id)
    {
        if (_shown?.Find(id) is not { } item)
        {
            return false;
        }
        ScrollTo(item);
        return true;
    }

    private void ScrollTo(MessageItem item)
    {
        MessageList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Center(item, 3));
        Highlight(item);
    }

    /// <summary>
    /// Puts a message in the middle of the view. Its row has a place only after
    /// a layout pass, and the rows around it can still change height after
    /// that, so this goes again on the next passes.
    /// </summary>
    private void Center(MessageItem item, int passes)
    {
        if (passes == 0 || Scroller is not { } scroller || _shown?.Find(item.Id) != item)
        {
            return;
        }
        if (MessageList.ContainerFromItem(item) is FrameworkElement row)
        {
            double top = row.TransformToVisual(scroller).TransformPoint(default).Y;
            double gap = Math.Max(0, (scroller.ViewportHeight - row.ActualHeight) / 2);
            scroller.ChangeView(null, Math.Max(0, scroller.VerticalOffset + top - gap), null, true);
        }
        else
        {
            MessageList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Center(item, passes - 1));
    }

    /// <summary>Lights up the row of a message for a moment, as WhatsApp does when it jumps to one.</summary>
    private void Highlight(MessageItem item)
    {
        if (_highlighted is not null)
        {
            _highlighted.IsHighlighted = false;
            _highlighted.HasHighlight = false;
        }
        _highlighted = item;
        // Lit before the band is made, so it starts lit rather than fading in.
        item.IsHighlighted = true;
        item.HasHighlight = true;
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void OnHighlightTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_highlighted is { } item)
        {
            item.IsHighlighted = false;
            _highlighted = null;
            // The band goes once it has faded out, unless the row lit up again.
            DispatcherQueueTimer done = DispatcherQueue.CreateTimer();
            done.Interval = TimeSpan.FromMilliseconds(400);
            done.IsRepeating = false;
            done.Tick += (_, _) => item.HasHighlight = item.IsHighlighted;
            done.Start();
        }
    }

    private async void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is null || _shown is null)
        {
            return;
        }
        UpdateAtBottom();

        if (_scroller.VerticalOffset < 800 && _shown.HasOlder)
        {
            await Session.LoadOlderAsync();
            UpdateOlderPanel();
        }
        else if (_scroller.VerticalOffset > _scroller.ScrollableHeight - 800 && _shown.HasNewer)
        {
            await Session.LoadNewerAsync();
        }
    }

    /// <summary>
    /// Pictures and link previews get their height only once they have loaded,
    /// which is after the chat scrolled to the bottom. The list keeps the rows
    /// above in place, so the newest messages would slide out of view; at the
    /// bottom, the chat follows them down.
    /// </summary>
    private void OnRowsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_atBottom && _shown is { HasNewer: false } && _scroller is { } scroller && e.NewSize.Height > e.PreviousSize.Height)
        {
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        }
    }

    private void UpdateAtBottom()
    {
        if (Scroller is not { } scroller || _shown is null)
        {
            return;
        }
        _atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight - 60;
        if (_atBottom && !_shown.HasNewer)
        {
            _newWhileAway = 0;
        }
        UpdateScrollButton();
    }

    private void UpdateScrollButton()
    {
        bool show = !_atBottom || _shown?.HasNewer == true;
        ScrollDownButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NewMessagesBadge.Visibility = _newWhileAway > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewMessagesBadge.Value = _newWhileAway;
    }

    private async void OnScrollDownClick(object sender, RoutedEventArgs e)
    {
        if (_shown is { HasNewer: true } conversation)
        {
            // Jumped to an old message: load the newest ones again.
            await Session.OpenAsync(conversation.Jid);
            return;
        }
        _newWhileAway = 0;
        ScrollToBottom(animate: true);
    }

    private void OnMessageAppended(MessageItem item)
    {
        if (item.FromMe || _atBottom)
        {
            ScrollToBottom(animate: true);
        }
        else
        {
            _newWhileAway++;
            UpdateScrollButton();
        }
    }

    private async void OnRequestOlderClick(object sender, RoutedEventArgs e)
    {
        OlderButton.IsEnabled = false;
        await Session.RequestOlderFromPhoneAsync();
        // The phone answers through history sync, which reloads the chat.
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(10);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => OlderButton.IsEnabled = true;
        timer.Start();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not MessageItem item)
        {
            return;
        }
        if (args.InRecycleQueue)
        {
            item.Unrealize();
            return;
        }
        item.Realize();
        if (item.IsVoice && !item.SenderAvatarRequested)
        {
            item.SenderAvatarRequested = true;
            _ = LoadSenderAvatarAsync(item);
        }

        // Videos from history sync have no preview at all; small ones are fetched to show a frame.
        bool smallVideoWithoutPreview = item.IsPlayable && item.Data.Media is { Thumb: null, Size: > 0 and < 8_000_000 };
        bool wanted = item.IsSticker || ((item.Kind == "image" || smallVideoWithoutPreview) && Session.Settings.AutoDownloadImages);
        if (wanted && !item.IsDownloaded && !item.IsBusy && _autoDownloads.Add(item.Chat + "/" + item.Id))
        {
            _ = AutoDownloadAsync(item);
        }
        else if (!wanted && item.HasVisual && item.Data.Media is { Thumb: null } && !item.IsDownloaded && _autoDownloads.Add("thumb:" + item.Chat + "/" + item.Id))
        {
            // Messages from history sync come without the small preview; WhatsApp keeps one to fetch.
            _ = Session.Client.FetchThumbnailAsync(item.Chat, item.Id).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>The picture beside a voice message: of who sent it, or one's own.</summary>
    private async Task LoadSenderAvatarAsync(MessageItem item)
    {
        string? jid = item.FromMe ? Session.Me?.Jid : item.Data.Sender;
        if (string.IsNullOrEmpty(jid))
        {
            return;
        }
        try
        {
            item.SenderAvatar = Images.Avatar(await Session.Client.GetAvatarAsync(jid));
        }
        catch (BridgeException)
        {
            item.SenderAvatarRequested = false;
        }
    }

        private async Task AutoDownloadAsync(MessageItem item)
    {
        try
        {
            string path = await Session.Client.DownloadAsync(item.Chat, item.Id);
            if (item.Data.Media is { } media)
            {
                media.Path = path;
                item.Update(item.Data);
            }
        }
        catch (BridgeException)
        {
            // The preview stays; a click tries again and reports the problem.
        }
    }

    // ---- Writing ----

    private void OnMessageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter when !shift:
                e.Handled = true;
                _ = SendAsync();
                break;
            case VirtualKey.Escape when _replyTo is not null || _editing is not null:
                e.Handled = true;
                if (_editing is not null)
                {
                    SetText("");
                }
                CancelReply();
                break;
            case VirtualKey.Up when MessageBox.Text.Length == 0:
                // Up in an empty box edits one's last message, as in WhatsApp.
                if (_shown?.Messages.LastOrDefault(m => m.FromMe) is { CanEdit: true } last)
                {
                    e.Handled = true;
                    StartEdit(last);
                }
                break;
        }
    }

    private void OnMessageBoxTextChanged(object sender, RoutedEventArgs e)
    {
        UpdateSendButton();
        if (!_settingText && MessageBox.Text.Length > 0 && _editing is null)
        {
            Session.NotifyTyping();
        }
        _linkTimer.Stop();
        _linkTimer.Start();
    }

    // ---- The preview of a link ----

    /// <summary>Shows the preview of the first link in the text, as WhatsApp does above the message box.</summary>
    private async Task UpdateLinkPreviewAsync()
    {
        string text = MessageBox.Text;
        if (text.Trim().Length == 0)
        {
            _removedLinkUrl = null;
        }
        string? url = _editing is null && _shown is { Chat.IsReadOnly: false } ? WhatsAppText.FirstLink(text) : null;
        if (url == _removedLinkUrl)
        {
            url = null;
        }
        if (url == _linkUrl)
        {
            return;
        }
        ClearLinkPreview();
        if (url is null)
        {
            return;
        }

        _linkUrl = url;
        int version = _linkVersion;
        LinkBarThumbBox.Visibility = Visibility.Collapsed;
        LinkBarProgress.IsActive = true;
        LinkBarProgress.Visibility = Visibility.Visible;
        LinkBarTitle.Visibility = Visibility.Collapsed;
        LinkBarDescription.Visibility = Visibility.Collapsed;
        LinkBarHost.Text = Formatting.LinkHost(url);
        LinkBar.Visibility = Visibility.Visible;

        LinkData? link = null;
        try
        {
            link = await Session.Client.GetLinkPreviewAsync(url);
        }
        catch (BridgeException)
        {
            // The site could not be reached; the link goes without a preview.
        }
        if (version != _linkVersion)
        {
            return;
        }
        // Without a preview the bar goes, but the link stays the one handled,
        // so typing on does not fetch it again.
        LinkBarProgress.IsActive = false;
        LinkBarProgress.Visibility = Visibility.Collapsed;
        if (link is null)
        {
            LinkBar.Visibility = Visibility.Collapsed;
            return;
        }
        _link = link;
        LinkBarThumb.Source = Images.FromBytes(link.Thumb);
        LinkBarThumbBox.Visibility = link.Thumb is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        EmojiText.SetText(LinkBarTitle, link.Title ?? "");
        LinkBarTitle.Visibility = string.IsNullOrEmpty(link.Title) ? Visibility.Collapsed : Visibility.Visible;
        EmojiText.SetText(LinkBarDescription, link.Description ?? "");
        LinkBarDescription.Visibility = string.IsNullOrEmpty(link.Description) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearLinkPreview()
    {
        _linkVersion++;
        _linkUrl = null;
        _link = null;
        LinkBar.Visibility = Visibility.Collapsed;
        LinkBarProgress.IsActive = false;
        LinkBarThumb.Source = null;
    }

    private void OnRemoveLinkPreviewClick(object sender, RoutedEventArgs e)
    {
        _removedLinkUrl = _linkUrl;
        ClearLinkPreview();
        FocusComposer();
    }

    /// <summary>The send button shows once there is something to send.</summary>
    private void UpdateSendButton() =>
        SendButton.Visibility = MessageBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetText(string text)
    {
        _settingText = true;
        MessageBox.Text = text;
        _settingText = false;
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        string text = MessageBox.Text.Trim('\r', '\n', ' ', '\t');
        if (text.Length == 0)
        {
            return;
        }
        if (_editing is { } editing)
        {
            SetText("");
            CancelReply();
            if (text != editing.Data.Text)
            {
                await Session.EditAsync(editing, text);
            }
            return;
        }

        string? replyTo = _replyTo?.Id;
        LinkData? link = _link is { } loaded && text.Contains(loaded.Url, StringComparison.Ordinal) ? loaded : null;
        SetText("");
        CancelReply();
        ClearLinkPreview();
        _removedLinkUrl = null;
        if (!await Session.SendTextAsync(text, replyTo, link) && MessageBox.Text.Length == 0)
        {
            // Keep what was typed when it could not be sent.
            SetText(text);
        }
    }

    private void StartReply(MessageItem item)
    {
        _editing = null;
        _replyTo = item;
        EmojiText.SetText(ReplyTitle, item.FromMe ? Loc.T("common.you") : !string.IsNullOrEmpty(item.SenderName) ? item.SenderName : _shown?.Chat.Name ?? "");
        EmojiText.SetText(ReplyText, MessagePreview.Describe(item.Kind, item.Data.Text, item.Data.Media?.Name, item.Data.Media?.Seconds ?? 0, item.FromMe).Text);
        ShowReplyBar(item.ReplyBrush);
        FocusComposer();
    }

    private void StartEdit(MessageItem item)
    {
        _replyTo = null;
        _editing = item;
        EmojiText.SetText(ReplyTitle, Loc.T("conversation.editMessage"));
        EmojiText.SetText(ReplyText, item.Data.Text ?? "");
        ShowReplyBar((Brush)Application.Current.Resources["AccentGreenBrush"]);
        SetText(item.Data.Text ?? "");
        FocusComposer();
    }

    private void CancelReply()
    {
        _replyTo = null;
        _editing = null;
        ReplyBar.Visibility = Visibility.Collapsed;
        ComposerField.CornerRadius = new CornerRadius(24);
    }

    // The reply sits inside the field, which then trades its pill shape for a card.
    private void ShowReplyBar(Brush accent)
    {
        ReplyAccent.Fill = accent;
        ReplyTitle.Foreground = accent;
        ReplyBar.Visibility = Visibility.Visible;
        ComposerField.CornerRadius = new CornerRadius(16);
    }

    private void OnCancelReplyClick(object sender, RoutedEventArgs e)
    {
        if (_editing is not null)
        {
            SetText("");
        }
        CancelReply();
        FocusComposer();
    }

    private void OnEmojiFlyoutOpened(object? sender, object e) => EmojiPickerPanel.FocusSearch();

    /// <summary>Puts a picked emoji where the cursor is, and keeps the picker open for more.</summary>
    private void OnEmojiPicked(string emoji)
    {
        MessageBox.Insert(emoji);
    }

    // ---- Files ----

    private async void OnAttachPhotosClick(object sender, RoutedEventArgs e) => await PickAndSendAsync(SendMediaView.PhotoAndVideoTypes);

    private async void OnAttachFilesClick(object sender, RoutedEventArgs e) => await PickAndSendAsync(["*"], asDocument: true);

    private async Task PickAndSendAsync(string[] types, bool asDocument = false)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        App.Current.InitializePicker(picker);
        foreach (string type in types)
        {
            picker.FileTypeFilter.Add(type);
        }
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            ShowFiles(files.Select(f => f.Path).ToList(), asDocument);
        }
    }

    /// <summary>Shows files to send over the chat, or adds them when files are shown already.</summary>
    private void ShowFiles(IReadOnlyList<string> paths, bool asDocument = false)
    {
        if (_shown is null || paths.Count == 0)
        {
            return;
        }
        if (MediaPreview.IsOpen)
        {
            MediaPreview.Add(paths);
        }
        else
        {
            // What was typed becomes the caption, and leaves the message box once it is sent.
            _captionFromComposer = MessageBox.Text.Trim();
            MediaPreview.Open(paths, asDocument, _captionFromComposer);
        }
    }

    private async void OnMediaSendRequested(IReadOnlyList<PendingFile> files, bool asDocument)
    {
        if (_shown is not { } conversation)
        {
            return;
        }
        if (_captionFromComposer is { Length: > 0 } typed && files[0].Caption.Trim() == typed && MessageBox.Text.Trim() == typed)
        {
            SetText("");
        }
        _captionFromComposer = null;
        string? replyTo = _replyTo?.Id;
        CancelReply();
        FocusComposer();
        for (int i = 0; i < files.Count; i++)
        {
            string caption = files[i].Caption.Trim();
            OutgoingMedia media = await MediaInfo.DescribeAsync(conversation.Jid, files[i].Path, caption.Length > 0 ? caption : null,
                i == 0 ? replyTo : null, asDocument);
            await Session.SendFileAsync(media);
        }
    }

    /// <summary>Closes the files about to be sent, as Esc does. False when none are shown.</summary>
    public bool CloseMediaPreview()
    {
        if (!MediaPreview.IsOpen)
        {
            return false;
        }
        MediaPreview.Close();
        FocusComposer();
        return true;
    }

    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync();
            ShowFiles(items.OfType<StorageFile>().Select(f => f.Path).ToList());
        }
        else if (content.Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            string? path = await MediaInfo.SaveClipboardImageAsync(content);
            if (path is not null)
            {
                ShowFiles([path]);
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_shown is not null && !_shown.Chat.IsReadOnly && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.T("conversation.send");
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        ShowFiles(items.OfType<StorageFile>().Select(f => f.Path).ToList());
    }

    // ---- Messages: clicks and the context menu ----

    private static MessageItem? ItemOf(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: MessageItem item })
            {
                return item;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private async void OnVisualTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }
        if (item.IsLocation)
        {
            OpenUrl(item.MapUrl);
            return;
        }
        App.Current.Window.ShowMedia(item);
    }

    private async void OnDocumentTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && await Session.DownloadAsync(item) is { } path)
        {
            OpenFile(path);
        }
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }
        string? path = await Session.DownloadAsync(item);
        if (path is null)
        {
            return;
        }
        AudioPlayer.Toggle(item, path, () =>
        {
            if (item.IsVoice && !item.FromMe)
            {
                _ = Session.Client.MarkPlayedAsync(item.Chat, item.Id).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        });
    }

    private void OnAudioSeek(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement bar && ItemOf(sender) is { } item && bar.ActualWidth > 0)
        {
            AudioPlayer.Seek(item, e.GetPosition(bar).X / bar.ActualWidth);
        }
    }

    private async void OnQuoteTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is not { Data.Quote: { } quote } || _shown is not { } conversation)
        {
            return;
        }
        if (!ShowMessage(quote.Id))
        {
            await Session.OpenAsync(conversation.Jid, quote.Id);
        }
    }

    private void OnLinkTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is { Data.Link: { } link })
        {
            OpenUrl(link.Url.Contains("://", StringComparison.Ordinal) ? link.Url : "https://" + link.Url);
        }
    }

    private void OnMapClick(object sender, RoutedEventArgs e) => OpenUrl(ItemOf(sender)?.MapUrl);

    /// <summary>The name above a message in a group opens the sender's info.</summary>
    private void OnSenderClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { FromMe: false, Data.Sender: { Length: > 0 } jid } item)
        {
            App.Current.Window.ShowProfile(jid, item.SenderName.TrimStart('~'), isGroup: false);
        }
    }

    private static void OpenUrl(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            _ = Launcher.LaunchUriAsync(uri);
        }
    }

    internal static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Current.Session.ShowError(Loc.T("conversation.noAppToOpen", ("file", Path.GetFileName(path))));
        }
    }

    private void OnMessageContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (ItemOf(e.OriginalSource) is not { } item || item.Kind == "system")
        {
            return;
        }
        e.Handled = true;
        Point? point = e.TryGetPosition(MessageList, out Point p) ? p : null;
        MenuFlyout menu = BuildMessageMenu(item, e.OriginalSource as RichTextBlock, point);
        if (point is { } at)
        {
            menu.ShowAt(MessageList, at);
        }
        else if (e.OriginalSource is FrameworkElement element)
        {
            menu.ShowAt(element);
        }
    }

    /// <summary>The chevron in the corner of a message, shown while the pointer is over it.</summary>
    private void OnMessageMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || ItemOf(sender) is not { } item)
        {
            return;
        }
        // The button goes once the pointer leaves the message for the menu,
        // so the menu and what it opens are placed by where it was.
        Point at = button.TransformToVisual(MessageList).TransformPoint(new Point(button.ActualWidth, button.ActualHeight));
        MenuFlyout menu = BuildMessageMenu(item, null, at);
        menu.ShowAt(MessageList, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    /// <summary>The smiley beside a message, shown while the pointer is over it.</summary>
    private void OnHoverReactClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && ItemOf(sender) is { } item)
        {
            ShowReactionBar(item, button.TransformToVisual(MessageList).TransformPoint(new Point(button.ActualWidth / 2, 0)));
        }
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && item.Kind != "system")
        {
            item.SetHover(true, item.CanReact && _shown?.Chat.IsReadOnly != true);
        }
    }

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => ItemOf(sender)?.SetHover(false, false);

    private MenuFlyout BuildMessageMenu(MessageItem item, RichTextBlock? textBlock, Point? point)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Add(string text, string icon, Action action)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = WaIcons.PathIcon(icon) };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
            return entry;
        }

        bool writable = _shown?.Chat.IsReadOnly != true;
        bool react = item.CanReact && writable;
        if (react)
        {
            // WhatsApp's quick reactions above the menu's entries.
            menu.Items.Add(new MenuFlyoutItem
            {
                Style = (Style)Resources["MenuContentItemStyle"],
                Tag = ReactionRow(item, menu.Hide, () => ShowEmojiPicker(item, point)),
            });
        }
        if (item.IsFailed)
        {
            Add(Loc.T("conversation.sendAgain"), "Refresh", () => _ = Session.RetryAsync(item));
        }
        if (react)
        {
            Add(Loc.T("conversation.reply"), "Reply", () => StartReply(item));
        }

        string selected = Emoji.ToPlain(textBlock?.SelectedText);
        if (selected.Length > 0 || item.HasText && !item.IsNotice)
        {
            Add(selected.Length > 0 ? Loc.T("conversation.copySelection") : Loc.T("common.copy"), "Copy", () =>
            {
                var package = new DataPackage();
                package.SetText(selected.Length > 0 ? selected : item.CopyText);
                Clipboard.SetContent(package);
            });
        }
        if (react)
        {
            Add(Loc.T("conversation.react"), "React", () => ShowEmojiPicker(item, point));
        }
        if (item.CanForward)
        {
            Add(Loc.T("conversation.forward"), "Forward", () => _ = ForwardAsync(item));
        }
        if (item.CanAddOn && writable)
        {
            if (item.IsPinned)
            {
                Add(Loc.T("conversation.unpin"), "Unpin", () => _ = Session.PinAsync(item.Chat, item.Id, false));
            }
            else
            {
                var pin = new MenuFlyoutSubItem { Text = Loc.T("conversation.pin"), Icon = WaIcons.PathIcon("Pin") };
                foreach (long seconds in s_pinDurations)
                {
                    var entry = new MenuFlyoutItem { Text = Formatting.Disappearing(seconds) };
                    entry.Click += (_, _) => _ = Session.PinAsync(item.Chat, item.Id, true, seconds);
                    pin.Items.Add(entry);
                }
                menu.Items.Add(pin);
            }
            // Keeping is for disappearing messages; only who sent one can let it go again.
            if (item.IsKept ? item.FromMe : _shown?.Chat.Data.Ephemeral > 0)
            {
                Add(item.IsKept ? Loc.T("conversation.unkeep") : Loc.T("conversation.keep"), item.IsKept ? "Unkeep" : "Keep", () => _ = Session.KeepAsync(item, !item.IsKept));
            }
        }
        if (item.CanAddOn)
        {
            Add(item.IsStarred ? Loc.T("conversation.unstar") : Loc.T("conversation.star"), item.IsStarred ? "StarFilled" : "Star", () => _ = Session.StarAsync(item, !item.IsStarred));
        }
        if (item.CanEdit)
        {
            Add(Loc.T("conversation.edit"), "Edit", () => StartEdit(item));
        }
        if (item.HasMedia)
        {
            if (item.IsDownloaded)
            {
                Add(Loc.T("common.open"), "OpenInNew", () => OpenFile(item.Data.Media!.Path!));
                Add(Loc.T("conversation.showInFolder"), "Folder", () => Process.Start("explorer.exe", $"/select,\"{item.Data.Media!.Path}\""));
            }
            Add(Loc.T("conversation.saveAs"), "Download", () => _ = SaveAsAsync(item));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.CanRevoke)
        {
            Add(Loc.T("conversation.deleteForEveryone"), "Delete", () => _ = ConfirmRevokeAsync(item));
        }
        Add(Loc.T("conversation.deleteForMe"), "Delete", () => _ = Session.DeleteForMeAsync(item));
        return menu;
    }

    /// <summary>WhatsApp's row of quick reactions, on its own above the message.</summary>
    private void ShowReactionBar(MessageItem item, Point? point)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        flyout.Content = ReactionRow(item, flyout.Hide, () => ShowEmojiPicker(item, point));
        flyout.FlyoutPresenterStyle = ReactionBarStyle();
        ShowFlyout(flyout, point);
    }

    /// <summary>The quick reactions, and a plus for any other emoji. Picking one closes what holds the row.</summary>
    private StackPanel ReactionRow(MessageItem item, Action close, Action more)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (string emoji in s_quickReactions)
        {
            var button = new Button
            {
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(22),
                BorderThickness = new Thickness(0),
                Background = item.OwnReaction == emoji
                    ? (Brush)Application.Current.Resources["ListViewItemBackgroundSelected"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = EmojiVisual(emoji, 30),
            };
            ToolTipService.SetToolTip(button, item.OwnReaction == emoji ? Loc.T("conversation.removeReaction") : null);
            button.Click += (_, _) =>
            {
                close();
                _ = Session.ReactAsync(item, item.OwnReaction == emoji ? "" : emoji);
            };
            bar.Children.Add(button);
        }

        var plus = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new WaIcon { Kind = "Add", Size = 24 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(plus, Loc.T("conversation.moreReactions"));
        plus.Click += (_, _) =>
        {
            close();
            more();
        };
        bar.Children.Add(plus);
        return bar;
    }

    private void ShowEmojiPicker(MessageItem item, Point? point)
    {
        var picker = new EmojiPicker();
        var pickerFlyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Top };
        pickerFlyout.Opened += (_, _) => picker.FocusSearch();
        picker.Picked += emoji =>
        {
            pickerFlyout.Hide();
            _ = Session.ReactAsync(item, emoji);
        };
        ShowFlyout(pickerFlyout, point);
    }

    private async Task ForwardAsync(MessageItem item)
    {
        var dialog = new ForwardDialog(Session) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Chosen is { Count: > 0 } chosen)
        {
            await Session.ForwardAsync(item, chosen);
        }
    }

    private void ShowFlyout(Flyout flyout, Point? point)
    {
        if (point is { } at)
        {
            flyout.ShowAt(MessageList, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.Top });
        }
        else
        {
            flyout.ShowAt(MessageList);
        }
    }

    private static Style? s_reactionBarStyle;

    private static Style ReactionBarStyle()
    {
        if (s_reactionBarStyle is null)
        {
            s_reactionBarStyle = new Style(typeof(FlyoutPresenter));
            s_reactionBarStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(6)));
            s_reactionBarStyle.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(28)));
        }
        return s_reactionBarStyle;
    }

    /// <summary>An emoji as WhatsApp draws it, or as the font does when the sprites are not there.</summary>
    private static FrameworkElement EmojiVisual(string emoji, double size)
    {
        int index = Emoji.Set?.Find(emoji) ?? -1;
        return index >= 0
            ? Emoji.Create(index, size)
            : new TextBlock { Text = emoji, FontSize = size * 0.8, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private async Task ConfirmRevokeAsync(MessageItem item)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.T("conversation.deleteForEveryoneTitle"),
            Content = Loc.T("conversation.deleteForEveryoneText"),
            PrimaryButtonText = Loc.T("common.delete"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            // A dialog is not inside the window's content, so it does not take its theme by itself.
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Session.RevokeAsync(item);
        }
    }

    private async Task SaveAsAsync(MessageItem item)
    {
        string? path = await Session.DownloadAsync(item);
        if (path is null)
        {
            return;
        }
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(path) };
        App.Current.InitializePicker(picker);
        string extension = Path.GetExtension(path);
        picker.FileTypeChoices.Add(extension.Length > 1 ? Loc.T("conversation.fileType", ("type", extension.TrimStart('.').ToUpperInvariant())) : Loc.T("conversation.file"), [extension.Length > 1 ? extension : ".bin"]);
        if (item.Data.Media?.Name is { Length: > 0 } name)
        {
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(name);
        }
        else
        {
            picker.SuggestedFileName = $"WhatsApp {Formatting.ToLocal(item.Ts):yyyy-MM-dd HH.mm.ss}";
        }
        StorageFile? target = await picker.PickSaveFileAsync();
        if (target is not null)
        {
            File.Copy(path, target.Path, overwrite: true);
        }
    }

    // ---- Pinned messages ----

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Session.Pins))
        {
            UpdatePinBar();
        }
        else if (e.PropertyName == nameof(Session.HeaderStatus))
        {
            ShowHeaderStatus();
        }
    }

    // ---- The header's status line ----

    /// <summary>
    /// Shows the line under the name. Without one the name slides down into the middle,
    /// and a new line fades in from below. Opening another chat just puts it in place.
    /// </summary>
    private void ShowHeaderStatus()
    {
        string? status = Session.HeaderStatus;
        bool show = !string.IsNullOrEmpty(status);
        bool animate = _statusFor == Session.Current && IsLoaded;
        _statusFor = Session.Current;
        int version = ++_statusVersion;

        // Half the status line's height takes the name to the middle of the header.
        float drop = show ? 0 : (float)HeaderStatusText.ActualHeight / 2;
        Visual name = ElementCompositionPreview.GetElementVisual(HeaderNameLabel);
        Visual line = ElementCompositionPreview.GetElementVisual(HeaderStatusText);
        Animate(name, "Translation", new Vector3(0, drop, 0), animate ? 220 : 0);

        if (!show)
        {
            // The old text stays while it fades, so the line keeps its height.
            _statusShown = false;
            Animate(line, "Opacity", 0, animate ? 150 : 0);
            Animate(line, "Translation", new Vector3(0, drop, 0), animate ? 220 : 0);
            return;
        }

        if (HeaderStatusText.Text == status && _statusShown)
        {
            return;
        }
        if (!animate)
        {
            HeaderStatusText.Text = status;
            _statusShown = true;
            Animate(line, "Opacity", 1, 0);
            Animate(line, "Translation", Vector3.Zero, 0);
            return;
        }
        if (!_statusShown)
        {
            HeaderStatusText.Text = status;
            _statusShown = true;
            FadeIn(line);
            return;
        }

        // From one status to another: the old one leaves upwards, the new one comes from below.
        CompositionScopedBatch batch = line.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Animate(line, "Opacity", 0, 100);
        Animate(line, "Translation", new Vector3(0, -6, 0), 100);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _statusVersion)
            {
                HeaderStatusText.Text = status;
                FadeIn(line);
            }
        });
    }

    /// <summary>The line's height is only known after layout, and the empty header centers the name with it.</summary>
    private void OnHeaderStatusSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height != e.PreviousSize.Height && !_statusShown)
        {
            var drop = new Vector3(0, (float)e.NewSize.Height / 2, 0);
            Animate(ElementCompositionPreview.GetElementVisual(HeaderNameLabel), "Translation", drop, 0);
            Animate(ElementCompositionPreview.GetElementVisual(HeaderStatusText), "Translation", drop, 0);
        }
    }

    private static void FadeIn(Visual line)
    {
        Animate(line, "Translation", new Vector3(0, 6, 0), 0);
        Animate(line, "Opacity", 0, 0);
        Animate(line, "Opacity", 1, 180);
        Animate(line, "Translation", Vector3.Zero, 220);
    }

    /// <summary>Moves a visual's property to a value, at once when no time is given.</summary>
    private static void Animate(Visual visual, string property, object value, int milliseconds)
    {
        visual.StopAnimation(property);
        if (milliseconds == 0)
        {
            if (value is Vector3 vector)
            {
                visual.Properties.InsertVector3(property, vector);
            }
            else
            {
                visual.Opacity = Convert.ToSingle(value);
            }
            return;
        }

        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        KeyFrameAnimation animation;
        if (value is Vector3 target)
        {
            Vector3KeyFrameAnimation move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(1, target, ease);
            animation = move;
        }
        else
        {
            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1, Convert.ToSingle(value), ease);
            animation = fade;
        }
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation(property, animation);
    }

    /// <summary>Shows one pinned message above the chat, with a mark for each pin when there are several.</summary>
    private void UpdatePinBar()
    {
        IReadOnlyList<MessageData> pins = Session.Pins;
        if (pins.Count == 0 || _shown is null)
        {
            PinBar.Visibility = Visibility.Collapsed;
            return;
        }
        _pinIndex = Math.Clamp(_pinIndex, 0, pins.Count - 1);
        MessageData pin = pins[_pinIndex];
        string text = MessagePreview.Describe(pin.Kind, pin.Text, pin.Media?.Name, pin.Media?.Seconds ?? 0, pin.FromMe).Text;
        if (_shown.Chat.IsGroup)
        {
            text = Loc.T("conversation.pinFrom", ("name", pin.FromMe ? Loc.T("common.you") : pin.SenderName ?? ""), ("text", text));
        }
        EmojiText.SetText(PinText, text);

        PinMarks.Children.Clear();
        if (pins.Count > 1)
        {
            double height = (32 - 2 * (pins.Count - 1)) / (double)pins.Count;
            for (int i = 0; i < pins.Count; i++)
            {
                PinMarks.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 3,
                    Height = height,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = (Brush)Application.Current.Resources[i == _pinIndex ? "AccentGreenBrush" : "DividerBrush"],
                });
            }
        }
        PinBar.Visibility = Visibility.Visible;
    }

    private async void OnPinBarTapped(object sender, TappedRoutedEventArgs e)
    {
        IReadOnlyList<MessageData> pins = Session.Pins;
        if (_shown is not { } conversation || pins.Count == 0)
        {
            return;
        }
        MessageData pin = pins[Math.Clamp(_pinIndex, 0, pins.Count - 1)];
        _pinIndex = (_pinIndex + 1) % pins.Count;
        UpdatePinBar();
        if (!ShowMessage(pin.Id))
        {
            await Session.OpenAsync(conversation.Jid, pin.Id);
        }
    }

    private void OnUnpinBarClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<MessageData> pins = Session.Pins;
        if (pins.Count > 0)
        {
            MessageData pin = pins[Math.Clamp(_pinIndex, 0, pins.Count - 1)];
            _ = Session.PinAsync(pin.Chat, pin.Id, false);
        }
    }

    // ---- Calls ----

    /// <summary>People can be called; groups, channels and oneself not.</summary>
    public Visibility CallVisibility(ChatItem? chat) =>
        chat is { IsGroup: false } && (chat.Jid.EndsWith("@s.whatsapp.net", StringComparison.Ordinal) || chat.Jid.EndsWith("@lid", StringComparison.Ordinal))
            && chat.Jid != Session.Me?.Jid
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnCallClick(object sender, RoutedEventArgs e)
    {
        if (_shown?.Chat is { } chat)
        {
            _ = Session.Calls.StartAsync(chat.Jid, chat.Name);
        }
    }

    // ---- The chat's menu ----

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_shown?.Chat is { } chat)
        {
            App.Current.Window.ShowProfile(chat.Jid, chat.Name, chat.IsGroup);
        }
    }

    private void OnChatMenuOpening(object? sender, object e)
    {
        ChatMenu.Items.Clear();
        if (_shown?.Chat is { } chat)
        {
            var info = new MenuFlyoutItem { Text = chat.IsGroup ? Loc.T("conversation.groupInfo") : Loc.T("conversation.contactInfo"), Icon = WaIcons.PathIcon("Info") };
            info.Click += (_, _) => App.Current.Window.ShowProfile(chat.Jid, chat.Name, chat.IsGroup);
            ChatMenu.Items.Add(info);
            ChatMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (MenuFlyoutItemBase entry in ChatMenus.Build(chat, Session))
            {
                ChatMenu.Items.Add(entry);
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return default;
    }
}
