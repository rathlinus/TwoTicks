using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.App.Controls;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>
/// Contact info and group info: the profile photo, the name, the number and
/// about text of a person, or the description and members of a group. A member
/// opens their own info here, and the arrow goes back to the group.
/// </summary>
public sealed partial class ProfileView : UserControl
{
    private readonly ObservableCollection<MemberItem> _members = [];
    private readonly Stack<Target> _back = [];
    private Target? _target;
    private Task<string?>? _picture;
    private string? _previewPath;
    private int _version;

    private sealed record Target(string Jid, string Name, bool IsGroup);

    public ProfileView()
    {
        InitializeComponent();
        MemberList.ItemsSource = _members;
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
        Visibility = Visibility.Collapsed;
        _target = null;
        _picture = null;
        _back.Clear();
        _members.Clear();
        ChatJid = null;
    }

    private void Load(Target target)
    {
        int version = ++_version;
        _target = target;
        _members.Clear();
        ChatItem? chat = Session.Chats.Get(target.Jid);

        TitleText.Text = target.IsGroup ? "Group info" : "Contact info";
        CloseIcon.Kind = _back.Count > 0 ? "Back" : "Close";
        AutomationProperties.SetName(CloseButton, _back.Count > 0 ? "Back" : "Close");
        BigAvatar.IsGroup = target.IsGroup;
        BigAvatar.Source = chat?.Avatar;
        _previewPath = chat?.AvatarPath;
        EmojiText.SetText(NameText, chat?.Name ?? target.Name);
        SubtitleText.Text = "";
        AboutSection.Visibility = Visibility.Collapsed;
        MembersHeading.Visibility = Visibility.Collapsed;
        MessageButton.Visibility = !target.IsGroup && Session.Current?.Jid != target.Jid ? Visibility.Visible : Visibility.Collapsed;
        if (FindScroller(MemberList) is { } scroller)
        {
            scroller.ChangeView(null, 0, null, true);
        }

        _picture = LoadPictureAsync(target, chat, version);
        _ = target.IsGroup ? LoadGroupAsync(target, version) : LoadPersonAsync(target, version);
    }

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

    private async Task LoadPersonAsync(Target target, int version)
    {
        try
        {
            ProfileData profile = await Session.Client.GetProfileAsync(target.Jid);
            if (version != _version)
            {
                return;
            }
            if (Session.Chats.Get(target.Jid) is null && profile.Name.Length > 0)
            {
                EmojiText.SetText(NameText, profile.Me ? profile.Name + " (You)" : profile.Name);
            }
            SubtitleText.Text = profile.Phone ?? "";
            if (!string.IsNullOrEmpty(profile.About))
            {
                AboutHeading.Text = "About";
                AboutHeading.Visibility = Visibility.Visible;
                EmojiText.SetText(AboutText, profile.About);
                AboutText.Visibility = Visibility.Visible;
                CreatedText.Visibility = Visibility.Collapsed;
                AboutSection.Visibility = Visibility.Visible;
            }
        }
        catch (BridgeException e)
        {
            Log.Info($"No profile of {target.Jid}: {e.Message}");
        }
    }

    private async Task LoadGroupAsync(Target target, int version)
    {
        SubtitleText.Text = "Group";
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
            string count = group.Members.Count == 1 ? "1 member" : $"{group.Members.Count} members";
            SubtitleText.Text = "Group · " + count;

            bool hasTopic = !string.IsNullOrWhiteSpace(group.Topic);
            AboutHeading.Text = "Group description";
            EmojiText.SetText(AboutText, group.Topic ?? "");
            AboutText.Visibility = hasTopic ? Visibility.Visible : Visibility.Collapsed;
            AboutHeading.Visibility = hasTopic ? Visibility.Visible : Visibility.Collapsed;
            string created = group.Created > 0 ? Formatting.ToLocal(group.Created).ToString("d") : "";
            CreatedText.Text = (group.CreatedBy, created) switch
            {
                ({ Length: > 0 } by, { Length: > 0 }) => $"Created by {by}, {created}",
                (_, { Length: > 0 }) => $"Created {created}",
                _ => "",
            };
            CreatedText.Visibility = CreatedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AboutSection.Visibility = hasTopic || CreatedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // You first, as in WhatsApp, then the names, then those known only by number.
            foreach (GroupMember member in group.Members
                .OrderBy(m => !m.Me)
                .ThenBy(m => m.Name.StartsWith('+'))
                .ThenBy(m => m.Name.TrimStart('~'), StringComparer.CurrentCultureIgnoreCase))
            {
                _members.Add(new MemberItem(member));
            }
            MembersCount.Text = count;
            MembersHeading.Visibility = Visibility.Visible;
        }
        catch (BridgeException e)
        {
            // Not a member any more, or offline: the group shows without its members.
            Log.Info($"No info of group {target.Jid}: {e.Message}");
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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_back.TryPop(out Target? previous))
        {
            Load(previous);
        }
        else
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
