using Microsoft.UI.Xaml.Controls;
using TwoTicks.App.Models;
using TwoTicks.Core;

namespace TwoTicks.App.Views;

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
            items.Add(Item(Loc.T("chats.markRead"), "Check", () => _ = session.MarkReadAsync(chat.Jid)));
        }
        else
        {
            items.Add(Item(Loc.T("chats.markUnread"), "Unread", () => _ = session.MarkUnreadAsync(chat)));
        }

        items.Add(chat.IsPinned
            ? Item(Loc.T("chats.unpin"), "Unpin", () => _ = session.SetPinnedAsync(chat, false))
            : Item(Loc.T("chats.pin"), "Pin", () => _ = session.SetPinnedAsync(chat, true)));

        if (chat.IsMuted)
        {
            items.Add(Item(Loc.T("chats.unmute"), "Notifications", () => _ = session.SetMutedAsync(chat, 0)));
        }
        else
        {
            var mute = new MenuFlyoutSubItem { Text = Loc.T("chats.mute"), Icon = Controls.WaIcons.PathIcon("Muted") };
            foreach ((string text, long seconds) in new[] { (Loc.T("chats.mute8Hours"), 8 * 3600L), (Loc.T("chats.muteWeek"), 7 * 24 * 3600L), (Loc.T("chats.muteAlways"), -1L) })
            {
                var entry = new MenuFlyoutItem { Text = text };
                entry.Click += (_, _) => _ = session.SetMutedAsync(chat, seconds);
                mute.Items.Add(entry);
            }
            items.Add(mute);
        }

        items.Add(chat.IsArchived
            ? Item(Loc.T("chats.unarchive"), "Unarchive", () => _ = session.SetArchivedAsync(chat, false))
            : Item(Loc.T("chats.archive"), "Archive", () => _ = session.SetArchivedAsync(chat, true)));
        return items;
    }
}
