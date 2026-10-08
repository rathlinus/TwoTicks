using System.Globalization;
using System.Text.Json;
using TwoTicks.Core;

namespace TwoTicks.Core.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(3725, "1:02:05")]
    public void WritesDurations(int seconds, string expected) =>
        Assert.Equal(expected, Formatting.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "Off")]
    [InlineData(86400, "24 hours")]
    [InlineData(604800, "7 days")]
    [InlineData(7776000, "90 days")]
    public void WritesDisappearingTimers(long seconds, string expected) =>
        Assert.Equal(expected, Formatting.Disappearing(seconds));

    [Fact]
    public void WritesFileSizes()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Assert.Equal("512 B", Formatting.FileSize(512));
        Assert.Equal("1.5 kB", Formatting.FileSize(1500));
        Assert.Equal("23 MB", Formatting.FileSize(23_400_000));
    }

    [Fact]
    public void NamesRecentDaysInTheChatList()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var now = new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Local);
        long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

        Assert.Equal("09:30", Formatting.ChatListTime(Unix(now.Date.AddHours(9.5)), now));
        Assert.Equal("Yesterday", Formatting.ChatListTime(Unix(now.AddDays(-1)), now));
        Assert.Equal("Friday", Formatting.ChatListTime(Unix(now.AddDays(-4)), now));
        Assert.Equal("09/20/2026", Formatting.ChatListTime(Unix(now.AddDays(-16)), now));
        Assert.Equal("", Formatting.ChatListTime(0, now));
    }

    [Theory]
    [InlineData("Alice Example", "AE")]
    [InlineData("alice", "A")]
    [InlineData("Ann-Kathrin Müller Smith", "AK")]
    [InlineData("+49 170 1234", "")]
    public void TakesInitials(string name, string expected) =>
        Assert.Equal(expected, Formatting.Initials(name));

    [Theory]
    [InlineData("image", "", "Photo")]
    [InlineData("image", "*look*", "look")]
    [InlineData("voice", null, "0:12")]
    [InlineData("revoked", null, "This message was deleted")]
    [InlineData("text", "two\nlines", "two lines")]
    public void DescribesLastMessages(string kind, string? text, string expected) =>
        Assert.Equal(expected, MessagePreview.Describe(kind, text, null, 12, false).Text);

    [Fact]
    public void FindsTheSearchInAResult()
    {
        Assert.Equal(new SearchSnippet("dann easy", 5, 4), SearchSnippet.Find("dann easy", "EASY"));
        Assert.Equal(new SearchSnippet("You: dann easy", 10, 4), SearchSnippet.Find("dann easy", "easy").WithPrefix("You: "));
        Assert.Equal(new SearchSnippet("nothing here", 0, 0), SearchSnippet.Find("nothing here", "easy"));
    }

    [Fact]
    public void StartsALongResultNearTheMatch()
    {
        const string text = "Hello Linus, you have a pending notice to read. Please check it when you have a moment.";
        SearchSnippet snippet = SearchSnippet.Find(text, "moment");
        Assert.Equal("…it when you have a moment.", snippet.Text);
        Assert.Equal("moment", snippet.Text.Substring(snippet.MatchStart, snippet.MatchLength));
    }

    [Fact]
    public void ReadsMessagesFromTheHelper()
    {
        const string json = """
            {"chat":"1@s.whatsapp.net","id":"A","seq":4,"sender":"2@s.whatsapp.net","fromMe":false,"ts":1700000000,
             "kind":"image","text":"hi","media":{"mime":"image/jpeg","w":800,"h":600,"secs":0,"thumb":"AQID"},
             "reactions":[{"sender":"3@s.whatsapp.net","emoji":"👍"}],"status":0,"mentions":{"123":"Bob"}}
            """;
        MessageData message = JsonSerializer.Deserialize(json, BridgeJson.Default.MessageData)!;
        Assert.Equal("image", message.Kind);
        Assert.Equal(800, message.Media!.Width);
        Assert.Equal([1, 2, 3], message.Media.Thumb);
        Assert.Equal("👍", message.Reactions![0].Emoji);
        Assert.Equal("Bob", message.Mentions!["123"]);
    }
}
