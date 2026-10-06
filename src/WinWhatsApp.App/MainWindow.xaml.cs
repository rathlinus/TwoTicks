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
public sealed class SearchResult(MessageData message, string chatName, string query)
{
    public MessageData Message { get; } = message;
    public string ChatName { get; } = chatName;
    public string Time { get; } = Formatting.ChatListTime(message.Ts, DateTime.Now);
    public SearchSnippet Text { get; } = SearchSnippet
        .Find(MessagePreview.Describe(message.Kind, message.Text, message.Media?.Name, message.Media?.Seconds ?? 0, message.FromMe).Text, query)
        .WithPrefix(message.FromMe ? "You: " : message.SenderName is { Length: > 0 } s ? s + ": " : "");
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
    private bool _titleBarPending;
    private RectInt32[] _captionRects = [];
    private RectInt32[] _passthroughRects = [];

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
    }

    public Session Session => App.Current.Session;

    private double DisplayScale() => Content?.XamlRoot?.RasterizationScale ?? Native.GetDpiForWindow(App.Current.WindowHandleOf(this)) / 96.0;

    // ---- Showing and hiding ----

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
        Native.SetForegroundWindow(App.Current.WindowHandleOf(this));
    }

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
            AppWindow.Hide();
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
        bool active = args.WindowActivationState != WindowActivationState.Deactivated;
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
            _normalBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void RestorePlacement()
    {
        WindowPlacement? placement = Session.Settings.Window;
        if (placement is { Width: > 0, Height: > 0 })
        {
            var bounds = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
            // Only where a screen still is.
            DisplayArea area = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;
            bounds.Width = Math.Min(bounds.Width, work.Width);
            bounds.Height = Math.Min(bounds.Height, work.Height);
            bounds.X = Math.Clamp(bounds.X, work.X, work.X + work.Width - bounds.Width);
            bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - bounds.Height);
            AppWindow.MoveAndResize(bounds);
            _normalBounds = bounds;
            if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        else
        {
            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            int width = Math.Min((int)(1180 * DisplayScale()), work.Width);
            int height = Math.Min((int)(780 * DisplayScale()), work.Height);
            AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
            _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
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

    // ---- The title bar ----

    /// <summary>
    /// Lets the app draw up to the top of the window, and hides the window's own
    /// minimize, maximize and close: Windows draws them at most 48 px tall, so the app
    /// draws its own, as tall as the headers. The headers move the window.
    /// </summary>
    private void ExtendTitleBar()
    {
        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        Root.LayoutUpdated += (_, _) => QueueTitleBarUpdate();
        Root.ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, SetCaptionColor);
        SetCaptionColor();
        AppWindow.Changed += (_, _) => QueueTitleBarUpdate();
    }

    /// <summary>
    /// Windows keeps a line of its frame above the app when the window is maximized,
    /// in the caption colour, which is light by default: it takes the colour of the headers.
    /// </summary>
    private void SetCaptionColor()
    {
        if (ChatPane.Background is SolidColorBrush { Color: var color })
        {
            uint colorRef = color.R | (uint)color.G << 8 | (uint)color.B << 16;
            Native.DwmSetWindowAttribute(App.Current.WindowHandleOf(this), Native.DWMWA_CAPTION_COLOR, ref colorRef, sizeof(uint));
        }
    }

    /// <summary>Updates the title bar once after the layout settles, not on every pass.</summary>
    private void QueueTitleBarUpdate()
    {
        if (!_titleBarPending)
        {
            _titleBarPending = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateTitleBar);
        }
    }

    /// <summary>
    /// Makes the headers on top move the window, as a title bar does, while their
    /// buttons stay buttons, and keeps the buttons on the right clear of the window's own.
    /// </summary>
    private void UpdateTitleBar()
    {
        _titleBarPending = false;
        if (Content?.XamlRoot is not { } root)
        {
            return;
        }
        double scale = root.RasterizationScale;
        double inset = CaptionButtons.ActualWidth;
        double strip = CaptionButtons.ActualHeight;

        bool viewer = Viewer.IsOpen;
        bool login = LoginPane.Visibility == Visibility.Visible;
        bool chat = ConversationPane.Visibility == Visibility.Visible;
        ConversationPane.CaptionInset = ProfilePane.IsOpen ? 0 : inset;
        ProfilePane.CaptionInset = inset;
        // When the chat beside the list is narrower than the window's buttons.
        SetPadding(PaneHeader, new Thickness(20, 0, 10 + Math.Max(0, inset - ChatArea.ActualWidth), 0));
        Viewer.CaptionInset = inset;
        // Light on the dark viewer.
        ElementTheme captionTheme = viewer ? ElementTheme.Dark : ElementTheme.Default;
        if (CaptionButtons.RequestedTheme != captionTheme)
        {
            CaptionButtons.RequestedTheme = captionTheme;
        }

        var caption = new List<RectInt32>();
        var passthrough = new List<RectInt32>();
        void Add(FrameworkElement area, double height)
        {
            if (area.ActualWidth <= 0 || area.ActualHeight <= 0 || !IsShown(area))
            {
                return;
            }
            caption.Add(ToWindow(area, Math.Min(height, area.ActualHeight)));
            // The buttons in a header stay clickable.
            foreach (ButtonBase button in Descendants<ButtonBase>(area))
            {
                if (button.ActualWidth > 0 && IsShown(button))
                {
                    passthrough.Add(ToWindow(button, button.ActualHeight));
                }
            }
        }
        RectInt32 ToWindow(FrameworkElement element, double height)
        {
            Rect bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, height));
            return new RectInt32(
                (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
        }

        if (viewer)
        {
            Add(Viewer.TitleArea, double.MaxValue);
        }
        else if (login)
        {
            Add(LoginPane, strip);
        }
        else
        {
            Add(PaneHeader, PaneHeader.ActualHeight);
            Add(chat ? ConversationPane.TitleArea : EmptyState, chat ? double.MaxValue : strip);
            if (ProfilePane.IsOpen)
            {
                Add(ProfilePane.TitleArea, double.MaxValue);
            }
        }

        foreach (Button button in (Button[])[MinimizeButton, MaximizeButton, CloseButton])
        {
            passthrough.Add(ToWindow(button, button.ActualHeight));
        }

        if (!caption.SequenceEqual(_captionRects) || !passthrough.SequenceEqual(_passthroughRects))
        {
            _captionRects = [.. caption];
            _passthroughRects = [.. passthrough];
            InputNonClientPointerSource source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, _captionRects);
            source.SetRegionRects(NonClientRegionKind.Passthrough, _passthroughRects);
        }
    }

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

    /// <summary>Closes as the window's own close button does, so closing to the tray still applies.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        Native.PostMessage(App.Current.WindowHandleOf(this), Native.WM_CLOSE, 0, 0);

    /// <summary>Shows restore instead of maximize while the window fills the screen.</summary>
    private void UpdateMaximizeButton()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        string glyph = maximized ? "" : "";
        if (MaximizeIcon.Glyph != glyph)
        {
            MaximizeIcon.Glyph = glyph;
            string name = maximized ? "Restore" : "Maximize";
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
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredTheme = Session.Settings.Theme switch
            {
                "Light" => TitleBarTheme.Light,
                "Dark" => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };
        }
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
            ProfilePane.Close();
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
