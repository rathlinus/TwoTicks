using Microsoft.UI.Dispatching;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// What the window shows and does, between it and the WhatsApp helper: the
/// connection state, the chat list, the open chat, sending, and notifications.
/// Everything here runs on the UI thread; helper events are moved there.
/// </summary>
public sealed class Session : Observable
{
    private readonly DispatcherQueue _ui;
    private readonly Notifier _notifier;
    private readonly Dictionary<string, DispatcherQueueTimer> _typingTimers = [];
    private readonly DispatcherQueueTimer _chatsReload;
    private readonly DispatcherQueueTimer _markReadTimer;
    private readonly DispatcherQueueTimer _offlineTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly HashSet<string> _historyChats = [];

    private string _state = "starting";
    private string? _stateMessage;
    private string? _qrCode;
    private AccountData? _me;
    private int _syncProgress;
    private Conversation? _current;
    private string? _headerStatus;
    private string? _presenceText;
    private string? _groupMembersText;
    private bool _windowActive;
    private int _openVersion;
    private bool _loadingOlder;
    private bool _loadingNewer;
    private DateTime _lastTypingSent;
    private DispatcherQueueTimer? _typingStop;
    private string? _error;
    private bool _stateKnown;

    // Chat updates as they arrived, numbered, so that a list read before some
    // of them does not undo them. See ReloadChatsAsync.
    private readonly LinkedList<(long Number, ChatData Data)> _recentChatUpdates = new();
    private long _chatUpdateNumber;

    // Messages for the chat being opened, which arrive while its first page is read.
    private string? _openingJid;
    private List<MessageData>? _openingMessages;
    private bool _chatsLoaded;

    internal Session(DispatcherQueue ui, AppSettings settings, Notifier notifier)
    {
        _ui = ui;
        _notifier = notifier;
        Settings = settings;
        Client = new WhatsAppClient(Path.Combine(AppContext.BaseDirectory, "WinWhatsApp.Bridge.exe"), AppPaths.DataFolder,
            debug: Environment.GetEnvironmentVariable("WINWHATSAPP_DEBUG") == "1");

        _chatsReload = NewTimer(TimeSpan.FromMilliseconds(250), () => _ = ReloadChatsAsync());
        _markReadTimer = NewTimer(TimeSpan.FromMilliseconds(600), () => _ = MarkCurrentReadAsync());
        _offlineTimer = NewTimer(TimeSpan.FromSeconds(30), () => _ = SetOnlineAsync(false));
        _clockTimer = NewTimer(TimeSpan.FromMinutes(1), () =>
        {
            foreach (ChatItem chat in Chats.All)
            {
                chat.Refresh();
            }
        });
        _clockTimer.IsRepeating = true;
        _clockTimer.Start();

        Client.HelperStarted += HelperJob.Add;
        Client.StateChanged += s => Post(() => OnState(s));
        Client.QrCodeReceived += q => Post(() => QrCode = q.Code);
        Client.ChatUpdated += c => Post(() => OnChat(c));
        Client.ChatsChanged += c => Post(() => OnChatsChanged(c));
        Client.MessageUpdated += m => Post(() => OnMessage(m));
        Client.MessageDeleted += r => Post(() => _current?.Remove(r.Id));
        Client.StatusChanged += s => Post(() => OnStatus(s));
        Client.TypingChanged += t => Post(() => OnTyping(t));
        Client.PresenceChanged += p => Post(() => OnPresence(p));
        Client.AvatarChanged += a => Post(() => OnAvatar(a));
        Client.ChatMerged += m => Post(() => OnMerged(m));
        Client.ChatCleared += c => Post(() => OnCleared(c));
        Client.SyncProgress += s => Post(() => SyncProgress = s.Progress);
        Client.CallReceived += c => Post(() => OnCall(c));
        Client.SendFailed += r => Post(() => ShowError("A message could not be sent. Right-click it to try again."));
        Client.Failed += message => Post(() => ShowError(message));

        Chats.UnreadChanged += () => UnreadChanged?.Invoke(Chats.UnreadChats);
    }

    public WhatsAppClient Client { get; }
    public AppSettings Settings { get; }
    public ChatList Chats { get; } = new();

    /// <summary>Raised with the number of unread chats whenever it may have changed.</summary>
    public event Action<int>? UnreadChanged;

    /// <summary>A new message was added at the bottom of the open chat.</summary>
    public event Action<MessageItem>? MessageAppended;

    /// <summary>Another chat was opened, or the open one was loaded again.</summary>
    public event Action<string?>? ConversationOpened;

    private DispatcherQueueTimer NewTimer(TimeSpan interval, Action action)
    {
        DispatcherQueueTimer timer = _ui.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        return timer;
    }

    private void Post(Action action) => _ui.TryEnqueue(() =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error("Failed to handle an update", e);
        }
    });

    public void Start()
    {
        Client.Start();
        _ = ReloadChatsAsync();
    }

    // ---- Connection ----

    public string State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(NeedsLogin));
                OnPropertyChanged(nameof(ConnectionText));
            }
        }
    }

    public string? StateMessage { get => _stateMessage; private set { if (Set(ref _stateMessage, value)) { OnPropertyChanged(nameof(ConnectionText)); } } }
    public string? QrCode { get => _qrCode; private set => Set(ref _qrCode, value); }
    public AccountData? Me { get => _me; private set { if (Set(ref _me, value)) { OnPropertyChanged(nameof(NeedsLogin)); } } }
    public int SyncProgress { get => _syncProgress; private set => Set(ref _syncProgress, value); }

    /// <summary>Whether the login screen shows: the helper reported that no phone is linked.</summary>
    public bool NeedsLogin => _stateKnown && _me is null;

    /// <summary>A line about the connection for the top of the chat list, or null when all is well.</summary>
    public string? ConnectionText => _state switch
    {
        "connected" => null,
        "starting" => null,
        "qr" => null,
        "syncing" => "Syncing your chats from your phone…",
        "connecting" => "Connecting…",
        "replaced" => "WhatsApp is open on another computer or browser.",
        "banned" => "WhatsApp has temporarily banned this account. " + _stateMessage,
        "outdated" => "WhatsApp no longer accepts this version. Update WinWhatsApp.",
        "loggedOut" => "Logged out.",
        _ => "Can't connect to WhatsApp. " + _stateMessage,
    };

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public void ShowError(string message)
    {
        Log.Info("Shown to the user: " + message);
        Error = message;
    }

    public void ClearError() => Error = null;

    private void OnState(StateData state)
    {
        string previous = _state;
        if (!_stateKnown)
        {
            _stateKnown = true;
            OnPropertyChanged(nameof(NeedsLogin));
        }
        if (state.Me is not null)
        {
            Me = state.Me;
        }
        State = state.State;
        StateMessage = state.Message;

        switch (state.State)
        {
            case "loggedOut":
                Me = null;
                CloseConversation();
                Chats.ReplaceAll([]);
                QrCode = null;
                // A new helper starts linking from scratch.
                NewTimer(TimeSpan.FromSeconds(1), () => Client.Restart()).Start();
                break;
            case "connected" when previous != "connected":
                QrCode = null;
                _chatsReload.Start();
                if (_windowActive)
                {
                    _ = SetOnlineAsync(true);
                }
                break;
        }
    }

    public async Task ReloadQrAsync()
    {
        try
        {
            await Client.LoginAsync();
        }
        catch (BridgeException e)
        {
            ShowError(e.Message);
        }
    }

    /// <summary>Takes the session back after WhatsApp was opened elsewhere.</summary>
    public void UseHere() => Client.Restart();

    public async Task LogoutAsync()
    {
        try
        {
            await Client.LogoutAsync();
        }
        catch (BridgeException e)
        {
            ShowError(e.Message);
        }
    }

    // ---- Chat list ----

    private async Task ReloadChatsAsync()
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long since = _chatUpdateNumber;
            List<ChatData> list = await Client.GetChatsAsync();
            if (!_chatsLoaded)
            {
                _chatsLoaded = true;
                Log.Info($"Loaded {list.Count} chats in {watch.ElapsedMilliseconds} ms");
            }
            // A chat just started here has no messages yet, so the list leaves it out.
            string? keep = _current is { } open && !open.Messages.Any() ? open.Jid : null;
            Chats.ReplaceAll(list, keep);
            // Updates that arrived while the list was read are newer than it.
            foreach ((long number, ChatData data) in _recentChatUpdates)
            {
                if (number > since)
                {
                    Chats.Upsert(data);
                }
            }
            if (_current is not null && Chats.Get(_current.Jid) is null && list.Count > 0)
            {
                // Deleted on the phone.
                CloseConversation();
            }
        }
        catch (BridgeException e)
        {
            Log.Error("Failed to load the chats", e);
        }
    }

    private void OnChat(ChatData data)
    {
        _recentChatUpdates.AddLast((++_chatUpdateNumber, data));
        while (_recentChatUpdates.Count > 300)
        {
            _recentChatUpdates.RemoveFirst();
        }
        ChatItem chat = Chats.Upsert(data);
        if (_current?.Jid == data.Jid && (data.Unread > 0 || data.MarkedUnread) && _windowActive)
        {
            _markReadTimer.Start();
        }
        if (!chat.HasUnread)
        {
            _notifier.Clear(data.Jid);
        }
    }

    private void OnChatsChanged(ChatsChangedData data)
    {
        foreach (string chat in data.History ?? [])
        {
            _historyChats.Add(chat);
        }
        _chatsReload.Stop();
        _chatsReload.Start();

        if (_current is not null && _historyChats.Remove(_current.Jid))
        {
            // History sync brought messages for the open chat: older ones the
            // phone sent on request, or the first ones of a fresh link.
            _ = RefreshCurrentAsync();
        }
        _historyChats.Clear();
    }

    private async Task RefreshCurrentAsync()
    {
        Conversation? conversation = _current;
        if (conversation is null)
        {
            return;
        }
        if (!conversation.Messages.Any())
        {
            await OpenAsync(conversation.Jid);
            return;
        }
        int added = await LoadOlderAsync(force: true);
        if (added > 0)
        {
            ConversationOpened?.Invoke(null);
        }
    }

    /// <summary>Fetches the profile picture of a chat that came into view, once per session.</summary>
    public void RequestAvatar(ChatItem chat)
    {
        if (chat.AvatarRequested || _state != "connected")
        {
            return;
        }
        chat.AvatarRequested = true;
        _ = FetchAvatarAsync(chat);
    }

    private async Task FetchAvatarAsync(ChatItem chat)
    {
        try
        {
            string path = await Client.GetAvatarAsync(chat.Jid);
            chat.AvatarPath = string.IsNullOrEmpty(path) ? null : path;
        }
        catch (BridgeException)
        {
            chat.AvatarRequested = false;
        }
    }

    private void OnAvatar(AvatarData avatar)
    {
        if (Chats.Get(avatar.Jid) is { } chat)
        {
            chat.AvatarPath = string.IsNullOrEmpty(avatar.Path) ? null : avatar.Path;
        }
    }

    private void OnMerged(MergedData merged)
    {
        _chatsReload.Start();
        if (_current?.Jid == merged.From || _current?.Jid == merged.To)
        {
            // The messages moved in may fall anywhere in the loaded ones.
            _ = OpenAsync(merged.To);
        }
    }

    private void OnCleared(MessageRef cleared)
    {
        if (_current?.Jid == cleared.Chat)
        {
            _ = OpenAsync(cleared.Chat);
        }
    }

    public async Task SetMutedAsync(ChatItem chat, long seconds) => await Try(() => Client.MuteAsync(chat.Jid, seconds));
    public async Task SetPinnedAsync(ChatItem chat, bool pinned) => await Try(() => Client.PinAsync(chat.Jid, pinned));
    public async Task SetArchivedAsync(ChatItem chat, bool archived) => await Try(() => Client.ArchiveAsync(chat.Jid, archived));
    public async Task MarkUnreadAsync(ChatItem chat) => await Try(() => Client.MarkUnreadAsync(chat.Jid));
    public async Task MarkReadAsync(string jid)
    {
        _notifier.Clear(jid);
        await Try(() => Client.MarkReadAsync(jid));
    }

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (BridgeException e)
        {
            ShowError(e.Message);
        }
    }

    // ---- The open chat ----

    public Conversation? Current { get => _current; private set => Set(ref _current, value); }

    /// <summary>The line under the name in the chat's header.</summary>
    public string? HeaderStatus { get => _headerStatus; private set => Set(ref _headerStatus, value); }

    private void UpdateHeaderStatus()
    {
        ChatItem? chat = _current?.Chat;
        HeaderStatus = chat?.Typing ?? (chat?.IsGroup == true ? _groupMembersText : _presenceText);
    }

    public async Task OpenAsync(string jid, string? aroundMessage = null)
    {
        int version = ++_openVersion;
        try
        {
            // A chat without messages, as one started with New chat, joins the
            // list hidden, so that the updates its first message brings land here.
            ChatItem chat = Chats.Get(jid) ?? Chats.Upsert(await Client.GetChatAsync(jid));
            int unread = chat.Unread;
            _openingJid = jid;
            _openingMessages = [];
            MessagesPage page = aroundMessage is null
                ? await Client.GetMessagesAsync(jid, limit: Math.Clamp(unread + 20, 60, 400))
                : await Client.GetMessagesAsync(jid, around: aroundMessage, limit: 80);
            if (version != _openVersion)
            {
                return;
            }

            var conversation = new Conversation(chat);
            conversation.Load(page, aroundMessage is null ? unread : 0);
            // Messages that arrived while the page was read may not be in it.
            List<MessageData> arrived = _openingMessages ?? [];
            _openingJid = null;
            _openingMessages = null;
            foreach (MessageData message in arrived)
            {
                conversation.Upsert(message);
            }
            Current = conversation;
            _presenceText = null;
            _groupMembersText = null;
            UpdateHeaderStatus();
            ConversationOpened?.Invoke(aroundMessage);

            if (_windowActive)
            {
                await MarkCurrentReadAsync();
            }
            if (chat.IsGroup)
            {
                _ = LoadGroupMembersAsync(conversation);
            }
            else
            {
                _ = Client.SubscribePresenceAsync(jid).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        catch (BridgeException e)
        {
            ShowError(e.Message);
        }
    }

    public void CloseConversation()
    {
        _openVersion++;
        Current = null;
        HeaderStatus = null;
        ConversationOpened?.Invoke(null);
    }

    private async Task LoadGroupMembersAsync(Conversation conversation)
    {
        try
        {
            GroupData group = await Client.GetGroupInfoAsync(conversation.Jid);
            if (_current != conversation)
            {
                return;
            }
            IEnumerable<string> names = group.Members.Where(m => !m.Me).Select(m => m.Name).Order(StringComparer.CurrentCultureIgnoreCase);
            if (group.Members.Any(m => m.Me))
            {
                names = names.Append("You");
            }
            _groupMembersText = string.Join(", ", names);
            UpdateHeaderStatus();
        }
        catch (BridgeException)
        {
            // Not a member any more, or offline: the header just has no names.
        }
    }

    private async Task MarkCurrentReadAsync()
    {
        if (_current is { } conversation && (conversation.Chat.HasUnread))
        {
            await MarkReadAsync(conversation.Jid);
        }
    }

    public async Task<int> LoadOlderAsync(bool force = false)
    {
        Conversation? conversation = _current;
        if (conversation is null || _loadingOlder || (!conversation.HasOlder && !force) || conversation.Oldest is not { } oldest)
        {
            return 0;
        }
        _loadingOlder = true;
        try
        {
            MessagesPage page = await Client.GetMessagesAsync(conversation.Jid, before: oldest, limit: 60);
            return _current == conversation ? conversation.Prepend(page) : 0;
        }
        catch (BridgeException e)
        {
            Log.Error("Failed to load older messages", e);
            return 0;
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    public async Task<int> LoadNewerAsync()
    {
        Conversation? conversation = _current;
        if (conversation is not { HasNewer: true } || _loadingNewer || conversation.Newest is not { } newest)
        {
            return 0;
        }
        _loadingNewer = true;
        try
        {
            MessagesPage page = await Client.GetMessagesAsync(conversation.Jid, after: newest, limit: 80);
            if (_current != conversation)
            {
                return 0;
            }
            int before = conversation.Items.Count;
            conversation.AppendPage(page);
            return conversation.Items.Count - before;
        }
        catch (BridgeException e)
        {
            Log.Error("Failed to load newer messages", e);
            return 0;
        }
        finally
        {
            _loadingNewer = false;
        }
    }

    /// <summary>Asks the phone for messages older than the oldest stored one.</summary>
    public async Task RequestOlderFromPhoneAsync()
    {
        if (_current is { } conversation)
        {
            await Try(() => Client.RequestOlderAsync(conversation.Jid));
        }
    }

    private void OnMessage(MessageData message)
    {
        if (_openingJid == message.Chat)
        {
            _openingMessages?.Add(message);
        }
        if (_current?.Jid == message.Chat)
        {
            MessageItem? appended = _current.Upsert(message);
            if (appended is not null)
            {
                MessageAppended?.Invoke(appended);
                if (!message.FromMe && _windowActive)
                {
                    _markReadTimer.Start();
                }
            }
        }

        ChatItem? chat = Chats.Get(message.Chat);
        if (chat is not null && !message.FromMe && chat.Typing is not null)
        {
            ClearTyping(chat);
        }

        bool looking = _windowActive && _current?.Jid == message.Chat;
        if (message.Notify && Settings.Notifications && !looking)
        {
            _notifier.ShowMessage(chat?.Name ?? message.SenderName ?? message.Chat.Split('@')[0], message, chat?.AvatarPath,
                Settings.NotificationPreview, Settings.NotificationSound);
        }
    }

    private void OnStatus(StatusData status)
    {
        if (_current?.Jid != status.Chat)
        {
            return;
        }
        foreach (string id in status.Ids)
        {
            if (_current.Find(id) is { } item)
            {
                item.Data.Status = status.Status;
                item.Update(item.Data);
            }
        }
    }

    private void OnTyping(TypingData typing)
    {
        if (Chats.Get(typing.Chat) is not { } chat || (Me is not null && typing.Sender == Me.Jid))
        {
            return;
        }
        if (!typing.Typing)
        {
            ClearTyping(chat);
            return;
        }
        string action = typing.Audio ? "recording audio…" : "typing…";
        string? who = null;
        if (chat.IsGroup)
        {
            who = _current?.Jid == chat.Jid
                ? _current.Messages.LastOrDefault(m => m.Data.Sender == typing.Sender)?.SenderName
                : null;
            who ??= "Someone";
        }
        chat.Typing = who is null ? action : $"{who} is {action}";
        if (!_typingTimers.TryGetValue(chat.Jid, out DispatcherQueueTimer? timer))
        {
            timer = NewTimer(TimeSpan.FromSeconds(8), () => ClearTyping(chat));
            _typingTimers[chat.Jid] = timer;
        }
        timer.Stop();
        timer.Start();
        if (_current?.Jid == chat.Jid)
        {
            UpdateHeaderStatus();
        }
    }

    private void ClearTyping(ChatItem chat)
    {
        chat.Typing = null;
        if (_current?.Jid == chat.Jid)
        {
            UpdateHeaderStatus();
        }
    }

    private void OnPresence(PresenceData presence)
    {
        if (_current is not { } conversation || conversation.Jid != presence.Jid || conversation.Chat.IsGroup)
        {
            return;
        }
        _presenceText = presence.Online ? "online" : presence.LastSeen > 0 ? Formatting.LastSeen(presence.LastSeen, DateTime.Now) : null;
        UpdateHeaderStatus();
    }

    private void OnCall(CallData call)
    {
        if (Settings.Notifications)
        {
            _notifier.ShowCall(call, Chats.Get(call.From)?.AvatarPath);
        }
    }

    // ---- Writing ----

    public async Task<bool> SendTextAsync(string text, string? replyTo)
    {
        if (_current is not { } conversation)
        {
            return false;
        }
        StopTyping();
        try
        {
            MessageData message = await Client.SendTextAsync(conversation.Jid, text, replyTo);
            OnSent(message);
            return true;
        }
        catch (BridgeException e)
        {
            ShowError(e.Message);
            return false;
        }
    }

    /// <summary>Sends a reply typed into a notification.</summary>
    public async Task ReplyFromNotificationAsync(string chat, string text)
    {
        try
        {
            MessageData message = await Client.SendTextAsync(chat, text, null);
            OnSent(message);
            await Client.MarkReadAsync(chat);
        }
        catch (BridgeException e)
        {
            ShowError(e.Message);
        }
    }

    public async Task SendFileAsync(OutgoingMedia media)
    {
        try
        {
            MessageData message = await Client.SendMediaAsync(media);
            OnSent(message);
        }
        catch (BridgeException e)
        {
            ShowError($"{Path.GetFileName(media.Path)} could not be sent: {e.Message}");
        }
    }

    /// <summary>
    /// Shows a message just sent. The helper may have reported on it already,
    /// as sent or failed, before its answer to the request arrived; then that
    /// report is newer and the answer is left out.
    /// </summary>
    private void OnSent(MessageData message)
    {
        if (_current?.Jid == message.Chat && _current.Find(message.Id) is not null)
        {
            return;
        }
        OnMessage(message);
    }

    public async Task EditAsync(MessageItem item, string text) => await Try(() => Client.EditAsync(item.Chat, item.Id, text));
    public async Task ReactAsync(MessageItem item, string emoji) => await Try(() => Client.ReactAsync(item.Chat, item.Id, emoji));
    public async Task RevokeAsync(MessageItem item) => await Try(() => Client.RevokeAsync(item.Chat, item.Id));
    public async Task DeleteForMeAsync(MessageItem item) => await Try(() => Client.DeleteForMeAsync(item.Chat, item.Id));
    public async Task RetryAsync(MessageItem item) => await Try(() => Client.RetryAsync(item.Chat, item.Id));

    /// <summary>Downloads the file of a message, or returns it when it is already here.</summary>
    public async Task<string?> DownloadAsync(MessageItem item)
    {
        if (item.IsDownloaded)
        {
            return item.Data.Media!.Path;
        }
        item.IsBusy = true;
        try
        {
            string path = await Client.DownloadAsync(item.Chat, item.Id);
            if (item.Data.Media is { } media)
            {
                media.Path = path;
                item.Update(item.Data);
            }
            return path;
        }
        catch (BridgeException e)
        {
            ShowError($"Download failed: {e.Message}");
            return null;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    /// <summary>Tells the chat that the user types, at most every few seconds, and that they stopped after a pause.</summary>
    public void NotifyTyping()
    {
        if (_current is not { } conversation || _state != "connected")
        {
            return;
        }
        if (DateTime.UtcNow - _lastTypingSent > TimeSpan.FromSeconds(4))
        {
            _lastTypingSent = DateTime.UtcNow;
            _ = Client.SetTypingAsync(conversation.Jid, true).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        _typingStop ??= NewTimer(TimeSpan.FromSeconds(5), StopTyping);
        _typingStop.Stop();
        _typingStop.Start();
    }

    private void StopTyping()
    {
        _typingStop?.Stop();
        if (_lastTypingSent != default && _current is { } conversation)
        {
            _lastTypingSent = default;
            _ = Client.SetTypingAsync(conversation.Jid, false).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    // ---- The window ----

    /// <summary>The window is in the foreground: the open chat counts as read and the account as online.</summary>
    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (active)
        {
            _offlineTimer.Stop();
            _ = SetOnlineAsync(true);
            _markReadTimer.Start();
        }
        else
        {
            _offlineTimer.Start();
        }
    }

    public bool IsWindowActive => _windowActive;

    private async Task SetOnlineAsync(bool online)
    {
        try
        {
            await Client.SetOnlineAsync(online);
        }
        catch (BridgeException)
        {
            // Not connected; it is sent again after connecting.
        }
    }
}
