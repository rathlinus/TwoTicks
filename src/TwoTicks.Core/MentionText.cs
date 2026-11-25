namespace TwoTicks.Core;

/// <summary>A group member picked to be mentioned in the message being written.</summary>
/// <param name="Name">The name as it stands in the message box, behind the "@".</param>
/// <param name="Jid">The JID the group knows the member by.</param>
public readonly record struct Mention(string Name, string Jid);

/// <summary>
/// Mentions while a message is written. In the message box a mention reads
/// "@Alice". WhatsApp sends it as "@" and the number of the person's JID, with
/// the JID beside the text, and every app shows the name it has for the person.
/// </summary>
public static class MentionText
{
    private const int LongestQuery = 40;

    /// <summary>
    /// Finds the name being typed behind an "@" at the end of the text before
    /// the cursor: where its "@" is, and what was typed of the name so far.
    /// </summary>
    public static (int Start, string Query)? QueryAt(string beforeCursor)
    {
        int at = beforeCursor.LastIndexOf('@');
        if (at < 0 || beforeCursor.Length - at - 1 > LongestQuery)
        {
            return null;
        }
        // An "@" inside a word is an e-mail address.
        if (at > 0 && !char.IsWhiteSpace(beforeCursor[at - 1]))
        {
            return null;
        }
        string query = beforeCursor[(at + 1)..];
        if (query.AsSpan().IndexOfAny('\n', '\r') >= 0 || (query.Length > 0 && char.IsWhiteSpace(query[^1])) || (query.Length > 0 && char.IsWhiteSpace(query[0])))
        {
            return null;
        }
        return (at, query);
    }

    /// <summary>
    /// The members a typed name can mean: those with a name or a word of the
    /// name that starts with it, or with the digits in their number. Names
    /// that start with it come first.
    /// </summary>
    public static List<GroupMember> Match(IEnumerable<GroupMember> members, string query)
    {
        string digits = new(query.Where(char.IsAsciiDigit).ToArray());
        bool number = digits.Length >= 3 && query.All(c => char.IsAsciiDigit(c) || c is '+' or ' ');
        var found = new List<(GroupMember Member, int Rank)>();
        foreach (GroupMember member in members)
        {
            if (member.Me || member.Name.Length == 0 || NumberOf(member.Mention.Length > 0 ? member.Mention : member.Jid).Length == 0)
            {
                continue;
            }
            int rank = member.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0
                : StartsAWord(member.Name, query) ? 1
                : number && NumberOf(member.Jid).Contains(digits, StringComparison.Ordinal) ? 2
                : -1;
            if (rank >= 0)
            {
                found.Add((member, rank));
            }
        }
        return found
            .OrderBy(f => f.Rank)
            .ThenBy(f => f.Member.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => f.Member)
            .ToList();
    }

    private static bool StartsAWord(string name, string query)
    {
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i - 1]) && name.AsSpan(i).StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The person to mention for a member picked from the list.</summary>
    public static Mention For(GroupMember member) => new(member.Name, member.Mention.Length > 0 ? member.Mention : member.Jid);

    /// <summary>
    /// Turns the text of the message box into the text to send: each "@Name"
    /// of a picked member becomes "@" and their number. Also gives the JIDs of
    /// the members the text names. A name that was picked but edited since is
    /// left as plain text.
    /// </summary>
    public static (string Text, List<string> Jids) ToSend(string text, IEnumerable<Mention> picked)
    {
        // The longest name first, so "@Alice Smith" is not taken for "@Alice".
        List<Mention> byLength = picked
            .Where(m => m.Name.Length > 0 && NumberOf(m.Jid).Length > 0)
            .OrderByDescending(m => m.Name.Length)
            .ToList();
        var jids = new List<string>();
        if (byLength.Count == 0 || !text.Contains('@'))
        {
            return (text, jids);
        }

        var result = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '@' && Named(text, i + 1, byLength) is { } mention)
            {
                result.Append('@').Append(NumberOf(mention.Jid));
                if (!jids.Contains(mention.Jid))
                {
                    jids.Add(mention.Jid);
                }
                i += 1 + mention.Name.Length;
            }
            else
            {
                result.Append(text[i++]);
            }
        }
        return (result.ToString(), jids);
    }

    private static Mention? Named(string text, int start, List<Mention> byLength)
    {
        foreach (Mention mention in byLength)
        {
            int end = start + mention.Name.Length;
            if (string.CompareOrdinal(text, start, mention.Name, 0, mention.Name.Length) == 0 && end <= text.Length
                && (end == text.Length || !char.IsLetterOrDigit(text[end])))
            {
                return mention;
            }
        }
        return null;
    }

    /// <summary>
    /// Turns the text of a sent message back into what the message box shows,
    /// to edit it: each "@number" of a mentioned member becomes "@Name". Also
    /// gives those members, as if they had been picked. Numbers of people who
    /// are not among the members stay as they are.
    /// </summary>
    /// <param name="names">The mentioned people of the message, by their number.</param>
    public static (string Text, List<Mention> Picked) ToEdit(string text, IReadOnlyDictionary<string, string>? names, IEnumerable<GroupMember> members)
    {
        var picked = new List<Mention>();
        if (names is not { Count: > 0 } || !text.Contains('@'))
        {
            return (text, picked);
        }
        var byNumber = new Dictionary<string, Mention>();
        foreach (GroupMember member in members)
        {
            Mention mention = For(member);
            if (mention.Name.Length > 0 && NumberOf(mention.Jid) is { Length: > 0 } number && names.ContainsKey(number))
            {
                byNumber.TryAdd(number, mention);
            }
        }

        var result = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            result.Append(text[i++]);
            if (text[i - 1] != '@')
            {
                continue;
            }
            int end = i;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }
            if (byNumber.TryGetValue(text[i..end], out Mention mention))
            {
                result.Append(mention.Name);
                if (!picked.Contains(mention))
                {
                    picked.Add(mention);
                }
                i = end;
            }
        }
        return (result.ToString(), picked);
    }

    /// <summary>The number of a JID: what is before its "@", when that is only digits.</summary>
    private static string NumberOf(string jid)
    {
        int at = jid.IndexOf('@');
        string user = at < 0 ? jid : jid[..at];
        return user.Length > 0 && user.All(char.IsAsciiDigit) ? user : "";
    }
}
