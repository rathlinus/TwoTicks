using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.App.Controls;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>
/// Contact info and group info: the profile photo, the name, the number and
/// about text of a person, or the description and members of a group, and
/// what belongs to the chat with them: its media, starred and kept messages,
/// whether it is muted, its disappearing messages, and clearing, blocking,
/// leaving or deleting it. A member opens their own info here, and the arrow
/// goes back to the group.
/// </summary>
public sealed partial class ProfileView : UserControl
{
    private const int PageSize = 60;

    private static (string Text, long Seconds)[] MuteDurations =>
        [(Loc.T("profile.mute8Hours"), 8 * 3600L), (Loc.T("profile.mute1Week"), 7 * 24 * 3600L), (Loc.T("profile.muteAlways"), -1L)];
    private static readonly long[] s_disappearingTimers = [0, 86400, 7 * 86400, 90 * 86400];

    private readonly ObservableCollection<MemberItem> _members = [];
    private readonly ObservableCollection<CommonGroupItem> _groups = [];
    private readonly ObservableCollection<MediaTile> _tiles = [];
    private readonly ObservableCollection<SearchResult> _results = [];
    private readonly Stack<Target> _back = [];
    private readonly DispatcherQueueTimer _searchTimer;
    private Target? _target;
    private ChatItem? _chat;
    private Task<string?>? _picture;
    private string? _previewPath;
    private int _version;
    private bool _blocked;

    // The list behind a row, when one is open: media, docs, links, starred, kept or search.
    private string? _page;
    private int _pageVersion;
    private Cursor? _oldest;
    private bool _hasOlder;
    private bool _loadingOlder;

    private sealed record Target(string Jid, string Name, bool IsGroup);

    public ProfileView()
    {
        InitializeComponent();
        MemberList.ItemsSource = _members;
        GroupsList.ItemsSource = _groups;
        MediaGrid.ItemsSource = _tiles;
        MessageList.ItemsSource = _results;

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = SearchAsync();
    }

    public Session Session => App.Current.Session;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The header, which moves the window like a title bar.</summary>
    public FrameworkElement TitleArea => HeaderBar;

    /// <summary>Room on the right of the header for the window's minimize, maximize and close.</summary>
    public double CaptionInset
    {
        set
        {
            var padding = new Thickness(10, 0, 16 + value, 0);
            if (HeaderBar.Padding != padding)
            {
                HeaderBar.Padding = padding;
            }
        }
    }

    /// <summary>The chat that was open when the info was opened from it.</summary>
    public string? ChatJid { get; private set; }

    private string DisplayName => _chat?.Name ?? _target?.Name ?? "";

    public void Show(string jid, string name, bool isGroup, string? chatJid)
    {
        ChatJid = chatJid;
        _back.Clear();
        Load(new Target(jid, name, isGroup));
        Visibility = Visibility.Visible;
    }

    public void Close()
    {
        _version++;
        _pageVersion++;
        Visibility = Visibility.Collapsed;
        _target = null;
        _picture = null;
        _back.Clear();
        _members.Clear();
        _groups.Clear();
        ClosePage();
        WatchChat(null);
        ChatJid = null;
    }

    /// <summary>Goes back from a list to the info, or from a member to the group. Returns false when there is nowhere to go back to.</summary>
    public bool GoBack()
    {
        if (_page is not null)
        {
            ClosePage();
            if (_target is { } target)
            {
                _ = LoadChatInfoAsync(target, _version);
            }
            return true;
        }
        if (_back.TryPop(out Target? previous))
        {
            Load(previous);
            return true;
        }
        return false;
    }

    private void Load(Target target)
    {
        int version = ++_version;
        _target = target;
        _members.Clear();
        _groups.Clear();
        ClosePage();
        ChatItem? chat = Session.Chats.Get(target.Jid);
        WatchChat(chat);
        bool isOpenChat = Session.Current?.Jid == target.Jid;

        BigAvatar.IsGroup = target.IsGroup;
        BigAvatar.Source = chat?.Avatar;
        _previewPath = chat?.AvatarPath;
        EmojiText.SetText(NameText, chat?.Name ?? target.Name);
        SubtitleText.Text = "";
        AboutSection.Visibility = Visibility.Collapsed;
        MembersHeading.Visibility = Visibility.Collapsed;
        GroupsSection.Visibility = Visibility.Collapsed;
        MediaSection.Visibility = Visibility.Collapsed;

        bool isSelf = !target.IsGroup && Session.Me?.Jid == target.Jid;
        MessageAction.Visibility = !target.IsGroup && !isSelf && !isOpenChat ? Visibility.Visible : Visibility.Collapsed;
        SearchAction.Visibility = chat is not null ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = MessageAction.Visibility == Visibility.Visible || chat is not null ? Visibility.Visible : Visibility.Collapsed;

        // What belongs to the chat shows only when there is one.
        ChatSection.Visibility = chat is not null ? Visibility.Visible : Visibility.Collapsed;
        KeptRow.Visibility = chat?.Data.Ephemeral > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowChatState();
        _blocked = false;
        BlockText.Text = Loc.T("profile.block", ("name", DisplayName));
        ClearRow.Visibility = chat is not null ? Visibility.Visible : Visibility.Collapsed;
        BlockRow.Visibility = !target.IsGroup && !isSelf ? Visibility.Visible : Visibility.Collapsed;
        // A group can be deleted only after leaving it; which of the two shows is known with its members.
        ExitRow.Visibility = Visibility.Collapsed;
        DeleteRow.Visibility = chat is not null && !target.IsGroup ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionsSection();

        if (FindScroller(MemberList) is { } scroller)
        {
            scroller.ChangeView(null, 0, null, true);
        }

        _picture = LoadPictureAsync(target, chat, version);
        _ = target.IsGroup ? LoadGroupAsync(target, version) : LoadPersonAsync(target, version, isSelf);
        if (chat is not null)
        {
            _ = LoadChatInfoAsync(target, version);
        }
    }

    private void UpdateActionsSection() =>
        ActionsSection.Visibility = new[] { ClearRow, BlockRow, ExitRow, DeleteRow }.Any(row => row.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ---- The profile ----

    /// <summary>Shows the small picture right away and the full one once it is here, and returns the full one.</summary>
    private async Task<string?> LoadPictureAsync(Target target, ChatItem? chat, int version)
    {
        try
        {
            if (chat?.AvatarPath is null)
            {
                string preview = await Session.Client.GetAvatarAsync(target.Jid);
                if (version == _version && preview.Length > 0)
                {
                    _previewPath = preview;
                    BigAvatar.Source = Images.Avatar(preview);
                }
            }
            string path = await Session.Client.GetPictureAsync(target.Jid);
            if (path.Length == 0)
            {
                return null;
            }
            if (version == _version)
            {
                BigAvatar.Source = Images.FromFile(path, 200);
            }
            return path;
        }
        catch (BridgeException e)
        {
            Log.Info($"No profile photo of {target.Jid}: {e.Message}");
            return null;
        }
    }

    private async Task LoadPersonAsync(Target target, int version, bool isSelf)
    {
        if (!isSelf)
        {
            _ = LoadCommonGroupsAsync(target, version);
        }
        try
        {
            ProfileData profile = await Session.Client.GetProfileAsync(target.Jid);
            if (version != _version)
            {
                return;
            }
            if (Session.Chats.Get(target.Jid) is null && profile.Name.Length > 0)
            {
                EmojiText.SetText(NameText, profile.Me ? Loc.T("profile.nameYou", ("name", profile.Name)) : profile.Name);
            }
            SubtitleText.Text = profile.Phone ?? "";
            if (!string.IsNullOrEmpty(profile.About))
            {
                AboutHeading.Text = Loc.T("profile.about");
                AboutHeading.Visibility = Visibility.Visible;
                EmojiText.SetText(AboutText, profile.About);
                AboutText.Visibility = Visibility.Visible;
                CreatedText.Visibility = Visibility.Collapsed;
                AboutSection.Visibility = Visibility.Visible;
            }
            if (profile.Me)
            {
                BlockRow.Visibility = Visibility.Collapsed;
                UpdateActionsSection();
            }
            SetBlocked(profile.Blocked);
        }
        catch (BridgeException e)
        {
            Log.Info($"No profile of {target.Jid}: {e.Message}");
        }
    }

    private async Task LoadGroupAsync(Target target, int version)
    {
        SubtitleText.Text = Loc.T("profile.group");
        bool member = false;
        try
        {
            GroupData group = await Session.Client.GetGroupInfoAsync(target.Jid);
            if (version != _version)
            {
                return;
            }
            if (group.Name.Length > 0)
            {
                EmojiText.SetText(NameText, group.Name);
            }
            SubtitleText.Text = Loc.Plural("profile.groupMembers", group.Members.Count);

            bool hasTopic = !string.IsNullOrWhiteSpace(group.Topic);
            AboutHeading.Text = Loc.T("profile.groupDescription");
            EmojiText.SetText(AboutText, group.Topic ?? "");
            AboutText.Visibility = hasTopic ? Visibility.Visible : Visibility.Collapsed;
            AboutHeading.Visibility = hasTopic ? Visibility.Visible : Visibility.Collapsed;
            string created = group.Created > 0 ? Formatting.ToLocal(group.Created).ToString("d") : "";
            CreatedText.Text = (group.CreatedBy, created) switch
            {
                ({ Length: > 0 } by, { Length: > 0 }) => Loc.T("profile.createdBy", ("name", by), ("date", created)),
                (_, { Length: > 0 }) => Loc.T("profile.created", ("date", created)),
                _ => "",
            };
            CreatedText.Visibility = CreatedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AboutSection.Visibility = hasTopic || CreatedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // You first, as in WhatsApp, then the names, then those known only by number.
            foreach (GroupMember m in group.Members
                .OrderBy(m => !m.Me)
                .ThenBy(m => m.Name.StartsWith('+'))
                .ThenBy(m => m.Name.TrimStart('~'), StringComparer.CurrentCultureIgnoreCase))
            {
                _members.Add(new MemberItem(m));
            }
            MembersCount.Text = Loc.Plural("profile.members", group.Members.Count);
            MembersHeading.Visibility = Visibility.Visible;
            member = group.Members.Any(m => m.Me);
        }
        catch (BridgeException e)
        {
            // Not a member any more, or offline: the group shows without its members.
            Log.Info($"No info of group {target.Jid}: {e.Message}");
            if (version != _version)
            {
                return;
            }
        }
        ShowMembership(member);
    }

    /// <summary>A member can exit the group; only who left it can delete the chat.</summary>
    private void ShowMembership(bool member)
    {
        ExitRow.Visibility = member ? Visibility.Visible : Visibility.Collapsed;
        DeleteRow.Visibility = !member && _chat is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionsSection();
    }

    private async Task LoadCommonGroupsAsync(Target target, int version)
    {
        try
        {
            List<CommonGroupData> groups = await Session.Client.GetCommonGroupsAsync(target.Jid);
            if (version != _version || groups.Count == 0)
            {
                return;
            }
            foreach (CommonGroupData data in groups)
            {
                var group = new CommonGroupItem(data);
                _groups.Add(group);
                _ = LoadGroupAvatarAsync(group);
            }
            GroupsCount.Text = Loc.Plural("profile.groupsInCommon", groups.Count);
            GroupsSection.Visibility = Visibility.Visible;
        }
        catch (BridgeException e)
        {
            Log.Info($"No groups in common with {target.Jid}: {e.Message}");
        }
    }

    private async Task LoadGroupAvatarAsync(CommonGroupItem group)
    {
        if (Session.Chats.Get(group.Jid) is { AvatarPath: not null } chat)
        {
            group.Avatar = chat.Avatar;
            return;
        }
        try
        {
            group.Avatar = Images.Avatar(await Session.Client.GetAvatarAsync(group.Jid));
        }
        catch (BridgeException)
        {
            // The group keeps the placeholder picture.
        }
    }

    private void OnMemberContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is MemberItem { AvatarRequested: false } member)
        {
            member.AvatarRequested = true;
            _ = LoadMemberAvatarAsync(member);
        }
    }

    private async Task LoadMemberAvatarAsync(MemberItem member)
    {
        if (Session.Chats.Get(member.Jid) is { AvatarPath: not null } chat)
        {
            member.Avatar = chat.Avatar;
            return;
        }
        try
        {
            member.Avatar = Images.Avatar(await Session.Client.GetAvatarAsync(member.Jid));
        }
        catch (BridgeException)
        {
            member.AvatarRequested = false;
        }
    }

    private void OnMemberClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MemberItem member && _target is { } current)
        {
            _back.Push(current);
            Load(new Target(member.Jid, member.Name, false));
        }
    }

    private async void OnCommonGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string jid })
        {
            await App.Current.Window.OpenChatAsync(jid);
        }
    }

    private async void OnPictureClick(object sender, RoutedEventArgs e)
    {
        if (_target is not { } target || _picture is not { } picture)
        {
            return;
        }
        // Offline, the small picture is better than nothing.
        string? path = await picture ?? _previewPath;
        if (path is not null && _target == target)
        {
            App.Current.Window.ShowPicture(path, Session.Chats.Get(target.Jid)?.Name ?? target.Name);
        }
    }

    private async void OnMessageClick(object sender, RoutedEventArgs e)
    {
        if (_target is { IsGroup: false } target)
        {
            await App.Current.Window.OpenChatAsync(target.Jid);
        }
    }

    // ---- The chat: media, starred and kept messages, muting, disappearing messages ----

    /// <summary>Follows the chat while its info is open, so muting it on the phone shows here too.</summary>
    private void WatchChat(ChatItem? chat)
    {
        if (_chat is not null)
        {
            _chat.PropertyChanged -= OnChatChanged;
        }
        _chat = chat;
        if (chat is not null)
        {
            chat.PropertyChanged += OnChatChanged;
        }
    }

    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatItem.IsMuted) or nameof(ChatItem.Name) or "")
        {
            ShowChatState();
        }
    }

    private void ShowChatState()
    {
        if (_chat is not { } chat)
        {
            return;
        }
        MuteSwitch.IsOn = chat.IsMuted;
        long until = chat.Data.MutedUntil;
        MuteUntilText.Text = chat.IsMuted && until > 0 ? Loc.T("profile.mutedUntil", ("time", Formatting.ToLocal(until).ToString("g"))) : "";
        MuteUntilText.Visibility = MuteUntilText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DisappearingText.Text = Formatting.Disappearing(chat.Data.Ephemeral);
        if (!_blocked)
        {
            BlockText.Text = Loc.T("profile.block", ("name", DisplayName));
        }
    }

    private async Task LoadChatInfoAsync(Target target, int version)
    {
        try
        {
            ChatInfoData info = await Session.Client.GetChatInfoAsync(target.Jid);
            if (version != _version)
            {
                return;
            }
            int count = info.Media + info.Docs + info.Links;
            MediaCount.Text = count.ToString();
            MediaSection.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowMediaStrip(info.Recent, target.IsGroup);
            KeptRow.Visibility = info.Kept > 0 || _chat?.Data.Ephemeral > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (BridgeException e)
        {
            Log.Info($"No info of chat {target.Jid}: {e.Message}");
        }
    }

    /// <summary>The newest photos and videos, four in a row under "Media, links and docs".</summary>
    private void ShowMediaStrip(List<MessageData> recent, bool isGroup)
    {
        MediaStrip.Children.Clear();
        var template = (DataTemplate)Resources["MediaTileTemplate"];
        for (int i = 0; i < Math.Min(4, recent.Count); i++)
        {
            MessageData message = recent[i];
            var button = new PlainButton
            {
                Height = 86,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                CornerRadius = new CornerRadius(8),
                Content = new MediaTile(message),
                ContentTemplate = template,
            };
            AutomationProperties.SetName(button, message.Kind == "image" ? Loc.T("preview.photo") : Loc.T("preview.video"));
            button.Click += (_, _) => App.Current.Window.ShowMedia(new MessageItem(message, isGroup));
            Grid.SetColumn(button, i);
            MediaStrip.Children.Add(button);
        }
        MediaStrip.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_chat is not { } chat)
        {
            return;
        }
        if (chat.IsMuted)
        {
            _ = Session.SetMutedAsync(chat, 0);
            return;
        }
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach ((string text, long seconds) in MuteDurations)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => _ = Session.SetMutedAsync(chat, seconds);
            menu.Items.Add(item);
        }
        menu.ShowAt(MuteSwitch);
    }

    private void OnDisappearingClick(object sender, RoutedEventArgs e)
    {
        if (_chat is not { } chat)
        {
            return;
        }
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (long seconds in s_disappearingTimers)
        {
            var item = new RadioMenuFlyoutItem { Text = Formatting.Disappearing(seconds), GroupName = "Timer", IsChecked = chat.Data.Ephemeral == seconds };
            item.Click += async (_, _) =>
            {
                if (await TryAsync(() => Session.Client.SetDisappearingAsync(chat.Jid, seconds)) && _chat == chat)
                {
                    DisappearingText.Text = Formatting.Disappearing(seconds);
                    if (seconds > 0)
                    {
                        KeptRow.Visibility = Visibility.Visible;
                    }
                }
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(DisappearingText);
    }

    // ---- Clearing, blocking, leaving and deleting ----

    private void SetBlocked(bool blocked)
    {
        _blocked = blocked;
        BlockText.Text = blocked ? Loc.T("profile.unblock", ("name", DisplayName)) : Loc.T("profile.block", ("name", DisplayName));
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_target is not { } target || _chat is null)
        {
            return;
        }
        if (await ConfirmAsync(Loc.T("profile.clearTitle"), Loc.T("profile.clearText"), Loc.T("profile.clearChat"))
            && await TryAsync(() => Session.Client.ClearChatAsync(target.Jid)) && _target == target)
        {
            _ = LoadChatInfoAsync(target, _version);
        }
    }

    private async void OnBlockClick(object sender, RoutedEventArgs e)
    {
        if (_target is not { IsGroup: false } target)
        {
            return;
        }
        bool block = !_blocked;
        if (block && !await ConfirmAsync(Loc.T("profile.blockTitle", ("name", DisplayName)), Loc.T("profile.blockText"), Loc.T("profile.blockAction")))
        {
            return;
        }
        if (await TryAsync(() => Session.Client.BlockAsync(target.Jid, block)) && _target == target)
        {
            SetBlocked(block);
        }
    }

    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (_target is not { IsGroup: true } target)
        {
            return;
        }
        if (await ConfirmAsync(Loc.T("profile.exitTitle", ("name", DisplayName)), Loc.T("profile.exitText"), Loc.T("profile.exitGroup"))
            && await TryAsync(() => Session.Client.LeaveGroupAsync(target.Jid)) && _target == target)
        {
            ShowMembership(false);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_target is not { } target)
        {
            return;
        }
        if (await ConfirmAsync(Loc.T("profile.deleteTitle"), Loc.T("profile.deleteText"), Loc.T("profile.deleteChat"))
            && await TryAsync(() => Session.Client.DeleteChatAsync(target.Jid)))
        {
            if (Session.Current?.Jid == target.Jid)
            {
                Session.CloseConversation();
            }
            Close();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string text, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = text,
            PrimaryButtonText = action,
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            // A dialog is not inside the window's content, so it does not take its theme by itself.
            RequestedTheme = ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (BridgeException e)
        {
            Session.ShowError(e.Message);
            return false;
        }
    }

    // ---- The lists behind the rows ----

    private void OnMediaClick(object sender, RoutedEventArgs e) => OpenPage("media");

    private void OnStarredClick(object sender, RoutedEventArgs e) => OpenPage("starred");

    private void OnKeptClick(object sender, RoutedEventArgs e) => OpenPage("kept");

    private void OnSearchClick(object sender, RoutedEventArgs e) => OpenPage("search");

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
        {
            OpenPage(page);
        }
    }

    private void OpenPage(string page)
    {
        if (_target is null)
        {
            return;
        }
        _page = page;
        int version = ++_pageVersion;
        _tiles.Clear();
        _results.Clear();
        _oldest = null;
        _hasOlder = false;

        bool media = page is "media" or "docs" or "links";
        TitleText.Text = page switch
        {
            "starred" => Loc.T("profile.starredMessages"),
            "kept" => Loc.T("profile.keptMessages"),
            "search" => Loc.T("profile.searchMessages"),
            _ => Loc.T("profile.mediaLinksDocs"),
        };
        CloseIcon.Kind = "Back";
        AutomationProperties.SetName(CloseButton, Loc.T("profile.back"));
        MediaTabs.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        MediaTab.IsChecked = page == "media";
        DocsTab.IsChecked = page == "docs";
        LinksTab.IsChecked = page == "links";
        SearchBox.Visibility = page == "search" ? Visibility.Visible : Visibility.Collapsed;
        MediaGrid.Visibility = page == "media" ? Visibility.Visible : Visibility.Collapsed;
        MessageList.Visibility = page == "media" ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        SubPage.Visibility = Visibility.Visible;

        if (page == "search")
        {
            SearchBox.Text = "";
            SearchBox.Focus(FocusState.Programmatic);
            return;
        }
        _ = LoadPageAsync(version);
    }

    private void ClosePage()
    {
        _page = null;
        _pageVersion++;
        _searchTimer.Stop();
        _tiles.Clear();
        _results.Clear();
        SubPage.Visibility = Visibility.Collapsed;
        bool isGroup = _target?.IsGroup == true;
        TitleText.Text = isGroup ? Loc.T("profile.groupInfo") : Loc.T("profile.contactInfo");
        CloseIcon.Kind = _back.Count > 0 ? "Back" : "Close";
        AutomationProperties.SetName(CloseButton, _back.Count > 0 ? Loc.T("profile.back") : Loc.T("common.close"));
    }

    /// <summary>Loads the newest messages of the list, or older ones when scrolled to the end.</summary>
    private async Task LoadPageAsync(int version)
    {
        if (_target is not { } target || _page is not { } page)
        {
            return;
        }
        _loadingOlder = true;
        try
        {
            MessagesPage result = await Session.Client.GetMessagesAsync(target.Jid, before: _oldest, limit: PageSize, filter: page);
            if (version != _pageVersion)
            {
                return;
            }
            // Oldest first as they come; the lists show the newest first.
            if (result.Messages.Count > 0)
            {
                _oldest = new Cursor { Ts = result.Messages[0].Ts, Seq = result.Messages[0].Seq };
            }
            _hasOlder = result.HasOlder;
            for (int i = result.Messages.Count - 1; i >= 0; i--)
            {
                MessageData message = result.Messages[i];
                if (page == "media")
                {
                    _tiles.Add(new MediaTile(message));
                }
                else
                {
                    _results.Add(new SearchResult(message, SenderOf(message, target), "", withSender: false));
                }
            }
            ShowEmpty(page);
        }
        catch (BridgeException e)
        {
            Session.ShowError(e.Message);
        }
        finally
        {
            if (version == _pageVersion)
            {
                _loadingOlder = false;
            }
        }
    }

    private void ShowEmpty(string page)
    {
        bool empty = page == "media" ? _tiles.Count == 0 : _results.Count == 0;
        EmptyText.Text = page switch
        {
            "media" => Loc.T("profile.noMedia"),
            "docs" => Loc.T("profile.noDocs"),
            "links" => Loc.T("profile.noLinks"),
            "starred" => Loc.T("profile.noStarred"),
            "kept" => Loc.T("profile.noKept"),
            _ => Loc.T("profile.noResults"),
        };
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SenderOf(MessageData message, Target target) =>
        message.FromMe ? Loc.T("common.you")
        : target.IsGroup && message.SenderName is { Length: > 0 } name ? name.TrimStart('~')
        : DisplayName;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task SearchAsync()
    {
        if (_target is not { } target || _page != "search")
        {
            return;
        }
        int version = ++_pageVersion;
        string query = SearchBox.Text.Trim();
        _results.Clear();
        if (query.Length == 0)
        {
            EmptyText.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            List<MessageData> found = await Session.Client.SearchAsync(query, 100, target.Jid);
            if (version != _pageVersion)
            {
                return;
            }
            foreach (MessageData message in found)
            {
                _results.Add(new SearchResult(message, SenderOf(message, target), query, withSender: false));
            }
            ShowEmpty("search");
        }
        catch (BridgeException e)
        {
            Session.ShowError(e.Message);
        }
    }

    /// <summary>Loads older messages once the end of the list comes into view.</summary>
    private void OnPageContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemIndex >= sender.Items.Count - 12
            && _hasOlder && !_loadingOlder && _page is not (null or "search"))
        {
            _ = LoadPageAsync(_pageVersion);
        }
    }

    private void OnMediaGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MediaGrid.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            double side = Math.Floor((e.NewSize.Width - MediaGrid.Padding.Left - MediaGrid.Padding.Right) / 3) - 4;
            panel.ItemWidth = side + 4;
            panel.ItemHeight = side + 4;
        }
    }

    private void OnMediaTileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaTile tile)
        {
            App.Current.Window.ShowMedia(new MessageItem(tile.Message, _target?.IsGroup == true));
        }
    }

    private async void OnMessageResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResult result)
        {
            await App.Current.Window.OpenChatAsync(result.Message.Chat, result.Message.Id);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!GoBack())
        {
            Close();
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? FindScroller(child)) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
