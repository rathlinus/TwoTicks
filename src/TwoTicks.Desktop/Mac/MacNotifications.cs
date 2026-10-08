using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TwoTicks.App.Mac;

/// <summary>
/// Notifications on macOS, through the notification centre: with buttons, and
/// with a box to reply in, which macOS shows when the notification is opened.
/// </summary>
/// <remarks>
/// macOS wants the kinds of notifications and their buttons announced before
/// one is shown. A kind here is a set of buttons: the first notification with
/// a new set announces it. What a button means travels with each notification.
/// The notification centre only takes notifications from an app bundle.
/// </remarks>
internal sealed unsafe class MacNotifications : ISystemNotifications
{
    private static MacNotifications? s_current;

    private readonly Dictionary<string, string> _categories = [];
    // The names of what is on the screen, by group, to take them away again.
    private readonly Dictionary<string, List<(string Identifier, string? Tag)>> _shown = [];
    private int _next;

    private MacNotifications()
    {
    }

    /// <summary>What shows notifications, or null where macOS takes none from the app.</summary>
    public static ISystemNotifications? Create()
    {
        if (!MacNative.IsAvailable)
        {
            return null;
        }
        if (MacNative.wa_notifications_start(&OnActivated) == 0)
        {
            Log.Info("Not running from an app bundle: macOS takes no notifications");
            return null;
        }
        return s_current = new MacNotifications();
    }

    public event Action<string, string?>? Activated;

    /// <summary>macOS shows the icon of the app bundle.</summary>
    public string Icon
    {
        set { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnActivated(byte* arguments, byte* reply)
    {
        try
        {
            if (Marshal.PtrToStringUTF8((nint)arguments) is { } text)
            {
                s_current?.Activated?.Invoke(text, reply is null ? null : Marshal.PtrToStringUTF8((nint)reply));
            }
        }
        catch (Exception e)
        {
            Log.Error("A click on a notification failed", e);
        }
    }

    public void Show(SystemNotification notification)
    {
        // The buttons, and what each of them means for this notification.
        var buttons = new List<string[]>();
        var meanings = new List<string[]> { (string[])["default", notification.Arguments] };
        if (notification.Reply is { } reply)
        {
            buttons.Add(["reply", reply.Label, "reply", reply.Placeholder]);
            meanings.Add(["reply", reply.Arguments]);
        }
        for (int i = 0; i < notification.Actions.Count; i++)
        {
            // A call is answered in its window, which has to come up for it.
            buttons.Add(["a" + i, notification.Actions[i].Label, notification.Call ? "front" : ""]);
            meanings.Add(["a" + i, notification.Actions[i].Arguments]);
        }

        string actions = MacNative.Rows(buttons);
        string identifier;
        lock (_shown)
        {
            if (!_categories.TryGetValue(actions, out string? category))
            {
                category = "kind" + _categories.Count;
                _categories[actions] = category;
                MacNative.wa_notifications_set_category(category, actions);
            }
            if (!_shown.TryGetValue(notification.Group, out List<(string Identifier, string? Tag)>? group))
            {
                _shown[notification.Group] = group = [];
            }
            // One with a tag takes the place of the one before it, which the same name does.
            identifier = notification.Tag is { } tag ? $"{notification.Group}/{tag}" : $"{notification.Group}/{_next++}";
            group.RemoveAll(shown => shown.Identifier == identifier);
            group.Add((identifier, notification.Tag));
            MacNative.wa_notifications_show(identifier, category, notification.Group, notification.Title, notification.Text,
                notification.Picture ?? "", notification.Sound ? 1 : 0, MacNative.Rows(meanings));
        }
    }

    public void Remove(string group, string? tag = null)
    {
        List<string> identifiers;
        lock (_shown)
        {
            if (!_shown.TryGetValue(group, out List<(string Identifier, string? Tag)>? shown))
            {
                return;
            }
            identifiers = shown.Where(s => tag is null || s.Tag == tag).Select(s => s.Identifier).ToList();
            shown.RemoveAll(s => tag is null || s.Tag == tag);
        }
        if (identifiers.Count > 0)
        {
            MacNative.wa_notifications_remove(string.Join('\n', identifiers));
        }
    }

    public void RemoveAll()
    {
        lock (_shown)
        {
            _shown.Clear();
        }
        MacNative.wa_notifications_remove_all();
    }
}
