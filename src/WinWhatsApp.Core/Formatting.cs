using System.Globalization;

namespace WinWhatsApp.Core;

/// <summary>Times, lengths and sizes as the app writes them.</summary>
public static class Formatting
{
    public static DateTime ToLocal(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;

    /// <summary>The time of a message in its bubble.</summary>
    public static string MessageTime(long unixSeconds) =>
        ToLocal(unixSeconds).ToString("t", CultureInfo.CurrentCulture);

    /// <summary>The time in the chat list: the time today, then the day name for a week, then the date.</summary>
    public static string ChatListTime(long unixSeconds, DateTime now)
    {
        if (unixSeconds <= 0)
        {
            return "";
        }
        DateTime time = ToLocal(unixSeconds);
        int days = (now.Date - time.Date).Days;
        return days switch
        {
            <= 0 => time.ToString("t", CultureInfo.CurrentCulture),
            1 => "Yesterday",
            < 7 => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(time.DayOfWeek),
            _ => time.ToString("d", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>The label between the messages of two days.</summary>
    public static string DayLabel(DateTime day, DateTime now)
    {
        int days = (now.Date - day.Date).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day.DayOfWeek),
            _ when day.Year == now.Year => day.ToString("M", CultureInfo.CurrentCulture),
            _ => day.ToString("D", CultureInfo.CurrentCulture),
        };
    }

    public static string LastSeen(long unixSeconds, DateTime now)
    {
        DateTime time = ToLocal(unixSeconds);
        string clock = time.ToString("t", CultureInfo.CurrentCulture);
        int days = (now.Date - time.Date).Days;
        return days switch
        {
            <= 0 => $"last seen today at {clock}",
            1 => $"last seen yesterday at {clock}",
            _ => $"last seen {time.ToString("d", CultureInfo.CurrentCulture)} at {clock}",
        };
    }

    /// <summary>A length as minutes and seconds, such as 1:05.</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    public static string FileSize(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1000 && unit < units.Length - 1)
        {
            size /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : size.ToString(size < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>
    /// The initials for a picture placeholder: the first letters of the first
    /// two words, or the last two digits of a number.
    /// </summary>
    public static string Initials(string name)
    {
        string[] words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var letters = words
            .Select(w => w.FirstOrDefault(char.IsLetter))
            .Where(c => c != default)
            .Take(2)
            .Select(c => char.ToUpper(c, CultureInfo.CurrentCulture));
        string initials = string.Concat(letters);
        return initials.Length > 0 ? initials : "";
    }
}

/// <summary>The line in the chat list that describes a chat's last message.</summary>
/// <param name="Glyph">The name of an icon to show before the text, or null.</param>
public readonly record struct Preview(string? Glyph, string Text);

public static class MessagePreview
{
    // Names of WhatsApp's icons, as the app's WaIcons knows them.
    public const string PhotoGlyph = "Camera";
    public const string VideoGlyph = "Video";
    public const string GifGlyph = "Gif";
    public const string MicrophoneGlyph = "Mic";
    public const string AudioGlyph = "Headphones";
    public const string DocumentGlyph = "Document";
    public const string StickerGlyph = "Sticker";
    public const string LocationGlyph = "Location";
    public const string ContactGlyph = "Person";
    public const string PollGlyph = "Poll";
    public const string DeletedGlyph = "Block";
    public const string ViewOnceGlyph = "ViewOnce";
    public const string WaitingGlyph = "Schedule";
    public const string UnsupportedGlyph = "Unsupported";

    public static Preview Describe(string kind, string? text, string? fileName, int seconds, bool fromMe)
    {
        string firstLine = FirstLine(text);
        return kind switch
        {
            "text" or "system" => new(null, firstLine),
            "image" => new(PhotoGlyph, Or(firstLine, "Photo")),
            "video" => new(VideoGlyph, Or(firstLine, "Video")),
            "gif" => new(GifGlyph, Or(firstLine, "GIF")),
            "voice" => new(MicrophoneGlyph, seconds > 0 ? Formatting.Duration(TimeSpan.FromSeconds(seconds)) : "Voice message"),
            "audio" => new(AudioGlyph, "Audio"),
            "document" => new(DocumentGlyph, Or(firstLine, Or(fileName ?? "", "Document"))),
            "sticker" => new(StickerGlyph, "Sticker"),
            "location" => new(LocationGlyph, Or(firstLine, "Location")),
            "contact" => new(ContactGlyph, Or(firstLine, "Contact")),
            "poll" => new(PollGlyph, Or(firstLine, "Poll")),
            "revoked" => new(DeletedGlyph, fromMe ? "You deleted this message" : "This message was deleted"),
            "viewonce" => new(ViewOnceGlyph, "View once message"),
            "pending" => new(WaitingGlyph, "Waiting for this message"),
            _ => new(UnsupportedGlyph, "Unsupported message"),
        };
    }

    private static string Or(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        // Formatting markers would show as they are in a single line of plain text.
        string plain = string.Concat(WhatsAppText.Parse(text).Select(s => s.Text));
        return plain.ReplaceLineEndings(" ").Trim();
    }
}

/// <summary>The text of a found message as the search results show it, and where in it the search matched.</summary>
/// <param name="MatchLength">0 when the text does not contain the search, as when it matched formatting markers the result leaves out.</param>
public sealed record SearchSnippet(string Text, int MatchStart, int MatchLength)
{
    // A result shows two lines. A match further in than this would be cut off,
    // so the text then starts at a word a little before it.
    private const int MaxLead = 40;
    private const int ContextBefore = 20;

    public static SearchSnippet Find(string text, string query)
    {
        int start = query.Length == 0 ? -1 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return new(text, 0, 0);
        }
        if (start <= MaxLead)
        {
            return new(text, start, query.Length);
        }
        int from = start - ContextBefore;
        int space = text.IndexOf(' ', from, ContextBefore);
        if (space >= 0)
        {
            from = space + 1;
        }
        else if (char.IsLowSurrogate(text[from]))
        {
            from++;
        }
        return new("…" + text[from..], start - from + 1, query.Length);
    }

    /// <summary>The same with text in front, such as "You: ".</summary>
    public SearchSnippet WithPrefix(string prefix) => this with { Text = prefix + Text, MatchStart = MatchStart + prefix.Length };
}
