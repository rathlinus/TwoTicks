using System.Collections.ObjectModel;
using TwoTicks.Core;

namespace TwoTicks.App.Models;

public enum ChatFilter
{
    All,
    Unread,
    Groups,
}

/// <summary>
/// All chats, and the ones the list shows: in order, filtered, and changed in
/// place so the list keeps its scroll position and selection.
/// </summary>
public sealed class ChatList : Observable
{
    private readonly Dictionary<string, ChatItem> _chats = [];
    private bool _showArchived;
    private ChatFilter _filter;
    private string _search = "";
    private int _archivedCount;
    private int _archivedUnread;

    public ObservableCollection<ChatItem> Visible { get; } = [];

    public IEnumerable<ChatItem> All => _chats.Values;

    public bool ShowArchived
    {
        get => _showArchived;
        set { if (Set(ref _showArchived, value)) { Sync(); } }
    }

    public ChatFilter Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) { Sync(); } }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) { Sync(); } }
    }

    public int ArchivedCount { get => _archivedCount; private set => Set(ref _archivedCount, value); }
    public int ArchivedUnread { get => _archivedUnread; private set => Set(ref _archivedUnread, value); }

    /// <summary>Chats that are unread and not muted, as the badges count them.</summary>
    public int UnreadChats => _chats.Values.Count(c => c.HasUnread && !c.IsMuted);

    public event Action? UnreadChanged;

    public ChatItem? Get(string jid) => _chats.GetValueOrDefault(jid);

    /// <param name="keep">A chat to keep although the list does not have it: one just started, without messages yet.</param>
    public void ReplaceAll(IEnumerable<ChatData> list, string? keep = null)
    {
        var seen = new HashSet<string>();
        foreach (ChatData data in list)
        {
            seen.Add(data.Jid);
            if (_chats.TryGetValue(data.Jid, out ChatItem? item))
            {
                item.Update(data);
            }
            else
            {
                _chats[data.Jid] = new ChatItem(data);
            }
        }
        foreach (string gone in _chats.Keys.Where(k => !seen.Contains(k) && k != keep).ToList())
        {
            _chats.Remove(gone);
        }
        Sync();
    }

    public ChatItem Upsert(ChatData data)
    {
        if (_chats.TryGetValue(data.Jid, out ChatItem? item))
        {
            item.Update(data);
        }
        else
        {
            item = new ChatItem(data);
            _chats[data.Jid] = item;
        }
        Sync();
        return item;
    }

    public void Remove(string jid)
    {
        if (_chats.Remove(jid))
        {
            Sync();
        }
    }

    /// <summary>Whether the chat belongs in the list as it is filtered now.</summary>
    private bool Shows(ChatItem chat)
    {
        if (chat.Ts <= 0 && chat.Pinned <= 0)
        {
            // Started here but nothing written yet.
            return false;
        }
        if (_search.Length > 0)
        {
            return chat.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) || chat.Jid.StartsWith(_search.TrimStart('+'), StringComparison.Ordinal);
        }
        if (chat.IsArchived != _showArchived)
        {
            return false;
        }
        return _filter switch
        {
            ChatFilter.Unread => chat.HasUnread,
            ChatFilter.Groups => chat.IsGroup,
            _ => true,
        };
    }

    private static int Compare(ChatItem a, ChatItem b)
    {
        bool aPinned = a.Pinned > 0, bPinned = b.Pinned > 0;
        if (aPinned != bPinned)
        {
            return aPinned ? -1 : 1;
        }
        if (aPinned && a.Pinned != b.Pinned)
        {
            return b.Pinned.CompareTo(a.Pinned);
        }
        int byTime = b.Ts.CompareTo(a.Ts);
        return byTime != 0 ? byTime : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Brings the visible list in line with the chats, moving rows rather than rebuilding.</summary>
    public void Sync()
    {
        var desired = _chats.Values.Where(Shows).ToList();
        desired.Sort(Compare);

        for (int i = 0; i < desired.Count; i++)
        {
            ChatItem item = desired[i];
            if (i < Visible.Count && ReferenceEquals(Visible[i], item))
            {
                continue;
            }
            int existing = -1;
            for (int j = i + 1; j < Visible.Count; j++)
            {
                if (ReferenceEquals(Visible[j], item))
                {
                    existing = j;
                    break;
                }
            }
            if (existing >= 0)
            {
                Visible.Move(existing, i);
            }
            else
            {
                Visible.Insert(i, item);
            }
        }
        while (Visible.Count > desired.Count)
        {
            Visible.RemoveAt(Visible.Count - 1);
        }

        var archived = _chats.Values.Where(c => c.IsArchived).ToList();
        ArchivedCount = archived.Count;
        ArchivedUnread = archived.Count(c => c.HasUnread);
        OnPropertyChanged(nameof(UnreadChats));
        UnreadChanged?.Invoke();
    }
}
