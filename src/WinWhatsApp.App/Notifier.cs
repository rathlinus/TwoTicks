using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// Windows notifications for new messages and calls, with a box to reply in.
/// </summary>
/// <remarks>
/// WinWhatsApp is not installed as a package, so it registers the name and icon
/// Windows shows its notifications with itself. Clicks and replies come back as
/// events of the notification while the app runs, which it does for as long as
/// it shows notifications.
/// </remarks>
internal sealed class Notifier
{
    /// <summary>The name Windows files the notifications under, also the app's taskbar identity.</summary>
    public const string AppId = "WinWhatsApp";

    private const string ReplyInput = "reply";

    // Notifications whose clicks are still wanted; their events stop when they are collected.
    private readonly LinkedList<ToastNotification> _shown = new();
    private ToastNotifier? _notifier;

    /// <summary>A notification was clicked. Raised on a background thread.</summary>
    public event Action<string?>? Opened;

    /// <summary>Reply was pressed in a notification. Raised on a background thread.</summary>
    public event Action<string, string>? Replied;

    /// <summary>Mark as read was pressed in a notification. Raised on a background thread.</summary>
    public event Action<string>? MarkedRead;

    public void Register()
    {
        try
        {
            using (RegistryKey app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            {
                app.SetValue("DisplayName", "WinWhatsApp");
                app.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
            }
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception e)
        {
            Log.Error("Notifications are not available", e);
        }
    }

    /// <summary>Takes the app's notifications out of the notification centre, where clicking them would do nothing once the app quit.</summary>
    public void ClearAll()
    {
        try
        {
            ToastNotificationManager.History.Clear(AppId);
        }
        catch (Exception e)
        {
            Log.Error("Failed to clear notifications", e);
        }
    }

    public void ShowMessage(string chatName, MessageData message, string? avatarPath, bool showText, bool sound)
    {
        Preview preview = MessagePreview.Describe(message.Kind, message.Text, message.Media?.Name, message.Media?.Seconds ?? 0, message.FromMe);
        string text = showText ? preview.Text : "New message";
        if (showText && !string.IsNullOrEmpty(message.SenderName))
        {
            text = $"{message.SenderName}: {text}";
        }
        string chat = Escape(message.Chat);
        string timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Ts).ToString("yyyy-MM-ddTHH:mm:ssZ");

        string xml =
            $"<toast launch=\"action=open&amp;chat={chat}\" activationType=\"foreground\" displayTimestamp=\"{timestamp}\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            $"<text>{Escape(chatName)}</text><text>{Escape(text)}</text>{Logo(avatarPath)}" +
            "</binding></visual>" +
            "<actions>" +
            $"<input id=\"{ReplyInput}\" type=\"text\" placeHolderContent=\"Type a reply\"/>" +
            $"<action content=\"Reply\" arguments=\"action=reply&amp;chat={chat}\" activationType=\"foreground\" hint-inputId=\"{ReplyInput}\"/>" +
            $"<action content=\"Mark as read\" arguments=\"action=read&amp;chat={chat}\" activationType=\"foreground\"/>" +
            "</actions>" +
            (sound ? "" : "<audio silent=\"true\"/>") +
            "</toast>";
        Show(xml, GroupOf(message.Chat));
    }

    public void ShowCall(CallData call, string? avatarPath)
    {
        string kind = call.Video ? "video call" : "voice call";
        string xml =
            $"<toast launch=\"action=open&amp;chat={Escape(call.From)}\" activationType=\"foreground\" scenario=\"incomingCall\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            $"<text>{Escape(call.Name)}</text><text>Incoming {(call.Group ? "group " : "")}{kind}. Answer it on your phone.</text>{Logo(avatarPath)}" +
            "</binding></visual>" +
            "<actions><action content=\"OK\" arguments=\"action=dismiss\" activationType=\"foreground\"/></actions>" +
            "</toast>";
        Show(xml, "calls");
    }

    private static string Logo(string? avatarPath) =>
        !string.IsNullOrEmpty(avatarPath) && File.Exists(avatarPath)
            ? $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{Escape(new Uri(avatarPath).AbsoluteUri)}\"/>"
            : "";

    private void Show(string xml, string group)
    {
        if (_notifier is null)
        {
            return;
        }
        try
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            var toast = new ToastNotification(document) { Group = group };
            toast.Activated += OnActivated;
            toast.Dismissed += (sender, _) => Forget(sender);
            lock (_shown)
            {
                _shown.AddLast(toast);
                while (_shown.Count > 100)
                {
                    _shown.RemoveFirst();
                }
            }
            _notifier.Show(toast);
        }
        catch (Exception e)
        {
            Log.Error("Failed to show a notification", e);
        }
    }

    private void Forget(ToastNotification toast)
    {
        lock (_shown)
        {
            _shown.Remove(toast);
        }
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        if (args is not ToastActivatedEventArgs activated)
        {
            return;
        }
        Dictionary<string, string> arguments = Parse(activated.Arguments);
        arguments.TryGetValue("chat", out string? chat);
        arguments.TryGetValue("action", out string? action);
        switch (action)
        {
            case "reply" when chat is not null:
                if (activated.UserInput.TryGetValue(ReplyInput, out object? value) && value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    Replied?.Invoke(chat, text);
                }
                break;
            case "read" when chat is not null:
                MarkedRead?.Invoke(chat);
                break;
            case "dismiss":
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

    /// <summary>Removes the notifications of a chat once it was read.</summary>
    public void Clear(string chat)
    {
        try
        {
            ToastNotificationManager.History.RemoveGroup(GroupOf(chat), AppId);
        }
        catch (Exception)
        {
            // Nothing to remove.
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    // Notification groups are limited to 64 characters; JIDs stay well below.
    private static string GroupOf(string chat) => chat.Length <= 64 ? chat : chat[..64];
}
