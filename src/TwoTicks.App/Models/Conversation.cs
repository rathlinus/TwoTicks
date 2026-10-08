using System.Collections.ObjectModel;
using TwoTicks.Core;

namespace TwoTicks.App.Models;

/// <summary>
/// The rows of an open chat: messages, with a label at each new day and, when
/// the chat opened with unread messages, a line above the first of them.
/// </summary>
public sealed class Conversation
{
    private readonly Dictionary<string, MessageItem> _byId = [];
    private UnreadItem? _unreadLine;

    public Conversation(ChatItem chat)
    {
        Chat = chat;
    }

    public ChatItem Chat { get; }
    public string Jid => Chat.Jid;
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Whether older messages are stored than the ones loaded.</summary>
    public bool HasOlder { get; private set; }

    /// <summary>Whether newer messages are stored than the ones loaded, after jumping to an old one.</summary>
    public bool HasNewer { get; private set; }

    public MessageItem? Find(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<MessageItem> Messages => Items.OfType<MessageItem>();

    public Cursor? Oldest => Messages.FirstOrDefault() is { } m ? new Cursor { Ts = m.Ts, Seq = m.Data.Seq } : null;
    public Cursor? Newest => Messages.LastOrDefault() is { } m ? new Cursor { Ts = m.Ts, Seq = m.Data.Seq } : null;

    public UnreadItem? UnreadLine => _unreadLine;

    /// <summary>Fills the conversation with a first page of messages.</summary>
    /// <param name="unread">How many of the newest incoming messages are unread; they get the unread line.</param>
    public void Load(MessagesPage page, int unread)
    {
        Items.Clear();
        _byId.Clear();
        _unreadLine = null;
        HasOlder = page.HasOlder;
        HasNewer = page.HasNewer;

        var rows = new List<object>();
        DateTime? day = null;
        MessageItem? previous = null;

        int unreadStart = -1;
        if (unread > 0)
        {
            int left = unread;
            for (int i = page.Messages.Count - 1; i >= 0 && left > 0; i--)
            {
                if (!page.Messages[i].FromMe && page.Messages[i].Kind != "system")
                {
                    unreadStart = i;
                    left--;
                }
            }
        }

        for (int i = 0; i < page.Messages.Count; i++)
        {
            var item = new MessageItem(page.Messages[i], Chat.IsGroup);
            if (day != item.Day)
            {
                rows.Add(new DayItem(item.Day));
                day = item.Day;
                previous = null;
            }
            if (i == unreadStart)
            {
                _unreadLine = new UnreadItem(unread);
                rows.Add(_unreadLine);
                previous = null;
            }
            item.IsFirstInRun = StartsRun(previous, item);
            rows.Add(item);
            _byId[item.Id] = item;
            previous = item;
        }
        foreach (object row in rows)
        {
            Items.Add(row);
        }
    }

    /// <summary>Puts older messages above the loaded ones. Returns how many rows were added.</summary>
    public int Prepend(MessagesPage page)
    {
        HasOlder = page.HasOlder;
        var fresh = page.Messages.Where(m => !_byId.ContainsKey(m.Id)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        // The label of the first loaded day moves up if the older messages end on that day.
        if (Items.Count > 0 && Items[0] is DayItem firstDay && Formatting.ToLocal(fresh[^1].Ts).Date == firstDay.Day)
        {
            Items.RemoveAt(0);
        }

        var rows = new List<object>();
        DateTime? day = null;
        MessageItem? previous = null;
        foreach (MessageData data in fresh)
        {
            var item = new MessageItem(data, Chat.IsGroup);
            if (day != item.Day)
            {
                rows.Add(new DayItem(item.Day));
                day = item.Day;
                previous = null;
            }
            item.IsFirstInRun = StartsRun(previous, item);
            rows.Add(item);
            _byId[item.Id] = item;
            previous = item;
        }
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            Items.Insert(0, rows[i]);
        }
        if (rows.Count < Items.Count && Items[rows.Count] is MessageItem next)
        {
            next.IsFirstInRun = StartsRun(previous, next);
        }
        return rows.Count;
    }

    /// <summary>Puts newer messages below the loaded ones, after jumping to an old message.</summary>
    public void AppendPage(MessagesPage page)
    {
        HasNewer = page.HasNewer;
        foreach (MessageData data in page.Messages)
        {
            if (!_byId.ContainsKey(data.Id))
            {
                Append(data);
            }
        }
    }

    /// <summary>Adds a new message or updates a known one. Returns the new row, or null for an update.</summary>
    public MessageItem? Upsert(MessageData data)
    {
        if (_byId.TryGetValue(data.Id, out MessageItem? existing))
        {
            string template = existing.TemplateKey;
            existing.Update(data);
            if (existing.TemplateKey != template)
            {
                // A row shows with the template chosen when it was added.
                int index = Items.IndexOf(existing);
                var replacement = new MessageItem(data, Chat.IsGroup) { IsFirstInRun = existing.IsFirstInRun };
                _byId[data.Id] = replacement;
                Items[index] = replacement;
            }
            return null;
        }
        if (HasNewer)
        {
            // The view shows older messages; the new one is there when scrolling down.
            return null;
        }
        return Append(data);
    }

    private MessageItem Append(MessageData data)
    {
        var item = new MessageItem(data, Chat.IsGroup);
        MessageItem? previous = Items.Count > 0 ? Items[^1] as MessageItem : null;
        MessageItem? lastMessage = Messages.LastOrDefault();
        if (lastMessage is null || lastMessage.Day != item.Day)
        {
            Items.Add(new DayItem(item.Day));
            previous = null;
        }
        item.IsFirstInRun = StartsRun(previous, item);
        Items.Add(item);
        _byId[item.Id] = item;
        return item;
    }

    public void Remove(string id)
    {
        if (!_byId.Remove(id, out MessageItem? item))
        {
            return;
        }
        int index = Items.IndexOf(item);
        Items.RemoveAt(index);
        // A day label with nothing left under it goes too.
        if (index > 0 && Items[index - 1] is DayItem && (index == Items.Count || Items[index] is DayItem))
        {
            Items.RemoveAt(index - 1);
        }
        else if (index < Items.Count && Items[index] is MessageItem next)
        {
            next.IsFirstInRun = StartsRun(index > 0 ? Items[index - 1] as MessageItem : null, next);
        }
    }

    /// <summary>Takes the unread line away, once the messages under it were seen for a while.</summary>
    public void RemoveUnreadLine()
    {
        if (_unreadLine is not null)
        {
            int index = Items.IndexOf(_unreadLine);
            _unreadLine = null;
            if (index >= 0)
            {
                Items.RemoveAt(index);
                if (index < Items.Count && Items[index] is MessageItem next)
                {
                    next.IsFirstInRun = StartsRun(index > 0 ? Items[index - 1] as MessageItem : null, next);
                }
            }
        }
    }

    private static bool StartsRun(MessageItem? previous, MessageItem item) =>
        previous is null || previous.Kind == "system" || item.Kind == "system" ||
        previous.FromMe != item.FromMe || previous.Data.Sender != item.Data.Sender ||
        item.Ts - previous.Ts > 10 * 60;
}
