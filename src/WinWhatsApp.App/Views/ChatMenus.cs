using Microsoft.UI.Xaml.Controls;
using WinWhatsApp.App.Models;

namespace WinWhatsApp.App.Views;

/// <summary>The menu of a chat, in the chat list and in the chat's header.</summary>
internal static class ChatMenus
{
    public static List<MenuFlyoutItemBase> Build(ChatItem chat, Session session)
    {
        var items = new List<MenuFlyoutItemBase>();
        MenuFlyoutItem Item(string text, string icon, Action action)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = Controls.WaIcons.PathIcon(icon) };
            entry.Click += (_, _) => action();
            return entry;
        }

        if (chat.HasUnread)
        {
            items.Add(Item("Mark as read", "Check", () => _ = session.MarkReadAsync(chat.Jid)));
        }
        else
        {
            items.Add(Item("Mark as unread", "Unread", () => _ = session.MarkUnreadAsync(chat)));
        }

        items.Add(chat.IsPinned
            ? Item("Unpin chat", "Unpin", () => _ = session.SetPinnedAsync(chat, false))
            : Item("Pin chat", "Pin", () => _ = session.SetPinnedAsync(chat, true)));

        if (chat.IsMuted)
        {
            items.Add(Item("Unmute notifications", "Notifications", () => _ = session.SetMutedAsync(chat, 0)));
        }
        else
        {
            var mute = new MenuFlyoutSubItem { Text = "Mute notifications", Icon = Controls.WaIcons.PathIcon("Muted") };
            foreach ((string text, long seconds) in new[] { ("For 8 hours", 8 * 3600L), ("For 1 week", 7 * 24 * 3600L), ("Always", -1L) })
            {
                var entry = new MenuFlyoutItem { Text = text };
                entry.Click += (_, _) => _ = session.SetMutedAsync(chat, seconds);
                mute.Items.Add(entry);
            }
            items.Add(mute);
        }

        items.Add(chat.IsArchived
            ? Item("Unarchive chat", "Unarchive", () => _ = session.SetArchivedAsync(chat, false))
            : Item("Archive chat", "Archive", () => _ = session.SetArchivedAsync(chat, true)));
        return items;
    }
}
