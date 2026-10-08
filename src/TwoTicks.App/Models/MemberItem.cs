using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TwoTicks.Core;

namespace TwoTicks.App.Models;

/// <summary>A member of a group, as the group's info lists them.</summary>
public sealed class MemberItem(GroupMember data) : Observable
{
    private ImageSource? _avatar;

    public string Jid { get; } = data.Jid;
    public string Name { get; } = data.Name;
    public bool IsMe { get; } = data.Me;
    public bool IsAdmin { get; } = data.Admin;
    public Visibility AdminVisibility => IsAdmin ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the avatar was asked for since the list was shown.</summary>
    public bool AvatarRequested { get; set; }

    public ImageSource? Avatar { get => _avatar; set => Set(ref _avatar, value); }
}
