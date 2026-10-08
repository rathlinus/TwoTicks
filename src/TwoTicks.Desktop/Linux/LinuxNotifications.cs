using Tmds.DBus.Protocol;

namespace TwoTicks.App.Linux;

/// <summary>
/// Notifications on a Linux desktop, through org.freedesktop.Notifications on
/// the session bus, which GNOME, KDE and the others all answer.
/// </summary>
/// <remarks>
/// What a desktop can do differs, and it says so: buttons need "actions", and
/// a box to reply in needs "inline-reply", which KDE has. Where there is no
/// box, a click opens the chat to reply there.
/// </remarks>
internal sealed class LinuxNotifications : ISystemNotifications
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string ReplyAction = "inline-reply";

    /// <summary>The name of the app's menu entry, by which a desktop finds its name and icon.</summary>
    public const string DesktopEntry = "io.github.rathlinus.TwoTicks";

    // What is on the screen, by the number the desktop gave it.
    private readonly Dictionary<uint, SystemNotification> _shown = [];
    private readonly Task<Connection?> _ready;
    private HashSet<string> _capabilities = [];

    public LinuxNotifications() => _ready = StartAsync();

    public event Action<string, string?>? Activated;

    public string Icon { private get; set; } = "";

    private async Task<Connection?> StartAsync()
    {
        Connection? bus = await SessionBus.GetAsync().ConfigureAwait(false);
        if (bus is null)
        {
            return null;
        }
        try
        {
            await bus.AddMatchAsync(Signal("ActionInvoked"),
                static (Message m, object? _) =>
                {
                    Reader reader = m.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Text: reader.ReadString());
                },
                (Exception? error, (uint Id, string Text) signal, object? _, object? _) =>
                {
                    if (error is null)
                    {
                        OnAction(signal.Id, signal.Text, null);
                    }
                },
                ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);
            await bus.AddMatchAsync(Signal("NotificationReplied"),
                static (Message m, object? _) =>
                {
                    Reader reader = m.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Text: reader.ReadString());
                },
                (Exception? error, (uint Id, string Text) signal, object? _, object? _) =>
                {
                    if (error is null)
                    {
                        OnAction(signal.Id, ReplyAction, signal.Text);
                    }
                },
                ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);
            await bus.AddMatchAsync(Signal("NotificationClosed"),
                static (Message m, object? _) => m.GetBodyReader().ReadUInt32(),
                (Exception? error, uint id, object? _, object? _) =>
                {
                    if (error is null)
                    {
                        lock (_shown)
                        {
                            _shown.Remove(id);
                        }
                    }
                },
                ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);

            string[] capabilities = await bus.CallMethodAsync(Call(bus, "GetCapabilities", null),
                static (Message m, object? _) => m.GetBodyReader().ReadArrayOfString()).ConfigureAwait(false);
            _capabilities = [.. capabilities];
            Log.Info("Notifications: the desktop can do " + string.Join(", ", capabilities));
            SelfCheck.Note("Notification server", string.Join(", ", capabilities));
            return bus;
        }
        catch (Exception e)
        {
            // No desktop answered: a session without a notification service.
            Log.Info("Notifications are not available: " + e.Message);
            SelfCheck.Note("Notification server", "none: " + e.Message);
            return null;
        }
    }

    private static MatchRule Signal(string member) => new()
    {
        Type = MessageType.Signal,
        Path = ObjectPath,
        Interface = Service,
        Member = member,
    };

    private static MessageBuffer Call(Connection bus, string member, string? signature)
    {
        using MessageWriter writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Service, member, signature);
        return writer.CreateMessage();
    }

    public async void Show(SystemNotification notification)
    {
        try
        {
            if (await _ready.ConfigureAwait(false) is not { } bus)
            {
                return;
            }
            // One with a tag takes the place of the one before it.
            uint replaces = 0;
            if (notification.Tag is not null)
            {
                lock (_shown)
                {
                    replaces = _shown.FirstOrDefault(s => s.Value.Group == notification.Group && s.Value.Tag == notification.Tag).Key;
                }
            }
            uint id = await bus.CallMethodAsync(Notify(bus, notification, replaces),
                static (Message m, object? _) => m.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
            lock (_shown)
            {
                _shown[id] = notification;
            }
        }
        catch (Exception e)
        {
            Log.Error("Failed to show a notification", e);
        }
    }

    private MessageBuffer Notify(Connection bus, SystemNotification notification, uint replaces)
    {
        bool markup = _capabilities.Contains("body-markup");
        var actions = new List<string>();
        if (_capabilities.Contains("actions"))
        {
            // The click on the notification itself.
            actions.Add("default");
            actions.Add("");
            if (notification.Reply is { } reply && _capabilities.Contains(ReplyAction))
            {
                actions.Add(ReplyAction);
                actions.Add(reply.Label);
            }
            for (int i = 0; i < notification.Actions.Count; i++)
            {
                actions.Add("a" + i);
                actions.Add(notification.Actions[i].Label);
            }
        }

        // Not a using variable: the hints below take it by reference.
        MessageWriter writer = bus.GetMessageWriter();
        try
        {
            return Write(ref writer);
        }
        finally
        {
            writer.Dispose();
        }

        MessageBuffer Write(ref MessageWriter writer)
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Notify", "susssasa{sv}i");
            writer.WriteString(Notifier.AppId);
            writer.WriteUInt32(replaces);
            writer.WriteString(File.Exists(Icon) ? Icon : "");
            writer.WriteString(notification.Title);
            writer.WriteString(markup ? Escape(notification.Text) : notification.Text);
            writer.WriteArray(actions.ToArray());

            ArrayStart hints = writer.WriteDictionaryStart();
            Hint(ref writer, "desktop-entry");
            writer.WriteVariantString(DesktopEntry);
            Hint(ref writer, "category");
            writer.WriteVariantString(notification.Call ? "call.incoming" : "im.received");
            if (notification.Picture is { Length: > 0 } picture)
            {
                Hint(ref writer, "image-path");
                writer.WriteVariantString(picture);
            }
            if (notification.Call)
            {
                // Stays until answered.
                Hint(ref writer, "urgency");
                writer.WriteVariantByte(2);
            }
            if (notification.Sound)
            {
                Hint(ref writer, "sound-name");
                writer.WriteVariantString("message-new-instant");
            }
            else
            {
                Hint(ref writer, "suppress-sound");
                writer.WriteVariantBool(true);
            }
            if (notification.Reply is { } box && _capabilities.Contains(ReplyAction))
            {
                Hint(ref writer, "x-kde-reply-placeholder-text");
                writer.WriteVariantString(box.Placeholder);
            }
            writer.WriteDictionaryEnd(hints);

            // How long it shows: as long as the desktop likes, and a call until it is answered.
            writer.WriteInt32(notification.Call ? 0 : -1);
            return writer.CreateMessage();
        }

        static void Hint(ref MessageWriter writer, string name)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(name);
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private void OnAction(uint id, string action, string? reply)
    {
        SystemNotification? notification;
        lock (_shown)
        {
            _shown.TryGetValue(id, out notification);
        }
        if (notification is null)
        {
            return;
        }
        string? arguments = action switch
        {
            "default" => notification.Arguments,
            ReplyAction => notification.Reply?.Arguments,
            _ when action.StartsWith('a') && int.TryParse(action.AsSpan(1), out int index) && index < notification.Actions.Count => notification.Actions[index].Arguments,
            _ => null,
        };
        if (arguments is not null)
        {
            Activated?.Invoke(arguments, reply);
        }
    }

    public void Remove(string group, string? tag = null)
    {
        List<uint> ids;
        lock (_shown)
        {
            ids = _shown.Where(s => s.Value.Group == group && (tag is null || s.Value.Tag == tag)).Select(s => s.Key).ToList();
        }
        Close(ids);
    }

    public void RemoveAll()
    {
        List<uint> ids;
        lock (_shown)
        {
            ids = [.. _shown.Keys];
        }
        // The app is about to quit: wait a moment for the desktop to take them.
        Close(ids).Wait(TimeSpan.FromMilliseconds(500));
    }

    private async Task Close(List<uint> ids)
    {
        try
        {
            if (ids.Count == 0 || !_ready.IsCompletedSuccessfully || _ready.Result is not { } bus)
            {
                return;
            }
            foreach (uint id in ids)
            {
                lock (_shown)
                {
                    _shown.Remove(id);
                }
                await bus.CallMethodAsync(CloseCall(bus, id)).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Log.Info("Could not remove a notification: " + e.Message);
        }

        static MessageBuffer CloseCall(Connection bus, uint id)
        {
            using MessageWriter writer = bus.GetMessageWriter();
            writer.WriteMethodCallHeader(Service, ObjectPath, Service, "CloseNotification", "u");
            writer.WriteUInt32(id);
            return writer.CreateMessage();
        }
    }
}
