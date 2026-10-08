using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using WinWhatsApp.App.Controls;
using WinWhatsApp.App.Models;
using WinWhatsApp.App.Views;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>A message found by the search, as the results list shows it.</summary>
/// <param name="chatName">The line above the message: the chat, or who sent it in a list of one chat's messages.</param>
/// <param name="withSender">Whether the message starts with who sent it, when the line above does not say so.</param>
public sealed class SearchResult(MessageData message, string chatName, string query, bool withSender = true)
{
    public MessageData Message { get; } = message;
    public string ChatName { get; } = chatName;
    public string Time { get; } = Formatting.ChatListTime(message.Ts, DateTime.Now);
    public SearchSnippet Text { get; } = SearchSnippet
        .Find(MessagePreview.Describe(message.Kind, message.Text, message.Media?.Name, message.Media?.Seconds ?? 0, message.FromMe).Text, query)
        .WithPrefix(!withSender ? "" : message.FromMe ? Loc.T("main.senderPrefix", ("name", Loc.T("common.you"))) : message.SenderName is { Length: > 0 } s ? Loc.T("main.senderPrefix", ("name", s)) : "");
}

/// <summary>
/// The window: the chat list on the left, the open chat on the right, the login
/// screen when no phone is linked, and the viewer for photos and videos.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 480;
    private const double MinChatListWidth = 280;
    private const double MaxChatListWidth = 640;
    private const double ChatListShare = 0.4;

    private readonly ObservableCollection<ChatItem> _searchChats = [];
    private readonly ObservableCollection<SearchResult> _searchMessages = [];
    private readonly DispatcherQueueTimer _searchTimer;
    private bool _selecting;
    private bool _quitting;
    private bool _dragging;
    private double _dragStartX;
    private double _dragStartWidth;
    private double _chatListWidth;
    private RectInt32 _normalBounds;
    private ScrollViewer? _chatScroller;

    public MainWindow()
    {
        InitializeComponent();
        ExtendTitleBar();
        SetIcon(AppIcon.Folder(Session.Settings.WhatsAppIcon));
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnWindowChanged;
        Activated += OnActivated;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            double scale = DisplayScale();
            presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        }

        SettingsItem.Icon = WaIcons.PathIcon("Settings");
        MarkAllReadItem.Icon = WaIcons.PathIcon("Check");
        QuitItem.Icon = WaIcons.PathIcon("Logout");

        SearchChats.ItemsSource = _searchChats;
        SearchMessages.ItemsSource = _searchMessages;
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = SearchMessagesAsync();

        _chatListWidth = Math.Clamp(Session.Settings.ChatListWidth, MinChatListWidth, MaxChatListWidth);
        ChatColumn.Width = new GridLength(_chatListWidth);
        ApplyTheme();
        RestorePlacement();
        UpdateMaximizeButton();

        Session.PropertyChanged += OnSessionChanged;
        Session.Chats.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatList.ArchivedCount) or nameof(ChatList.ArchivedUnread))
            {
                UpdateArchivedButton();
            }
        };
        RefreshFromSession();
        // The chats may be there already, and then nothing changes to tell of them.
        UpdateArchivedButton();
    }

    public Session Session => App.Current.Session;

    // ---- Showing and hiding ----

    public void ShowAndActivate()
    {
        ShowWindow();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
        BringToFront();
    }

    /// <summary>In front of other windows, also when the app was not the one in use.</summary>
    private partial void BringToFront();

    /// <summary>Lets the window close for real, when the app quits.</summary>
    public void CloseForGood()
    {
        _quitting = true;
        SavePlacement();
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SavePlacement();
        if (!_quitting && Session.Settings.CloseToTray)
        {
            // Keeps running in the notification area, so messages keep arriving.
            args.Cancel = true;
            Viewer.Close();
            AudioPlayer.Stop();
            HideWindow();
            Session.SetWindowActive(false);
            return;
        }
        if (!_quitting)
        {
            args.Cancel = true;
            App.Current.Quit();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
#if HAS_UNO
        bool active = args.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated;
#else
        bool active = args.WindowActivationState != WindowActivationState.Deactivated;
#endif
        Session.SetWindowActive(active);
        // Dimmed while another window is in front, as Windows does.
        CaptionButtons.Opacity = active ? 1 : 0.5;
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange)
        {
            UpdateMaximizeButton();
        }
        if ((args.DidPositionChange || args.DidSizeChange) && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalBounds = new RectInt32 { X = sender.Position.X, Y = sender.Position.Y, Width = sender.Size.Width, Height = sender.Size.Height };
        }
    }

    private void SavePlacement()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (_normalBounds.Width > 0)
        {
            Session.Settings.Window = new WindowPlacement
            {
                X = _normalBounds.X,
                Y = _normalBounds.Y,
                Width = _normalBounds.Width,
                Height = _normalBounds.Height,
                Maximized = maximized,
            };
        }
        Session.Settings.ChatListWidth = _chatListWidth;
        SettingsStore.Save(Session.Settings);
    }

    /// <summary>The icon on the taskbar and in Alt+Tab, from the folder with AppIcon.ico.</summary>
    public void SetIcon(string assets) => AppWindow.SetIcon(Path.Combine(assets, "AppIcon.ico"));

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
        }
    }

    /// <summary>Shows restore instead of maximize while the window fills the screen.</summary>
    private void UpdateMaximizeButton()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        string glyph = maximized ? "" : "";
        if (MaximizeIcon.Glyph != glyph)
        {
            MaximizeIcon.Glyph = glyph;
            string name = maximized ? Loc.T("main.restore") : Loc.T("main.maximize");
            AutomationProperties.SetName(MaximizeButton, name);
            ToolTipService.SetToolTip(MaximizeButton, name);
        }
    }

    private static void SetPadding(Grid grid, Thickness padding)
    {
        if (grid.Padding != padding)
        {
            grid.Padding = padding;
        }
    }

    private static bool IsShown(UIElement element)
    {
        for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
                continue;
            }
            foreach (T inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    public void ApplyTheme()
    {
        Root.RequestedTheme = Session.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyTitleBarTheme();
    }

    /// <summary>Puts the number of unread chats in the window's name on the taskbar.</summary>
    public void SetUnread(int chats) => Title = chats > 0 ? $"({chats}) WinWhatsApp" : "WinWhatsApp";

    // ---- Following the session ----

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Session.NeedsLogin):
            case nameof(Session.State):
            case nameof(Session.StateMessage):
            case nameof(Session.Current):
            case nameof(Session.Error):
                RefreshFromSession();
                break;
        }
    }

    private void RefreshFromSession()
    {
        LoginPane.Visibility = Session.NeedsLogin ? Visibility.Visible : Visibility.Collapsed;

        string? connection = Session.ConnectionText;
        ConnectionBar.IsOpen = connection is not null && !Session.NeedsLogin;
        ConnectionBar.Message = connection ?? "";
        ConnectionBar.Severity = Session.State is "connecting" or "syncing" ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        UseHereButton.Visibility = Session.State == "replaced" ? Visibility.Visible : Visibility.Collapsed;

        bool open = Session.Current is not null;
        ConversationPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (ProfilePane.IsOpen && ProfilePane.ChatJid != Session.Current?.Jid)
        {
            ProfilePane.Close();
        }
        SelectCurrentChat();

        if (Session.Error is { } error)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
        }
        else
        {
            ErrorBar.IsOpen = false;
        }
    }

    private void OnErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) => Session.ClearError();

    private void OnUseHereClick(object sender, RoutedEventArgs e) => Session.UseHere();

    // ---- The chat list ----

    /// <summary>Opens a chat, as a click in the list or on a notification does.</summary>
    public async Task OpenChatAsync(string jid, string? aroundMessage = null)
    {
        if (Session.Chats.Get(jid) is { IsArchived: true } && !Session.Chats.ShowArchived)
        {
            ShowArchive(true);
        }
        await Session.OpenAsync(jid, aroundMessage);
    }

    private void SelectCurrentChat()
    {
        ChatItem? current = Session.Current?.Chat;
        if (ChatListView.SelectedItem != current)
        {
            _selecting = true;
            ChatListView.SelectedItem = current is not null && Session.Chats.Visible.Contains(current) ? current : null;
            _selecting = false;
        }
    }

    private async void OnChatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting)
        {
            return;
        }
        if (ChatListView.SelectedItem is ChatItem chat)
        {
            if (Session.Current?.Jid != chat.Jid)
            {
                await Session.OpenAsync(chat.Jid);
            }
        }
        else if (e.RemovedItems.Count > 0 && Session.Current is { } current && e.RemovedItems.Contains(current.Chat))
        {
            // The open chat moved in the list; ListView forgets the selection when rows move.
            SelectCurrentChat();
        }
    }

    private void OnChatListLoaded(object sender, RoutedEventArgs e)
    {
        if (_chatScroller is not null || Descendants<ScrollViewer>(ChatListView).FirstOrDefault() is not { } scroller)
        {
            return;
        }
        _chatScroller = scroller;
        scroller.RegisterPropertyChangedCallback(ScrollViewer.VerticalOffsetProperty, (_, _) => UpdateChatListShadows());
        scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => UpdateChatListShadows());
        UpdateChatListShadows();
    }

    /// <summary>Shows the shadow at an edge of the chat list when more chats lie beyond it.</summary>
    private void UpdateChatListShadows()
    {
        if (_chatScroller is not { } scroller)
        {
            return;
        }
        ChatListTopShadow.Opacity = scroller.VerticalOffset > 0.5 ? 1 : 0;
        ChatListBottomShadow.Opacity = scroller.VerticalOffset < scroller.ScrollableHeight - 0.5 ? 1 : 0;
    }

    private void OnChatContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ChatItem chat)
        {
            Session.RequestAvatar(chat);
        }
    }

    private void OnChatContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem)
        {
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        if (element is not ListViewItem { Content: ChatItem chat } container)
        {
            return;
        }
        e.Handled = true;
        var menu = new MenuFlyout();
        foreach (MenuFlyoutItemBase item in ChatMenus.Build(chat, Session))
        {
            menu.Items.Add(item);
        }
        if (e.TryGetPosition(container, out Point point))
        {
            menu.ShowAt(container, point);
        }
        else
        {
            menu.ShowAt(container);
        }
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var filter = Enum.Parse<ChatFilter>((string)((FrameworkElement)sender).Tag);
        Session.Chats.Filter = filter;
        AllFilter.IsChecked = filter == ChatFilter.All;
        UnreadFilter.IsChecked = filter == ChatFilter.Unread;
        GroupsFilter.IsChecked = filter == ChatFilter.Groups;
    }

    private void UpdateArchivedButton()
    {
        ChatList chats = Session.Chats;
        ArchivedButton.Visibility = !chats.ShowArchived && chats.ArchivedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchivedCount.Text = chats.ArchivedUnread > 0 ? chats.ArchivedUnread.ToString() : "";
    }

    private void OnArchivedClick(object sender, RoutedEventArgs e) => ShowArchive(true);

    private void OnBackFromArchiveClick(object sender, RoutedEventArgs e) => ShowArchive(false);

    private void ShowArchive(bool show)
    {
        Session.Chats.ShowArchived = show;
        BackButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PaneTitle.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ArchiveTitle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Filters.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        UpdateArchivedButton();
        SelectCurrentChat();
    }

    private async void OnMarkAllReadClick(object sender, RoutedEventArgs e)
    {
        foreach (ChatItem chat in Session.Chats.All.Where(c => c.HasUnread).ToList())
        {
            await Session.MarkReadAsync(chat.Jid);
        }
    }

    // ---- Search ----

    private void OnSearchTextChanged(object sender, TextChangedEventArgs args)
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            _searchChats.Clear();
            _searchMessages.Clear();
            return;
        }
        SearchPanel.Visibility = Visibility.Visible;

        _searchChats.Clear();
        foreach (ChatItem chat in Session.Chats.All
            .Where(c => c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || c.Jid.StartsWith(query.TrimStart('+'), StringComparison.Ordinal))
            .OrderByDescending(c => c.Ts)
            .Take(30))
        {
            _searchChats.Add(chat);
        }
        ChatsHeading.Visibility = _searchChats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task SearchMessagesAsync()
    {
        string query = SearchBox.Text.Trim();
        if (query.Length < 2)
        {
            _searchMessages.Clear();
            MessagesHeading.Visibility = Visibility.Collapsed;
            NoResults.Visibility = _searchChats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        try
        {
            List<MessageData> found = await Session.Client.SearchAsync(query, 60);
            if (SearchBox.Text.Trim() != query)
            {
                return;
            }
            _searchMessages.Clear();
            foreach (MessageData message in found)
            {
                _searchMessages.Add(new SearchResult(message, Session.Chats.Get(message.Chat)?.Name ?? message.Chat.Split('@')[0], query));
            }
            MessagesHeading.Visibility = _searchMessages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Visibility = _searchMessages.Count == 0 && _searchChats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (BridgeException ex)
        {
            Log.Error("Search failed", ex);
        }
    }

    private void EndSearch()
    {
        SearchBox.Text = "";
        SearchPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnSearchChatClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChatItem chat)
        {
            EndSearch();
            await OpenChatAsync(chat.Jid);
            ConversationPane.FocusComposer();
        }
    }

    private async void OnSearchMessageClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResult result)
        {
            // In the open chat the message may be loaded already; then no need to load the chat again.
            if (Session.Current?.Jid == result.Message.Chat && ConversationPane.ShowMessage(result.Message.Id))
            {
                return;
            }
            await OpenChatAsync(result.Message.Chat, result.Message.Id);
        }
    }

    // ---- Keyboard ----

    private void OnFindInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnNewChatInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = NewChatAsync();
    }

    private void OnNextChatInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveChat(1);
    }

    private void OnPreviousChatInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveChat(-1);
    }

    private void MoveChat(int step)
    {
        ObservableCollection<ChatItem> chats = Session.Chats.Visible;
        if (chats.Count == 0)
        {
            return;
        }
        int index = Session.Current is { } current ? chats.IndexOf(current.Chat) : -1;
        int next = ((index + step) % chats.Count + chats.Count) % chats.Count;
        _ = Session.OpenAsync(chats[next].Jid);
        ChatListView.ScrollIntoView(chats[next]);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Viewer.IsOpen)
        {
            args.Handled = true;
            Viewer.Close();
        }
        else if (ConversationPane.CloseMediaPreview())
        {
            args.Handled = true;
        }
        else if (ProfilePane.IsOpen)
        {
            args.Handled = true;
            if (!ProfilePane.GoBack())
            {
                ProfilePane.Close();
            }
        }
        else if (SearchPanel.Visibility == Visibility.Visible)
        {
            args.Handled = true;
            EndSearch();
        }
    }

    // ---- Resizing the chat list ----

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = Splitter.CapturePointer(e.Pointer);
        _dragStartX = e.GetCurrentPoint(MainArea).Position.X;
        _dragStartWidth = ChatColumn.ActualWidth;
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            double x = e.GetCurrentPoint(MainArea).Position.X;
            _chatListWidth = Math.Clamp(_dragStartWidth + x - _dragStartX, MinChatListWidth, ChatListLimit());
            ChatColumn.Width = new GridLength(_chatListWidth);
        }
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Splitter.ReleasePointerCaptures();
            Session.Settings.ChatListWidth = _chatListWidth;
        }
    }

    /// <summary>
    /// The list takes at most a share of the window, as in WhatsApp Web, so the chat
    /// keeps room in a narrow window. The width the user chose comes back when the
    /// window is wide enough again.
    /// </summary>
    private void OnMainAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Min(_chatListWidth, ChatListLimit());
        if (Math.Abs(ChatColumn.Width.Value - width) > 0.5)
        {
            ChatColumn.Width = new GridLength(width);
        }
    }

    private double ChatListLimit() =>
        MainArea.ActualWidth > 0
            ? Math.Clamp(MainArea.ActualWidth * ChatListShare, MinChatListWidth, MaxChatListWidth)
            : MaxChatListWidth;

    // ---- Contact and group info ----

    /// <summary>Opens the info of a person or group beside the open chat.</summary>
    public void ShowProfile(string jid, string name, bool isGroup) => ProfilePane.Show(jid, name, isGroup, Session.Current?.Jid);

    /// <summary>
    /// The info goes beside the chat when there is room for both, and over the
    /// right of the chat when the window is narrow.
    /// </summary>
    private void OnChatAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool beside = e.NewSize.Width >= 800;
        Grid.SetColumn(ProfilePane, beside ? 1 : 0);
        ProfilePane.HorizontalAlignment = beside ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ProfilePane.Width = Math.Min(400, e.NewSize.Width);
    }

    // ---- Dialogs ----

    private void OnNewChatClick(object sender, RoutedEventArgs e) => _ = NewChatAsync();

    private async Task NewChatAsync()
    {
        if (Session.NeedsLogin)
        {
            return;
        }
        var dialog = new NewChatDialog(Session) { XamlRoot = Content.XamlRoot, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.ChosenJid is { } jid)
        {
            EndSearch();
            await OpenChatAsync(jid);
            ConversationPane.FocusComposer();
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(Session) { XamlRoot = Content.XamlRoot, RequestedTheme = Root.ActualTheme };
        await dialog.ShowAsync();
        SettingsStore.Save(Session.Settings);
        ApplyTheme();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => App.Current.Quit();

    // ---- Viewing photos and videos ----

    /// <summary>Opens the photo or video of a message in the viewer.</summary>
    public void ShowMedia(MessageItem item) => Viewer.ShowMessage(item);

    /// <summary>Shows a profile photo at full size.</summary>
    public void ShowPicture(string path, string name) => Viewer.ShowPicture(path, name);

    /// <summary>Scrolls the chat to a message, opening the chat or its older messages first when needed.</summary>
    public async Task ShowInChatAsync(string chat, string id)
    {
        if (Session.Current?.Jid != chat || !ConversationPane.ShowMessage(id))
        {
            await OpenChatAsync(chat, id);
        }
    }
}
