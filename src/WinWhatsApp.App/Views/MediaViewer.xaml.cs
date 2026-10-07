using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinWhatsApp.App.Controls;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>A photo or video in the viewer, or a profile photo.</summary>
public sealed class ViewerItem : Observable
{
    private readonly string? _path;
    private ImageSource? _thumb;
    private bool _isCurrent;

    public ViewerItem(MessageData message)
    {
        Message = message;
    }

    public ViewerItem(string path)
    {
        _path = path;
    }

    /// <summary>The message the photo or video came with; none for a profile photo.</summary>
    public MessageData? Message { get; }

    public string? Path => Message is null ? _path : Message.Media?.Path is { Length: > 0 } p && File.Exists(p) ? p : null;

    public bool IsVideo => Message?.Kind is "video" or "gif";

    public Visibility VideoVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The small picture in the strip.</summary>
    public ImageSource? Thumb => _thumb ??= Images.FromBytes(Message?.Media?.Thumb, 54) ?? (IsVideo ? null : Images.FromFile(Path, 54));

    /// <summary>The message's preview, shown large until the file is there.</summary>
    public ImageSource? Preview => Images.FromBytes(Message?.Media?.Thumb);

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (Set(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(CurrentBorder));
                OnPropertyChanged(nameof(CurrentOpacity));
            }
        }
    }

    public Thickness CurrentBorder => _isCurrent ? new Thickness(2) : new Thickness(0);
    public double CurrentOpacity => _isCurrent ? 1 : 0.55;
}

/// <summary>
/// Photos and videos over the whole window. A photo zooms and moves; the
/// arrows, the arrow keys, a swipe and the strip along the bottom go through
/// every photo and video of the chat, loading more of them on the way.
/// </summary>
public sealed partial class MediaViewer : UserControl
{
    private const int PageSize = 60;
    private const int LoadAhead = 6;

    private readonly ObservableCollection<ViewerItem> _items = [];
    private ViewerItem? _current;
    private string? _chat;
    private string _chatName = "";
    private string? _pictureName;
    private bool _hasOlder;
    private bool _hasNewer;
    private bool _loadingOlder;
    private bool _loadingNewer;
    private int _version;

    public MediaViewer()
    {
        InitializeComponent();
        Strip.ItemsSource = _items;
    }

    public Session Session => App.Current.Session;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The bar on top, which moves the window like a title bar.</summary>
    public FrameworkElement TitleArea => HeaderBar;

    /// <summary>Room on the right of the bar for the window's minimize, maximize and close.</summary>
    public double CaptionInset
    {
        set
        {
            var padding = new Thickness(16, 0, 16 + value, 0);
            if (HeaderBar.Padding != padding)
            {
                HeaderBar.Padding = padding;
            }
        }
    }

    /// <summary>Opens the photo or video of a message, with the others of its chat to go through.</summary>
    public void ShowMessage(MessageItem message)
    {
        int version = Reset();
        _chat = message.Chat;
        _chatName = Session.Current?.Jid == message.Chat ? Session.Current.Chat.Name : Session.Chats.Get(message.Chat)?.Name ?? "";
        var item = new ViewerItem(message.Data);
        _items.Add(item);
        Open();
        Show(item);
        _ = LoadAroundAsync(version, item);
    }

    /// <summary>Shows a profile photo at full size.</summary>
    public void ShowPicture(string path, string name)
    {
        Reset();
        _pictureName = name;
        var item = new ViewerItem(path);
        _items.Add(item);
        Open();
        Show(item);
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        Visibility = Visibility.Collapsed;
        Reset();
    }

    private int Reset()
    {
        StopVideo();
        Photo.Clear();
        _items.Clear();
        _current = null;
        _chat = null;
        _pictureName = null;
        _hasOlder = _hasNewer = false;
        _loadingOlder = _loadingNewer = false;
        return ++_version;
    }

    private void Open()
    {
        Visibility = Visibility.Visible;
        Focus(FocusState.Programmatic);
    }

    // ---- Showing one photo or video ----

    private async void Show(ViewerItem item)
    {
        if (_current is { } previous)
        {
            previous.IsCurrent = false;
        }
        _current = item;
        item.IsCurrent = true;
        StopVideo();
        Failure.Visibility = Visibility.Collapsed;
        ShowDetails(item);
        UpdateNavigation();
        LoadMoreIfNear(item);

        Photo.Visibility = Visibility.Visible;
        Video.Visibility = Visibility.Collapsed;
        ZoomTools.Visibility = item.IsVideo ? Visibility.Collapsed : Visibility.Visible;
        MediaData? media = item.Message?.Media;
        string? path = item.Path;
        Photo.Show(item.Preview, item.IsVideo ? null : path, media?.Width ?? 0, media?.Height ?? 0);

        if (path is null && item.Message is { } message)
        {
            Spinner.IsActive = true;
            try
            {
                path = await Session.DownloadAsync(message);
            }
            catch (BridgeException e)
            {
                if (_current == item)
                {
                    FailureText.Text = Loc.T("media.downloadFailed", ("error", e.Message));
                    Failure.Visibility = Visibility.Visible;
                }
                return;
            }
            finally
            {
                if (_current == item)
                {
                    Spinner.IsActive = false;
                }
            }
            if (_current != item)
            {
                return;
            }
            if (!item.IsVideo)
            {
                Photo.SetFile(path);
            }
        }

        if (item.IsVideo && path is not null)
        {
            Photo.Visibility = Visibility.Collapsed;
            Photo.Clear();
            Video.Visibility = Visibility.Visible;
            Video.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            if (Video.MediaPlayer is { } player)
            {
                player.IsLoopingEnabled = item.Message?.Kind == "gif";
            }
        }
    }

    private void ShowDetails(ViewerItem item)
    {
        if (item.Message is { } message)
        {
            string sender = message.FromMe ? Loc.T("common.you") : !string.IsNullOrEmpty(message.SenderName) ? message.SenderName : _chatName;
            EmojiText.SetText(Title, sender);
            Subtitle.Text = Formatting.ToLocal(message.Ts).ToString("g");
            bool hasCaption = !string.IsNullOrWhiteSpace(message.Text);
            RichText.SetSpans(Caption, hasCaption ? WhatsAppText.Parse(message.Text!, message.Mentions) : null);
            CaptionBox.Visibility = hasCaption ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            EmojiText.SetText(Title, _pictureName ?? "");
            Subtitle.Text = Loc.T("media.profilePhoto");
            CaptionBox.Visibility = Visibility.Collapsed;
        }
        ShowInChatButton.Visibility = item.Message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNavigation()
    {
        int index = _current is null ? -1 : _items.IndexOf(_current);
        PreviousButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = index >= 0 && index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        Strip.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_current is not null && _items.Count > 1)
        {
            Strip.ScrollIntoView(_current);
        }
    }

    private void StopVideo()
    {
        Video.MediaPlayer?.Pause();
        Video.Source = null;
        Spinner.IsActive = false;
    }

    private void Move(int step)
    {
        if (_current is null)
        {
            return;
        }
        int index = _items.IndexOf(_current) + step;
        if (index >= 0 && index < _items.Count)
        {
            Show(_items[index]);
        }
    }

    // ---- Loading the other photos and videos of the chat ----

    private async Task LoadAroundAsync(int version, ViewerItem item)
    {
        if (item.Message is not { } message)
        {
            return;
        }
        MessagesPage page;
        try
        {
            page = await Session.Client.GetMessagesAsync(message.Chat, around: message.Id, limit: PageSize, media: true);
        }
        catch (BridgeException)
        {
            return;
        }
        if (version != _version)
        {
            return;
        }
        int at = page.Messages.FindIndex(m => m.Id == message.Id);
        if (at < 0)
        {
            return;
        }
        _hasOlder = page.HasOlder;
        _hasNewer = page.HasNewer;
        for (int i = at - 1; i >= 0; i--)
        {
            _items.Insert(0, new ViewerItem(page.Messages[i]));
        }
        for (int i = at + 1; i < page.Messages.Count; i++)
        {
            _items.Add(new ViewerItem(page.Messages[i]));
        }
        UpdateNavigation();
    }

    private void LoadMoreIfNear(ViewerItem item)
    {
        int index = _items.IndexOf(item);
        if (index < LoadAhead && _hasOlder && !_loadingOlder)
        {
            _ = LoadOlderAsync(_version);
        }
        if (index >= _items.Count - LoadAhead && _hasNewer && !_loadingNewer)
        {
            _ = LoadNewerAsync(_version);
        }
    }

    private async Task LoadOlderAsync(int version)
    {
        if (_chat is null || _items.Count == 0 || _items[0].Message is not { } oldest)
        {
            return;
        }
        _loadingOlder = true;
        try
        {
            MessagesPage page = await Session.Client.GetMessagesAsync(_chat, before: new Cursor { Ts = oldest.Ts, Seq = oldest.Seq }, limit: PageSize, media: true);
            if (version != _version)
            {
                return;
            }
            _hasOlder = page.HasOlder;
            for (int i = page.Messages.Count - 1; i >= 0; i--)
            {
                _items.Insert(0, new ViewerItem(page.Messages[i]));
            }
            UpdateNavigation();
        }
        catch (BridgeException)
        {
            // The ones loaded stay; the next step tries again.
        }
        finally
        {
            if (version == _version)
            {
                _loadingOlder = false;
            }
        }
    }

    private async Task LoadNewerAsync(int version)
    {
        if (_chat is null || _items.Count == 0 || _items[^1].Message is not { } newest)
        {
            return;
        }
        _loadingNewer = true;
        try
        {
            MessagesPage page = await Session.Client.GetMessagesAsync(_chat, after: new Cursor { Ts = newest.Ts, Seq = newest.Seq }, limit: PageSize, media: true);
            if (version != _version)
            {
                return;
            }
            _hasNewer = page.HasNewer;
            foreach (MessageData message in page.Messages)
            {
                _items.Add(new ViewerItem(message));
            }
            UpdateNavigation();
        }
        catch (BridgeException)
        {
            // The ones loaded stay; the next step tries again.
        }
        finally
        {
            if (version == _version)
            {
                _loadingNewer = false;
            }
        }
    }

    // ---- Input ----

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The video's own controls use the keys while they have the focus.
        if (IsInside(e.OriginalSource as DependencyObject, Video))
        {
            return;
        }
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Left:
                Move(-1);
                break;
            case VirtualKey.Right:
                Move(1);
                break;
            case VirtualKey.Home when _items.Count > 0:
                Show(_items[0]);
                break;
            case VirtualKey.End when _items.Count > 0:
                Show(_items[^1]);
                break;
            case VirtualKey.Add or (VirtualKey)187 when _current is { IsVideo: false }:
                Photo.ZoomIn();
                break;
            case VirtualKey.Subtract or (VirtualKey)189 when _current is { IsVideo: false }:
                Photo.ZoomOut();
                break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0 when _current is { IsVideo: false }:
                Photo.ZoomToFit();
                break;
            case VirtualKey.S when control:
                Save();
                break;
            // The caption copies its own selection.
            case VirtualKey.C when control && !IsInside(e.OriginalSource as DependencyObject, CaptionBox):
                Copy();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (element == ancestor)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Move(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Move(1);

    private void OnStripItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ViewerItem item && item != _current)
        {
            Show(item);
        }
        // The arrow keys keep going through the photos rather than moving along the strip.
        Focus(FocusState.Programmatic);
    }

    private void OnPhotoSwiped(object? sender, int direction) => Move(direction);

    private void OnPhotoEmptyTapped(object? sender, EventArgs e) => Close();

    private void OnPhotoZoomChanged(object? sender, EventArgs e)
    {
        ZoomText.Text = Photo.Zoom > 0 ? $"{Photo.Zoom * 100:0}%" : "";
        ZoomOutButton.IsEnabled = Photo.IsZoomed;
        ZoomLevel.IsEnabled = Photo.IsZoomed;
        ZoomInButton.IsEnabled = Photo.CanZoomIn;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Photo.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Photo.ZoomOut();

    private void OnZoomLevelClick(object sender, RoutedEventArgs e) => Photo.ZoomToFit();

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_current is { } item)
        {
            Show(item);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_current?.Path is { } path)
        {
            ConversationView.OpenFile(path);
        }
    }

    /// <summary>Right-click on the photo or video, or the menu key.</summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // The caption has its own menu for the text, and the buttons have none.
        var source = e.OriginalSource as DependencyObject;
        if (source != this && (!IsInside(source, Stage) || IsInside(source, CaptionBox) || IsInside(source, Failure)
            || IsInside(source, PreviousButton) || IsInside(source, NextButton)))
        {
            return;
        }
        if (_current is not { Path: { } path } item)
        {
            return;
        }
        e.Handled = true;

        var menu = new MenuFlyout();
        void Add(string text, string icon, Action action)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = WaIcons.PathIcon(icon) };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add(Loc.T("common.copy"), "Copy", Copy);
        Add(Loc.T("conversation.saveAs"), "Download", Save);
        Add(Loc.T("media.openInAnotherApp"), "OpenInNew", () => ConversationView.OpenFile(path));
        Add(Loc.T("conversation.showInFolder"), "Folder", () => Process.Start("explorer.exe", $"/select,\"{path}\""));
        if (item.Message is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(Loc.T("media.showInChat"), "Chat", ShowInChat);
        }

        if (e.TryGetPosition(this, out Windows.Foundation.Point point))
        {
            menu.ShowAt(this, point);
        }
        else
        {
            menu.ShowAt(Stage);
        }
    }

    /// <summary>
    /// Puts the photo on the clipboard as a picture, for pasting into a chat or
    /// an image editor, and as the file, for pasting into a folder. A video goes
    /// only as the file.
    /// </summary>
    private async void Copy()
    {
        if (_current is not { Path: { } path } item)
        {
            return;
        }
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage();
            package.SetStorageItems([file]);
            if (!item.IsVideo)
            {
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            }
            Clipboard.SetContent(package);
            // Keeps it on the clipboard after the app closes.
            Clipboard.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error($"Failed to copy {path}", e);
        }
    }

    private void OnShowInChatClick(object sender, RoutedEventArgs e) => ShowInChat();

    private async void ShowInChat()
    {
        if (_current?.Message is not { } message)
        {
            return;
        }
        Close();
        await App.Current.Window.ShowInChatAsync(message.Chat, message.Id);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_current?.Path is not { } path)
        {
            return;
        }
        string name = _current.Message is { } message
            ? $"WhatsApp {Formatting.ToLocal(message.Ts):yyyy-MM-dd HH.mm.ss}"
            : string.Concat((_pictureName ?? "").Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0)
        {
            name = Loc.T("media.profilePhoto");
        }
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = name };
        App.Current.InitializePicker(picker);
        string extension = System.IO.Path.GetExtension(path);
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        Windows.Storage.StorageFile? target = await picker.PickSaveFileAsync();
        if (target is not null)
        {
            File.Copy(path, target.Path, overwrite: true);
        }
    }
}
