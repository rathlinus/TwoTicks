using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly HashSet<string> _autoDownloads = [];
    private readonly DispatcherQueueTimer _highlightTimer;
    private MessageItem? _highlighted;
    private ScrollViewer? _scroller;
    private Conversation? _shown;
    private MessageItem? _replyTo;
    private MessageItem? _editing;
    private bool _atBottom = true;
    private int _newWhileAway;
    private bool _settingText;

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
            }
            return _scroller;
        }
    }

    public Session Session => App.Current.Session;

    /// <summary>Puts the cursor in the message box.</summary>
    public void FocusComposer() => MessageBox.Focus(FocusState.Programmatic);

    // ---- Opening a chat ----

    private void OnConversationOpened(string? aroundMessage)
    {
        Conversation? conversation = Session.Current;
        if (conversation is null)
        {
            SaveDraft();
            _shown = null;
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
            AudioPlayer.Stop();
            _newWhileAway = 0;
            UpdateScrollButton();

            _settingText = true;
            MessageBox.Text = conversation.Chat.Draft ?? "";
            MessageBox.SelectionStart = MessageBox.Text.Length;
            _settingText = false;
            conversation.Chat.Draft = null;
            UpdateSendButton();
        }

        _shown = conversation;
        MessageList.ItemsSource = conversation.Items;
        bool readOnly = conversation.Chat.IsReadOnly;
        Composer.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyNotice.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        UpdateOlderPanel();

        MessageList.UpdateLayout();
        _ = Scroller;
        if (aroundMessage is not null && conversation.Find(aroundMessage) is { } target)
        {
            ScrollTo(target);
        }
        else if (conversation.UnreadLine is { } line)
        {
            MessageList.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
        }
        else
        {
            ScrollToBottom();
        }
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
        MessageList.ScrollIntoView(MessageList.Items[^1], ScrollIntoViewAlignment.Leading);
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
        }
        _highlighted = item;
        item.IsHighlighted = true;
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void OnHighlightTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_highlighted is not null)
        {
            _highlighted.IsHighlighted = false;
            _highlighted = null;
        }
    }

    private async void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is null || _shown is null)
        {
            return;
        }
        _atBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 60;
        if (_atBottom && !_shown.HasNewer)
        {
            _newWhileAway = 0;
        }
        UpdateScrollButton();

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

    private void OnMessageBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendButton();
        if (!_settingText && MessageBox.Text.Length > 0 && _editing is null)
        {
            Session.NotifyTyping();
        }
    }

    /// <summary>The send button shows once there is something to send.</summary>
    private void UpdateSendButton() =>
        SendButton.Visibility = MessageBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetText(string text)
    {
        _settingText = true;
        MessageBox.Text = text;
        MessageBox.SelectionStart = text.Length;
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
        SetText("");
        CancelReply();
        if (!await Session.SendTextAsync(text, replyTo) && MessageBox.Text.Length == 0)
        {
            // Keep what was typed when it could not be sent.
            SetText(text);
        }
    }

    private void StartReply(MessageItem item)
    {
        _editing = null;
        _replyTo = item;
        EmojiText.SetText(ReplyTitle, item.FromMe ? "You" : !string.IsNullOrEmpty(item.SenderName) ? item.SenderName : _shown?.Chat.Name ?? "");
        EmojiText.SetText(ReplyText, MessagePreview.Describe(item.Kind, item.Data.Text, item.Data.Media?.Name, item.Data.Media?.Seconds ?? 0, item.FromMe).Text);
        ReplyBar.Visibility = Visibility.Visible;
        FocusComposer();
    }

    private void StartEdit(MessageItem item)
    {
        _replyTo = null;
        _editing = item;
        EmojiText.SetText(ReplyTitle, "Edit message");
        EmojiText.SetText(ReplyText, item.Data.Text ?? "");
        ReplyBar.Visibility = Visibility.Visible;
        SetText(item.Data.Text ?? "");
        FocusComposer();
    }

    private void CancelReply()
    {
        _replyTo = null;
        _editing = null;
        ReplyBar.Visibility = Visibility.Collapsed;
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

    private void OnEmojiFlyoutOpened(object? sender, object e) => EmojiPickerPanel.Focus(FocusState.Programmatic);

    /// <summary>Puts a picked emoji where the cursor is, and keeps the picker open for more.</summary>
    private void OnEmojiPicked(string emoji)
    {
        int start = MessageBox.SelectionStart;
        MessageBox.SelectedText = emoji;
        MessageBox.SelectionStart = start + emoji.Length;
        MessageBox.SelectionLength = 0;
    }

    // ---- Files ----

    private async void OnAttachPhotosClick(object sender, RoutedEventArgs e) =>
        await PickAndSendAsync([".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".mp4", ".mov", ".m4v", ".3gp"]);

    private async void OnAttachFilesClick(object sender, RoutedEventArgs e) => await PickAndSendAsync(["*"], asDocument: true);

    private async Task PickAndSendAsync(string[] types, bool asDocument = false)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.WindowHandle);
        foreach (string type in types)
        {
            picker.FileTypeFilter.Add(type);
        }
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await SendFilesAsync(files.Select(f => f.Path).ToList(), asDocument);
        }
    }

    private async Task SendFilesAsync(IReadOnlyList<string> paths, bool asDocument = false)
    {
        if (_shown is not { } conversation || paths.Count == 0)
        {
            return;
        }
        var dialog = new SendFilesDialog(paths, conversation.Chat.Name, asDocument, MessageBox.Text) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (dialog.Caption.Length > 0 && dialog.Caption == MessageBox.Text.Trim())
        {
            SetText("");
        }
        string? replyTo = _replyTo?.Id;
        CancelReply();
        for (int i = 0; i < paths.Count; i++)
        {
            OutgoingMedia media = await MediaInfo.DescribeAsync(conversation.Jid, paths[i], i == 0 ? dialog.Caption : null,
                i == 0 ? replyTo : null, dialog.AsDocument);
            await Session.SendFileAsync(media);
        }
    }

    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync();
            await SendFilesAsync(items.OfType<StorageFile>().Select(f => f.Path).ToList());
        }
        else if (content.Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            string? path = await MediaInfo.SaveClipboardImageAsync(content);
            if (path is not null)
            {
                await SendFilesAsync([path]);
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_shown is not null && !_shown.Chat.IsReadOnly && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Send";
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
        await SendFilesAsync(items.OfType<StorageFile>().Select(f => f.Path).ToList());
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
        string? path = await Session.DownloadAsync(item);
        if (path is not null)
        {
            App.Current.Window.ShowMedia(item, path);
        }
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
            App.Current.Session.ShowError($"No app could open {Path.GetFileName(path)}.");
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

        if (item.IsFailed)
        {
            Add("Send again", "Refresh", () => _ = Session.RetryAsync(item));
        }
        if (item.CanReact && _shown?.Chat.IsReadOnly != true)
        {
            Add("Reply", "Reply", () => StartReply(item));
            Add("React", "React", () => ShowReactionBar(item, point));
        }

        string selected = textBlock?.SelectedText ?? "";
        if (selected.Length > 0 || item.HasText && !item.IsNotice)
        {
            Add(selected.Length > 0 ? "Copy selection" : "Copy", "Copy", () =>
            {
                var package = new DataPackage();
                package.SetText(selected.Length > 0 ? selected : item.CopyText);
                Clipboard.SetContent(package);
            });
        }
        if (item.HasMedia)
        {
            if (item.IsDownloaded)
            {
                Add("Open", "OpenInNew", () => OpenFile(item.Data.Media!.Path!));
                Add("Show in folder", "Folder", () => Process.Start("explorer.exe", $"/select,\"{item.Data.Media!.Path}\""));
            }
            Add("Save as…", "Download", () => _ = SaveAsAsync(item));
        }
        if (item.CanEdit)
        {
            Add("Edit", "Edit", () => StartEdit(item));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.CanRevoke)
        {
            Add("Delete for everyone", "Delete", () => _ = ConfirmRevokeAsync(item));
        }
        Add("Delete for me", "Delete", () => _ = Session.DeleteForMeAsync(item));
        return menu;
    }

    /// <summary>WhatsApp's row of quick reactions, and a plus for any other emoji.</summary>
    private void ShowReactionBar(MessageItem item, Point? point)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var flyout = new Flyout { Content = bar, Placement = FlyoutPlacementMode.Top };
        flyout.FlyoutPresenterStyle = ReactionBarStyle();

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
            ToolTipService.SetToolTip(button, item.OwnReaction == emoji ? "Remove your reaction" : null);
            button.Click += (_, _) =>
            {
                flyout.Hide();
                _ = Session.ReactAsync(item, item.OwnReaction == emoji ? "" : emoji);
            };
            bar.Children.Add(button);
        }

        var more = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new WaIcon { Kind = "Add", Size = 24 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "More reactions");
        more.Click += (_, _) =>
        {
            flyout.Hide();
            var picker = new EmojiPicker();
            var pickerFlyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Top };
            picker.Picked += emoji =>
            {
                pickerFlyout.Hide();
                _ = Session.ReactAsync(item, emoji);
            };
            ShowFlyout(pickerFlyout, point);
        };
        bar.Children.Add(more);
        ShowFlyout(flyout, point);
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
            Title = "Delete for everyone?",
            Content = "The message is removed for everyone in this chat. They can see that it was deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
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
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.WindowHandle);
        string extension = Path.GetExtension(path);
        picker.FileTypeChoices.Add(extension.Length > 1 ? extension.TrimStart('.').ToUpperInvariant() + " file" : "File", [extension.Length > 1 ? extension : ".bin"]);
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

    // ---- The chat's menu ----

    private void OnChatMenuOpening(object? sender, object e)
    {
        ChatMenu.Items.Clear();
        if (_shown?.Chat is { } chat)
        {
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
        return null;
    }
}
