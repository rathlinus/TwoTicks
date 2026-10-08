using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace TwoTicks.Core;

/// <summary>
/// The app's view of WhatsApp: typed requests to the helper and the events it
/// sends. Events are raised on a background thread.
/// </summary>
public sealed class WhatsAppClient : IDisposable
{
    private readonly string _executable;
    private readonly string _dataFolder;
    private readonly bool _debug;
    private readonly Lock _lock = new();
    private BridgeConnection? _connection;
    private bool _disposed;

    public WhatsAppClient(string executable, string dataFolder, bool debug = false)
    {
        _executable = executable;
        _dataFolder = dataFolder;
        _debug = debug;
    }

    public event Action<StateData>? StateChanged;
    public event Action<QrData>? QrCodeReceived;
    public event Action<ChatData>? ChatUpdated;
    public event Action<ChatsChangedData>? ChatsChanged;
    public event Action<MessageData>? MessageUpdated;
    public event Action<MessageRef>? MessageDeleted;
    public event Action<StatusData>? StatusChanged;
    public event Action<TypingData>? TypingChanged;
    public event Action<PresenceData>? PresenceChanged;
    public event Action<AvatarData>? AvatarChanged;
    public event Action<MergedData>? ChatMerged;

    /// <summary>A message of the chat was pinned or unpinned.</summary>
    public event Action<MessageRef>? PinsChanged;

    /// <summary>Messages of a chat were deleted on the phone.</summary>
    public event Action<MessageRef>? ChatCleared;
    public event Action<SyncData>? SyncProgress;
    /// <summary>A call this app cannot answer, such as a video or group call.</summary>
    public event Action<CallData>? CallReceived;

    /// <summary>A call stanza for the calling engine.</summary>
    public event Action<CallSignalData>? CallSignalReceived;
    public event Action<MessageRef>? SendFailed;
    public event Action<string>? Failed;

    /// <summary>Raised when the helper process was started, also after a restart.</summary>
    public event Action<Process>? HelperStarted;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null)
            {
                return;
            }
            var connection = BridgeConnection.Start(_executable, _dataFolder, _debug);
            connection.EventReceived += OnEvent;
            connection.Exited += () => OnExited(connection);
            _connection = connection;
            HelperStarted?.Invoke(connection.Process);
        }
    }

    /// <summary>Stops the helper and starts a new one, as after logging out.</summary>
    public void Restart()
    {
        BridgeConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = null;
        }
        old?.Dispose();
        Start();
    }

    private void OnExited(BridgeConnection connection)
    {
        bool restart;
        lock (_lock)
        {
            restart = !_disposed && _connection == connection;
            if (restart)
            {
                _connection = null;
            }
        }
        if (restart)
        {
            // A crash. Start again after a pause, so a helper that fails right
            // away does not spin.
            Failed?.Invoke(Loc.T("helper.restarting"));
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                try
                {
                    Start();
                }
                catch (Exception e) when (e is BridgeException or ObjectDisposedException or System.ComponentModel.Win32Exception)
                {
                    Failed?.Invoke(e.Message);
                }
            }, TaskScheduler.Default);
        }
    }

    private void OnEvent(string name, JsonElement data)
    {
        try
        {
            switch (name)
            {
                case "state":
                    StateChanged?.Invoke(Read(data, BridgeJson.Default.StateData));
                    break;
                case "qr":
                    QrCodeReceived?.Invoke(Read(data, BridgeJson.Default.QrData));
                    break;
                case "chat":
                    ChatUpdated?.Invoke(Read(data, BridgeJson.Default.ChatData));
                    break;
                case "chats":
                    ChatsChanged?.Invoke(data.ValueKind == JsonValueKind.Object ? Read(data, BridgeJson.Default.ChatsChangedData) : new ChatsChangedData());
                    break;
                case "message":
                    MessageUpdated?.Invoke(Read(data, BridgeJson.Default.MessageData));
                    break;
                case "deleted":
                    MessageDeleted?.Invoke(Read(data, BridgeJson.Default.MessageRef));
                    break;
                case "status":
                    StatusChanged?.Invoke(Read(data, BridgeJson.Default.StatusData));
                    break;
                case "typing":
                    TypingChanged?.Invoke(Read(data, BridgeJson.Default.TypingData));
                    break;
                case "presence":
                    PresenceChanged?.Invoke(Read(data, BridgeJson.Default.PresenceData));
                    break;
                case "avatar":
                    AvatarChanged?.Invoke(Read(data, BridgeJson.Default.AvatarData));
                    break;
                case "cleared":
                    ChatCleared?.Invoke(Read(data, BridgeJson.Default.MessageRef));
                    break;
                case "pins":
                    PinsChanged?.Invoke(Read(data, BridgeJson.Default.MessageRef));
                    break;
                case "merged":
                    ChatMerged?.Invoke(Read(data, BridgeJson.Default.MergedData));
                    break;
                case "sync":
                    SyncProgress?.Invoke(Read(data, BridgeJson.Default.SyncData));
                    break;
                case "call":
                    CallReceived?.Invoke(Read(data, BridgeJson.Default.CallData));
                    break;
                case "callSignal":
                    CallSignalReceived?.Invoke(Read(data, BridgeJson.Default.CallSignalData));
                    break;
                case "sendFailed":
                    SendFailed?.Invoke(Read(data, BridgeJson.Default.MessageRef));
                    break;
                case "fatal":
                case "error":
                    Failed?.Invoke(Read(data, BridgeJson.Default.ErrorData).Message);
                    break;
            }
        }
        catch (JsonException)
        {
            // An event this version does not understand.
        }
    }

    private static T Read<T>(JsonElement element, JsonTypeInfo<T> type) =>
        element.Deserialize(type) ?? throw new JsonException("Empty event");

    private BridgeConnection Connection
    {
        get
        {
            lock (_lock)
            {
                return _connection ?? throw new BridgeException(Loc.T("helper.notRunning"));
            }
        }
    }

    private async Task<T> CallAsync<T>(string method, JsonObject? parameters, JsonTypeInfo<T> type, CancellationToken cancellationToken = default)
    {
        JsonElement result = await Connection.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return result.Deserialize(type) ?? throw new BridgeException($"{method} returned nothing.");
    }

    private Task CallAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default) =>
        Connection.CallAsync(method, parameters, cancellationToken);

    public Task<StateData> GetStateAsync() => CallAsync("status", null, BridgeJson.Default.StateData);

    public Task LoginAsync() => CallAsync("login", null);

    public async Task<string> PairWithPhoneAsync(string phone) =>
        (await CallAsync("pairPhone", new JsonObject { ["phone"] = phone }, BridgeJson.Default.CodeData).ConfigureAwait(false)).Code;

    public Task LogoutAsync() => CallAsync("logout", null);

    public Task<List<ChatData>> GetChatsAsync() => CallAsync("chats", null, BridgeJson.Default.ListChatData);

    public Task<ChatData> GetChatAsync(string chat) => CallAsync("chat", new JsonObject { ["chat"] = chat }, BridgeJson.Default.ChatData);

    /// <param name="media">Only photos and videos, as the viewer steps through them.</param>
    /// <param name="filter">Only some messages, for the lists in a chat's info: media, docs, links, starred or kept.</param>
    public Task<MessagesPage> GetMessagesAsync(string chat, Cursor? before = null, Cursor? after = null, string? around = null, int limit = 60, bool media = false,
        string? filter = null)
    {
        var parameters = new JsonObject { ["chat"] = chat, ["limit"] = limit };
        if (media)
        {
            parameters["media"] = true;
        }
        if (filter is not null)
        {
            parameters["filter"] = filter;
        }
        if (before is not null)
        {
            parameters["before"] = new JsonObject { ["ts"] = before.Ts, ["seq"] = before.Seq };
        }
        if (after is not null)
        {
            parameters["after"] = new JsonObject { ["ts"] = after.Ts, ["seq"] = after.Seq };
        }
        if (around is not null)
        {
            parameters["around"] = around;
        }
        return CallAsync("messages", parameters, BridgeJson.Default.MessagesPage);
    }

    /// <param name="chat">Only in this chat, when given.</param>
    public Task<List<MessageData>> SearchAsync(string query, int limit = 50, string? chat = null) =>
        CallAsync("search", new JsonObject { ["query"] = query, ["limit"] = limit, ["chat"] = chat ?? "" }, BridgeJson.Default.ListMessageData);

    public Task<MessageData> SendTextAsync(string chat, string text, string? replyTo, LinkData? link = null)
    {
        var parameters = new JsonObject { ["chat"] = chat, ["text"] = text, ["replyTo"] = replyTo };
        if (link is not null)
        {
            parameters["link"] = JsonSerializer.SerializeToNode(link, BridgeJson.Default.LinkData);
        }
        return CallAsync("send", parameters, BridgeJson.Default.MessageData);
    }

    /// <summary>The title, description and picture of a web page, for a link about to be sent; null when it has none.</summary>
    public async Task<LinkData?> GetLinkPreviewAsync(string url, CancellationToken cancellationToken = default)
    {
        JsonElement result = await Connection.CallAsync("linkPreview", new JsonObject { ["url"] = url }, cancellationToken).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object ? result.Deserialize(BridgeJson.Default.LinkData) : null;
    }

    public Task<MessageData> SendMediaAsync(OutgoingMedia media) =>
        CallAsync("sendMedia", MediaParameters(media), BridgeJson.Default.MessageData);

    /// <summary>
    /// Sends several files to a chat at once. The photos and videos among them
    /// go as an album when there are at least two.
    /// </summary>
    public Task<List<MessageData>> SendAlbumAsync(string chat, string? replyTo, IEnumerable<OutgoingMedia> files)
    {
        var parameters = new JsonObject
        {
            ["chat"] = chat,
            ["replyTo"] = replyTo,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)MediaParameters(f)).ToArray()),
        };
        return CallAsync("sendAlbum", parameters, BridgeJson.Default.ListMessageData);
    }

    private static JsonObject MediaParameters(OutgoingMedia media)
    {
        var parameters = new JsonObject
        {
            ["chat"] = media.Chat,
            ["path"] = media.Path,
            ["caption"] = media.Caption,
            ["replyTo"] = media.ReplyTo,
            ["asDocument"] = media.AsDocument,
            ["width"] = media.Width,
            ["height"] = media.Height,
            ["seconds"] = media.Seconds,
        };
        if (media.Thumbnail is { Length: > 0 })
        {
            parameters["thumb"] = Convert.ToBase64String(media.Thumbnail);
        }
        return parameters;
    }

    public Task RetryAsync(string chat, string id) => CallAsync("retry", new JsonObject { ["chat"] = chat, ["id"] = id });

    public Task ReactAsync(string chat, string id, string emoji) =>
        CallAsync("react", new JsonObject { ["chat"] = chat, ["id"] = id, ["emoji"] = emoji });

    public Task EditAsync(string chat, string id, string text) =>
        CallAsync("edit", new JsonObject { ["chat"] = chat, ["id"] = id, ["text"] = text });

    /// <param name="seconds">How long the message stays pinned.</param>
    public Task PinMessageAsync(string chat, string id, bool pin, long seconds = 0) =>
        CallAsync("pin", new JsonObject { ["chat"] = chat, ["id"] = id, ["pin"] = pin, ["seconds"] = seconds });

    /// <summary>The pinned messages of a chat, the newest pin first.</summary>
    public Task<List<MessageData>> GetPinsAsync(string chat) => CallAsync("pins", new JsonObject { ["chat"] = chat }, BridgeJson.Default.ListMessageData);

    public Task StarAsync(string chat, string id, bool star) =>
        CallAsync("star", new JsonObject { ["chat"] = chat, ["id"] = id, ["star"] = star });

    public Task KeepAsync(string chat, string id, bool keep) =>
        CallAsync("keep", new JsonObject { ["chat"] = chat, ["id"] = id, ["keep"] = keep });

    /// <summary>Sends a copy of a message to other chats; returns the copies.</summary>
    public Task<List<MessageData>> ForwardAsync(string chat, string id, IEnumerable<string> to) =>
        CallAsync("forward", new JsonObject { ["chat"] = chat, ["id"] = id, ["to"] = new JsonArray(to.Select(t => (JsonNode)t).ToArray()) },
            BridgeJson.Default.ListMessageData);

    public Task RevokeAsync(string chat, string id) => CallAsync("revoke", new JsonObject { ["chat"] = chat, ["id"] = id });

    public Task DeleteForMeAsync(string chat, string id) => CallAsync("deleteForMe", new JsonObject { ["chat"] = chat, ["id"] = id });

    public Task MarkReadAsync(string chat) => CallAsync("markRead", new JsonObject { ["chat"] = chat });

    public Task MarkPlayedAsync(string chat, string id) => CallAsync("markPlayed", new JsonObject { ["chat"] = chat, ["id"] = id });

    public async Task<string> DownloadAsync(string chat, string id) =>
        (await CallAsync("download", new JsonObject { ["chat"] = chat, ["id"] = id }, BridgeJson.Default.PathData).ConfigureAwait(false)).Path;

    /// <summary>Fetches the preview picture of a photo or video that has none; it arrives as a message update.</summary>
    public Task FetchThumbnailAsync(string chat, string id) => CallAsync("thumbnail", new JsonObject { ["chat"] = chat, ["id"] = id });

    public async Task<string> GetAvatarAsync(string jid, bool force = false) =>
        (await CallAsync("avatar", new JsonObject { ["jid"] = jid, ["force"] = force }, BridgeJson.Default.PathData).ConfigureAwait(false)).Path;

    /// <summary>The profile picture at full size, fetched once; "" when there is none.</summary>
    public async Task<string> GetPictureAsync(string jid) =>
        (await CallAsync("picture", new JsonObject { ["jid"] = jid }, BridgeJson.Default.PathData).ConfigureAwait(false)).Path;

    public Task<ProfileData> GetProfileAsync(string jid) => CallAsync("profile", new JsonObject { ["jid"] = jid }, BridgeJson.Default.ProfileData);

    public Task SetTypingAsync(string chat, bool typing) => CallAsync("typing", new JsonObject { ["chat"] = chat, ["typing"] = typing });

    public Task SetOnlineAsync(bool online) => CallAsync("online", new JsonObject { ["online"] = online });

    public Task SubscribePresenceAsync(string chat) => CallAsync("subscribe", new JsonObject { ["chat"] = chat });

    public Task<List<ContactData>> GetContactsAsync() => CallAsync("contacts", null, BridgeJson.Default.ListContactData);

    public async Task<string> CheckNumberAsync(string phone) =>
        (await CallAsync("checkNumber", new JsonObject { ["phone"] = phone }, BridgeJson.Default.JidData).ConfigureAwait(false)).Jid;

    /// <param name="muteSeconds">-1 to mute until unmuted, 0 to unmute.</param>
    public Task MuteAsync(string chat, long muteSeconds) => CallAsync("setChat", new JsonObject { ["chat"] = chat, ["mute"] = muteSeconds });

    public Task PinAsync(string chat, bool pin) => CallAsync("setChat", new JsonObject { ["chat"] = chat, ["pin"] = pin });

    public Task ArchiveAsync(string chat, bool archive) => CallAsync("setChat", new JsonObject { ["chat"] = chat, ["archive"] = archive });

    public Task MarkUnreadAsync(string chat) => CallAsync("setChat", new JsonObject { ["chat"] = chat, ["markUnread"] = true });

    /// <summary>Turns disappearing messages on for 24 hours, 7 days or 90 days, in seconds, or off with 0.</summary>
    public Task SetDisappearingAsync(string chat, long seconds) => CallAsync("setChat", new JsonObject { ["chat"] = chat, ["ephemeral"] = seconds });

    /// <summary>Deletes the messages of a chat on every device, except the starred ones.</summary>
    public Task ClearChatAsync(string chat) => CallAsync("clearChat", new JsonObject { ["chat"] = chat });

    public Task DeleteChatAsync(string chat) => CallAsync("deleteChat", new JsonObject { ["chat"] = chat });

    public Task<ChatInfoData> GetChatInfoAsync(string chat) => CallAsync("chatInfo", new JsonObject { ["chat"] = chat }, BridgeJson.Default.ChatInfoData);

    public Task<List<CommonGroupData>> GetCommonGroupsAsync(string jid) =>
        CallAsync("commonGroups", new JsonObject { ["jid"] = jid }, BridgeJson.Default.ListCommonGroupData);

    public Task BlockAsync(string jid, bool block) => CallAsync("block", new JsonObject { ["jid"] = jid, ["block"] = block });

    public Task LeaveGroupAsync(string chat) => CallAsync("leaveGroup", new JsonObject { ["chat"] = chat });

    public Task RequestOlderAsync(string chat) => CallAsync("requestOlder", new JsonObject { ["chat"] = chat });

    public Task<GroupData> GetGroupInfoAsync(string chat) => CallAsync("groupInfo", new JsonObject { ["chat"] = chat }, BridgeJson.Default.GroupData);

    public Task<CallIdentityData> GetCallIdentityAsync() => CallAsync("callIdentity", null, BridgeJson.Default.CallIdentityData);

    public Task<CallTargetData> PrepareCallAsync(string chat) =>
        CallAsync("callPrepare", new JsonObject { ["chat"] = chat }, BridgeJson.Default.CallTargetData);

    /// <summary>Sends a stanza of the calling engine to a peer and returns the server's ack.</summary>
    public Task<CallAckData> SendCallAsync(string peer, string payload) =>
        CallAsync("callSend", new JsonObject { ["peer"] = peer, ["payload"] = payload }, BridgeJson.Default.CallAckData);

    public void Dispose()
    {
        BridgeConnection? connection;
        lock (_lock)
        {
            _disposed = true;
            connection = _connection;
            _connection = null;
        }
        connection?.Dispose();
    }
}

/// <summary>A file to send.</summary>
public sealed class OutgoingMedia
{
    public required string Chat { get; init; }
    public required string Path { get; init; }
    public string? Caption { get; init; }
    public string? ReplyTo { get; init; }
    public bool AsDocument { get; init; }

    // For videos: what the app found out about the file, since the helper
    // cannot decode video.
    public byte[]? Thumbnail { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Seconds { get; init; }
}
