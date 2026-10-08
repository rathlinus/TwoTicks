using Microsoft.UI.Dispatching;
using TwoTicks.App.Linux;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>
/// The icon in the notification area on Linux and in the menu bar on macOS. A
/// click opens the window; the menu has Open and Quit, and an entry for a new
/// version when there is one. The icon gets a red dot while chats are unread.
/// </summary>
/// <remarks>
/// Not every Linux desktop has a place for such icons. <see cref="IsShown"/>
/// says whether this one does: without the icon, closing the window must not
/// hide it, or there would be no way back to it.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly StatusNotifierItem? _item;
    private readonly Mac.MacStatusItem? _macItem;
    private string _assets;
    private bool _unread;
    private string? _updateVersion;

    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action? UpdateRequested;

    /// <param name="assets">The folder with Tray.ico and TrayUnread.ico; see <see cref="AppIcon"/>.</param>
    public TrayIcon(string assets)
    {
        _assets = assets;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                _item = new StatusNotifierItem();
                _item.Activated += () => _ui.TryEnqueue(() => OpenRequested?.Invoke());
                _item.SetIcon(IconFile);
                _item.SetMenu(Menu());
                _ = _item.StartAsync();
            }
            else if (OperatingSystem.IsMacOS())
            {
                _macItem = Mac.MacStatusItem.Create(IconFile, AppName.Shown, Menu());
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not show the icon in the notification area", e);
        }
    }

    /// <summary>Whether the icon is on the screen, so the window can be closed and found again.</summary>
    public bool IsShown => _item?.IsShown == true || _macItem is not null;

    /// <summary>The version the menu offers to get; null for none.</summary>
    public string? UpdateVersion
    {
        get => _updateVersion;
        set
        {
            if (_updateVersion != value)
            {
                _updateVersion = value;
                _item?.SetMenu(Menu());
                _macItem?.SetMenu(Menu());
            }
        }
    }

    private string IconFile => Path.Combine(_assets, _unread ? "TrayUnread.ico" : "Tray.ico");

    private List<TrayMenuItem> Menu()
    {
        var items = new List<TrayMenuItem> { new(1, Loc.T("notify.trayOpen", ("app", AppName.Shown)), () => _ui.TryEnqueue(() => OpenRequested?.Invoke())) };
        if (_updateVersion is not null)
        {
            items.Add(new(3, Loc.T("notify.trayInstall", ("version", _updateVersion)), () => _ui.TryEnqueue(() => UpdateRequested?.Invoke())));
        }
        items.Add(new(4, "", null));
        items.Add(new(2, Loc.T("notify.trayQuit"), () => _ui.TryEnqueue(() => QuitRequested?.Invoke())));
        return items;
    }

    /// <summary>Switches to the icons in another folder, and to the name that goes with them; see <see cref="AppName"/>.</summary>
    public void SetIcons(string assets)
    {
        _assets = assets;
        _item?.SetIcon(IconFile);
        _item?.SetTitle(AppName.Shown);
        _item?.SetMenu(Menu());
        _macItem?.SetIcon(IconFile);
        _macItem?.SetMenu(Menu());
    }

    /// <summary>Shows how many chats are unread, in the icon and its tooltip.</summary>
    public void SetUnread(int chats)
    {
        string tooltip = chats > 0 ? Loc.Plural("notify.trayUnread", chats, ("app", AppName.Shown)) : AppName.Shown;
        if (_unread != chats > 0)
        {
            _unread = chats > 0;
            _item?.SetIcon(IconFile);
            _macItem?.SetIcon(IconFile);
        }
        _item?.SetToolTip(tooltip);
        _macItem?.SetToolTip(tooltip);
    }

    public void Dispose()
    {
        _item?.Dispose();
        _macItem?.Dispose();
    }
}
