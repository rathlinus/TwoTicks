using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>A notification as the app describes it to whichever system shows it.</summary>
/// <param name="Group">What it belongs to: a chat, calls, updates. All of a group are removed together.</param>
/// <param name="Tag">A name within the group for one that is replaced and removed on its own; null otherwise.</param>
/// <param name="Arguments">What a click on it means, as "action=open&amp;chat=…".</param>
/// <param name="Actions">Its buttons: what each means, and its text.</param>
/// <param name="Reply">What a typed reply means, with the text of its button and of the empty box; null for none.</param>
internal sealed record SystemNotification(
    string Group,
    string? Tag,
    string Title,
    string Text,
    string? Picture,
    string Arguments,
    IReadOnlyList<(string Arguments, string Label)> Actions,
    (string Arguments, string Label, string Placeholder)? Reply = null,
    bool Sound = true,
    bool Call = false);

/// <summary>What shows notifications on one system.</summary>
internal interface ISystemNotifications
{
    /// <summary>Something in a notification was pressed, with the reply typed into it, if any. Raised on a background thread.</summary>
    event Action<string, string?>? Activated;

    /// <summary>The icon that stands for the app, as a PNG file.</summary>
    string Icon { set; }

    void Show(SystemNotification notification);

    /// <summary>Removes the notifications of a group, or with a tag the one of that name.</summary>
    void Remove(string group, string? tag = null);

    void RemoveAll();
}

/// <summary>
/// Notifications for new messages and calls on macOS and Linux, with a box to
/// reply in where the system has one.
/// </summary>
/// <remarks>
/// The same events and methods as the Windows app's Notifier, which talks to
/// Windows; this one hands a description to the system's own way of showing
/// notifications. Clicks and replies come back while the app runs.
/// </remarks>
internal sealed class Notifier
{
    /// <summary>The name the system files the notifications under.</summary>
    public const string AppId = "TwoTicks";

    private const string IncomingCallGroup = "calls";
    private const string IncomingCallTag = "incoming";

    private ISystemNotifications? _system;

    /// <summary>A notification was clicked. Raised on a background thread.</summary>
    public event Action<string?>? Opened;

    /// <summary>Reply was pressed in a notification. Raised on a background thread.</summary>
    public event Action<string, string>? Replied;

    /// <summary>Mark as read was pressed in a notification. Raised on a background thread.</summary>
    public event Action<string>? MarkedRead;

    /// <summary>Answer was pressed in the notification of a call. Raised on a background thread.</summary>
    public event Action? CallAnswered;

    /// <summary>Decline was pressed in the notification of a call. Raised on a background thread.</summary>
    public event Action? CallDeclined;

    /// <summary>The notification of a call was clicked. Raised on a background thread.</summary>
    public event Action? CallOpened;

    /// <summary>Download was pressed in the notification of a new version. Raised on a background thread.</summary>
    public event Action? UpdateRequested;

    /// <param name="assets">The folder with AppIcon.png; see <see cref="AppIcon"/>.</param>
    public void Register(string assets)
    {
        try
        {
            _system = OperatingSystem.IsLinux() ? new Linux.LinuxNotifications()
                : OperatingSystem.IsMacOS() ? Mac.MacNotifications.Create()
                : null;
            if (_system is not null)
            {
                _system.Activated += OnActivated;
                SetIcon(assets);
            }
            SelfCheck.Note("Notifications", _system is null ? "none on this system" : _system.GetType().Name);
        }
        catch (Exception e)
        {
            Log.Error("Notifications are not available", e);
            SelfCheck.Note("Notifications", "failed: " + e.Message);
        }
    }

    /// <summary>Sets the icon the system shows with the notifications.</summary>
    public void SetIcon(string assets)
    {
        if (_system is not null)
        {
            _system.Icon = Path.Combine(assets, "AppIcon.png");
        }
    }

    /// <summary>Takes the app's notifications away, where clicking them would do nothing once the app quit.</summary>
    public void ClearAll() => Try(s => s.RemoveAll());

    public void ShowMessage(string chatName, MessageData message, string? avatarPath, bool showText, bool sound)
    {
        Preview preview = MessagePreview.Describe(message.Kind, message.Text, message.Media?.Name, message.Media?.Seconds ?? 0, message.FromMe);
        string text = showText ? preview.Text : Loc.T("notify.newMessage");
        if (showText && !string.IsNullOrEmpty(message.SenderName))
        {
            text = $"{message.SenderName}: {text}";
        }
        string chat = Uri.EscapeDataString(message.Chat);
        Show(new SystemNotification(
            Group: message.Chat,
            Tag: null,
            Title: chatName,
            Text: text,
            Picture: avatarPath,
            Arguments: $"action=open&chat={chat}",
            Actions: [($"action=read&chat={chat}", Loc.T("notify.markRead"))],
            Reply: ($"action=reply&chat={chat}", Loc.T("notify.reply"), Loc.T("notify.replyPlaceholder")),
            Sound: sound));
    }

    public void ShowCall(CallData call, string? avatarPath)
    {
        string text = (call.Group, call.Video) switch
        {
            (false, false) => Loc.T("notify.incomingVoiceCall"),
            (false, true) => Loc.T("notify.incomingVideoCall"),
            (true, false) => Loc.T("notify.incomingGroupVoiceCall"),
            (true, true) => Loc.T("notify.incomingGroupVideoCall"),
        };
        Show(new SystemNotification(
            Group: IncomingCallGroup,
            Tag: null,
            Title: call.Name,
            Text: text,
            Picture: avatarPath,
            Arguments: $"action=open&chat={Uri.EscapeDataString(call.From)}",
            Actions: [("action=dismiss", Loc.T("common.ok"))],
            Call: true));
    }

    /// <summary>A call that rings here, with buttons to answer and decline it. The app plays the ringtone.</summary>
    public void ShowIncomingCall(string name, string? avatarPath, bool video) => Show(new SystemNotification(
        Group: IncomingCallGroup,
        Tag: IncomingCallTag,
        Title: name,
        Text: video ? Loc.T("calls.incomingVideoCall") : Loc.T("calls.incomingVoiceCall"),
        Picture: avatarPath,
        Arguments: "action=showCall",
        Actions: [("action=decline", Loc.T("calls.decline")), ("action=answer", Loc.T("calls.answer"))],
        Sound: false,
        Call: true));

    /// <summary>Removes the notification of a call that stopped ringing.</summary>
    public void ClearIncomingCall() => Try(s => s.Remove(IncomingCallGroup, IncomingCallTag));

    /// <summary>Tells about a new version.</summary>
    /// <param name="canInstall">Whether this copy installs it itself; otherwise the button opens the download page.</param>
    public void ShowUpdate(string version, bool canInstall) => Show(new SystemNotification(
        Group: "updates",
        Tag: null,
        Title: Loc.T("notify.updateAvailable", ("version", version)),
        Text: canInstall ? Loc.T("notify.updateInstallText") : Loc.T("notify.updateDownloadText"),
        Picture: null,
        Arguments: "action=open",
        Actions:
        [
            ("action=update", canInstall ? Loc.T("notify.updateInstall") : Loc.T("notify.updateDownload")),
            ("action=dismiss", Loc.T("notify.updateLater")),
        ]));

    /// <summary>Removes the notifications of a chat once it was read.</summary>
    public void Clear(string chat) => Try(s => s.Remove(chat));

    private void Show(SystemNotification notification)
    {
        if (notification.Picture is { Length: > 0 } picture && !File.Exists(picture))
        {
            notification = notification with { Picture = null };
        }
        Try(s => s.Show(notification));
    }

    private void Try(Action<ISystemNotifications> action)
    {
        if (_system is null)
        {
            return;
        }
        try
        {
            action(_system);
        }
        catch (Exception e)
        {
            Log.Error("A notification failed", e);
        }
    }

    private void OnActivated(string argumentText, string? reply)
    {
        Dictionary<string, string> arguments = Parse(argumentText);
        arguments.TryGetValue("chat", out string? chat);
        arguments.TryGetValue("action", out string? action);
        switch (action)
        {
            case "reply" when chat is not null:
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    Replied?.Invoke(chat, reply);
                }
                break;
            case "read" when chat is not null:
                MarkedRead?.Invoke(chat);
                break;
            case "dismiss":
                break;
            case "answer":
                CallAnswered?.Invoke();
                break;
            case "decline":
                CallDeclined?.Invoke();
                break;
            case "showCall":
                CallOpened?.Invoke();
                break;
            case "update":
                UpdateRequested?.Invoke();
                break;
            default:
                Opened?.Invoke(chat);
                break;
        }
    }

    private static Dictionary<string, string> Parse(string arguments)
    {
        var result = new Dictionary<string, string>();
        foreach (string pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0)
            {
                result[pair[..equals]] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }
        return result;
    }
}
