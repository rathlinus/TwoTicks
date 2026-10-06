using Microsoft.UI.Xaml.Media;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Models;

/// <summary>A group that you and a person are both in, as their info lists it.</summary>
public sealed class CommonGroupItem(CommonGroupData data) : Observable
{
    private ImageSource? _avatar;

    public string Jid { get; } = data.Jid;
    public string Name { get; } = data.Name;
    public string Members { get; } = data.Members;

    public ImageSource? Avatar { get => _avatar; set => Set(ref _avatar, value); }
}
