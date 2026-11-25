using TwoTicks.Core;

namespace TwoTicks.Core.Tests;

public class MentionTextTests
{
    private static readonly GroupMember[] s_members =
    [
        new() { Jid = "447700900111@s.whatsapp.net", Mention = "100000000000001@lid", Name = "Alice" },
        new() { Jid = "447700900222@s.whatsapp.net", Mention = "100000000000002@lid", Name = "Alice Smith" },
        new() { Jid = "447700900333@s.whatsapp.net", Mention = "447700900333@s.whatsapp.net", Name = "Bob van Dyk" },
        new() { Jid = "447700900444@s.whatsapp.net", Mention = "100000000000004@lid", Name = "+44 7700 900444" },
        new() { Jid = "447700900555@s.whatsapp.net", Mention = "100000000000005@lid", Name = "You", Me = true },
    ];

    [Theory]
    [InlineData("@", 0, "")]
    [InlineData("hi @al", 3, "al")]
    [InlineData("hi @Alice Sm", 3, "Alice Sm")]
    [InlineData("first line\n@bo", 11, "bo")]
    [InlineData("@Alice and @bo", 11, "bo")]
    public void FindsTheNameBeingTyped(string before, int start, string query) =>
        Assert.Equal((start, query), MentionText.QueryAt(before));

    [Theory]
    [InlineData("")]
    [InlineData("hi there")]
    [InlineData("mail me at a@b")]
    [InlineData("@Alice ")]
    [InlineData("@ al")]
    [InlineData("@al\nnext")]
    [InlineData("@this is far too long to still be the start of anybody's name")]
    public void FindsNoNameWhereNoneIsTyped(string before) =>
        Assert.Null(MentionText.QueryAt(before));

    [Fact]
    public void ListsEveryoneButYouForAnEmptyName() =>
        Assert.Equal(["+44 7700 900444", "Alice", "Alice Smith", "Bob van Dyk"], MentionText.Match(s_members, "").Select(m => m.Name));

    [Theory]
    [InlineData("al", "Alice,Alice Smith")]
    [InlineData("ALICE S", "Alice Smith")]
    [InlineData("dy", "Bob van Dyk")]
    [InlineData("s", "Alice Smith")]
    [InlineData("900333", "Bob van Dyk")]
    [InlineData("you", "")]
    [InlineData("zed", "")]
    public void MatchesNamesWordsAndNumbers(string query, string expected) =>
        Assert.Equal(expected, string.Join(",", MentionText.Match(s_members, query).Select(m => m.Name)));

    [Fact]
    public void SendsPickedNamesAsNumbers()
    {
        Mention[] picked = [MentionText.For(s_members[0]), MentionText.For(s_members[1]), MentionText.For(s_members[2])];
        (string text, List<string> jids) = MentionText.ToSend("@Alice Smith, @Alice: ask @Bob van Dyk. @Alice?", picked);
        Assert.Equal("@100000000000002, @100000000000001: ask @447700900333. @100000000000001?", text);
        Assert.Equal(["100000000000002@lid", "100000000000001@lid", "447700900333@s.whatsapp.net"], jids);
    }

    [Fact]
    public void LeavesNamesThatWereNotPickedOrWereEdited()
    {
        Mention[] picked = [MentionText.For(s_members[0])];
        (string text, List<string> jids) = MentionText.ToSend("@Alicea, @Bob van Dyk and alice@example.com @Alic", picked);
        Assert.Equal("@Alicea, @Bob van Dyk and alice@example.com @Alic", text);
        Assert.Empty(jids);
    }

    [Fact]
    public void SendsAMemberKnownOnlyByNumber()
    {
        (string text, List<string> jids) = MentionText.ToSend("@+44 7700 900444 hi", [MentionText.For(s_members[3])]);
        Assert.Equal("@100000000000004 hi", text);
        Assert.Equal(["100000000000004@lid"], jids);
    }

    [Fact]
    public void EditsNumbersAsNames()
    {
        var names = new Dictionary<string, string> { ["100000000000001"] = "Alice", ["447700900333"] = "Bob", ["999999999999"] = "Someone who left" };
        (string text, List<Mention> picked) = MentionText.ToEdit("@100000000000001 and @447700900333, @999999999999, @100000000000002", names, s_members);
        Assert.Equal("@Alice and @Bob van Dyk, @999999999999, @100000000000002", text);
        Assert.Equal([new Mention("Alice", "100000000000001@lid"), new Mention("Bob van Dyk", "447700900333@s.whatsapp.net")], picked);

        // What the message box shows goes out again as it came.
        Assert.Equal("@100000000000001 and @447700900333, @999999999999, @100000000000002", MentionText.ToSend(text, picked).Text);
    }
}
