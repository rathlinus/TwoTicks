using System.Text.Json.Serialization;

namespace TwoTicks.Core;

// The data the helper sends, as it arrives. Names follow the helper's JSON.

public sealed class ChatData
{
    public string Jid { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Group { get; set; }
    public long Ts { get; set; }
    public int Unread { get; set; }
    public bool MarkedUnread { get; set; }

    /// <summary>Unix seconds until which the chat is muted; -1 for always, 0 for not muted.</summary>
    public long MutedUntil { get; set; }

    /// <summary>When the chat was pinned, in Unix seconds; 0 when it is not pinned.</summary>
    public long Pinned { get; set; }

    public bool Archived { get; set; }
    public bool ReadOnly { get; set; }
    public int Members { get; set; }
    public int Ephemeral { get; set; }
    public string? Avatar { get; set; }
    public LastMessageData? Last { get; set; }

    public bool IsMuted(DateTimeOffset now) => MutedUntil < 0 || MutedUntil > now.ToUnixTimeSeconds();
}

public sealed class LastMessageData
{
    public string Id { get; set; } = "";
    public bool FromMe { get; set; }
    public string? SenderName { get; set; }
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public int Status { get; set; }
    public string? Name { get; set; }

    [JsonPropertyName("secs")]
    public int Seconds { get; set; }
}

public sealed class MessageData
{
    public string Chat { get; set; } = "";
    public string Id { get; set; } = "";
    public long Seq { get; set; }
    public string Sender { get; set; } = "";
    public string? SenderName { get; set; }
    public bool FromMe { get; set; }
    public long Ts { get; set; }
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public MediaData? Media { get; set; }
    public QuoteData? Quote { get; set; }
    public LinkData? Link { get; set; }

    /// <summary>Who an "@number" in the text refers to: the number, without @, to a name.</summary>
    public Dictionary<string, string>? Mentions { get; set; }

    public List<ReactionData>? Reactions { get; set; }
    public int Status { get; set; }
    public bool Edited { get; set; }

    /// <summary>How often the message was forwarded before it got here; 0 when it was not.</summary>
    public int Forwarded { get; set; }

    /// <summary>Kept in a chat with disappearing messages.</summary>
    public bool Kept { get; set; }

    public bool Starred { get; set; }

    /// <summary>Pinned at the top of the chat.</summary>
    public bool Pinned { get; set; }

    /// <summary>Set on new incoming messages that should raise a notification.</summary>
    public bool Notify { get; set; }
}

public sealed class MediaData
{
    public string? Mime { get; set; }
    public long Size { get; set; }
    public string? Name { get; set; }

    [JsonPropertyName("w")]
    public int Width { get; set; }

    [JsonPropertyName("h")]
    public int Height { get; set; }

    [JsonPropertyName("secs")]
    public int Seconds { get; set; }

    public int Pages { get; set; }

    /// <summary>A small JPEG to show until the file itself is downloaded.</summary>
    public byte[]? Thumb { get; set; }

    [JsonPropertyName("wave")]
    public byte[]? Waveform { get; set; }

    public bool Animated { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }

    /// <summary>The downloaded file, once there is one.</summary>
    public string? Path { get; set; }
}

public sealed class QuoteData
{
    public string Id { get; set; } = "";
    public string Sender { get; set; } = "";
    public string? SenderName { get; set; }
    public bool FromMe { get; set; }
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
}

public sealed class LinkData
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public byte[]? Thumb { get; set; }
}

public sealed class ReactionData
{
    public string Sender { get; set; } = "";
    public string? Name { get; set; }
    public string Emoji { get; set; } = "";
    public bool FromMe { get; set; }
}

public sealed class MessagesPage
{
    public List<MessageData> Messages { get; set; } = [];
    public bool HasOlder { get; set; }
    public bool HasNewer { get; set; }
}

public sealed class Cursor
{
    public long Ts { get; set; }
    public long Seq { get; set; }
}

public sealed class StateData
{
    public string State { get; set; } = "";
    public string? Message { get; set; }
    public AccountData? Me { get; set; }
}

public sealed class AccountData
{
    public string Jid { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class QrData
{
    public string Code { get; set; } = "";
    public int Timeout { get; set; }
}

public sealed class TypingData
{
    public string Chat { get; set; } = "";
    public string Sender { get; set; } = "";
    public bool Typing { get; set; }
    public bool Audio { get; set; }
}

public sealed class PresenceData
{
    public string Jid { get; set; } = "";
    public bool Online { get; set; }
    public long LastSeen { get; set; }
}

public sealed class StatusData
{
    public string Chat { get; set; } = "";
    public List<string> Ids { get; set; } = [];
    public int Status { get; set; }
}

public sealed class ChatsChangedData
{
    /// <summary>Chats that history sync added messages to.</summary>
    public List<string>? History { get; set; }
}

public sealed class AvatarData
{
    public string Jid { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class PathData
{
    public string Path { get; set; } = "";
}

public sealed class MessageRef
{
    public string Chat { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Message { get; set; }
}

public sealed class MergedData
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class SyncData
{
    public string Type { get; set; } = "";
    public int Progress { get; set; }
}

public sealed class CallData
{
    public string From { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Video { get; set; }
    public bool Group { get; set; }
}

/// <summary>
/// A call stanza from WhatsApp for the calling engine. Node is the stanza in
/// WhatsApp's binary XML, base64 encoded; the engine reads it as it is.
/// </summary>
public sealed class CallSignalData
{
    /// <summary>offer, message or receipt.</summary>
    public string Kind { get; set; } = "";
    public string Node { get; set; } = "";
    public string Peer { get; set; } = "";
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public long T { get; set; }
    public long E { get; set; }
    public bool Offline { get; set; }
    public string? TcToken { get; set; }
    public string CallId { get; set; } = "";

    // For an offer: the chat of the caller and their name.
    public string? Chat { get; set; }
    public string? Name { get; set; }
    public bool NotContact { get; set; }
}

/// <summary>This device as the calling engine knows itself.</summary>
public sealed class CallIdentityData
{
    public string Pn { get; set; } = "";
    public string PnUser { get; set; } = "";
    public string Lid { get; set; } = "";

    /// <summary>The country calling code of this account's number, such as 49.</summary>
    public string? CountryCode { get; set; }
}

/// <summary>What the calling engine needs to call someone.</summary>
public sealed class CallTargetData
{
    public string Peer { get; set; } = "";
    public string PeerPn { get; set; } = "";
    public List<string> Devices { get; set; } = [];
    public string? TcToken { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>The server's answer to a call stanza.</summary>
public sealed class CallAckData
{
    public string Node { get; set; } = "";
    public string Error { get; set; } = "0";
    public string? Type { get; set; }
    public string? TcToken { get; set; }
}

public sealed class ContactData
{
    public string Jid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
}

public sealed class JidData
{
    public string Jid { get; set; } = "";
}

public sealed class CodeData
{
    public string Code { get; set; } = "";
}

public sealed class GroupData
{
    public string Name { get; set; } = "";
    public string? Topic { get; set; }
    public List<GroupMember> Members { get; set; } = [];

    /// <summary>When the group was created, in Unix seconds; 0 when unknown.</summary>
    public long Created { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class GroupMember
{
    public string Jid { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Admin { get; set; }
    public bool Me { get; set; }
}

/// <summary>What the contact info of a person shows.</summary>
public sealed class ProfileData
{
    public string Jid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? About { get; set; }
    public bool Me { get; set; }

    /// <summary>Whether you blocked the person; only known while connected.</summary>
    public bool Blocked { get; set; }
}

/// <summary>What the info of a chat counts, and its newest photos and videos.</summary>
public sealed class ChatInfoData
{
    public int Media { get; set; }
    public int Docs { get; set; }
    public int Links { get; set; }
    public int Starred { get; set; }
    public int Kept { get; set; }
    public List<MessageData> Recent { get; set; } = [];
}

/// <summary>A group that you and a person are both in.</summary>
public sealed class CommonGroupData
{
    public string Jid { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Some of the members, as one line.</summary>
    public string Members { get; set; } = "";
}

public sealed class ErrorData
{
    public string Message { get; set; } = "";
}

/// <summary>Ticks under an outgoing message.</summary>
public static class MessageStatus
{
    public const int Failed = -1;
    public const int Pending = 0;
    public const int Sent = 1;
    public const int Delivered = 2;
    public const int Read = 3;
    public const int Played = 4;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ChatData>))]
[JsonSerializable(typeof(ChatData))]
[JsonSerializable(typeof(MessageData))]
[JsonSerializable(typeof(List<MessageData>))]
[JsonSerializable(typeof(MessagesPage))]
[JsonSerializable(typeof(StateData))]
[JsonSerializable(typeof(QrData))]
[JsonSerializable(typeof(TypingData))]
[JsonSerializable(typeof(PresenceData))]
[JsonSerializable(typeof(StatusData))]
[JsonSerializable(typeof(ChatsChangedData))]
[JsonSerializable(typeof(AvatarData))]
[JsonSerializable(typeof(PathData))]
[JsonSerializable(typeof(MessageRef))]
[JsonSerializable(typeof(MergedData))]
[JsonSerializable(typeof(SyncData))]
[JsonSerializable(typeof(CallData))]
[JsonSerializable(typeof(CallSignalData))]
[JsonSerializable(typeof(CallIdentityData))]
[JsonSerializable(typeof(CallTargetData))]
[JsonSerializable(typeof(CallAckData))]
[JsonSerializable(typeof(List<ContactData>))]
[JsonSerializable(typeof(JidData))]
[JsonSerializable(typeof(CodeData))]
[JsonSerializable(typeof(GroupData))]
[JsonSerializable(typeof(ProfileData))]
[JsonSerializable(typeof(ChatInfoData))]
[JsonSerializable(typeof(List<CommonGroupData>))]
[JsonSerializable(typeof(ErrorData))]
[JsonSerializable(typeof(Cursor))]
[JsonSerializable(typeof(LinkData))]
internal sealed partial class BridgeJson : JsonSerializerContext;
