using WinWhatsApp.Core;

namespace WinWhatsApp.Core.Tests;

public class WhatsAppTextTests
{
    private static string Describe(IReadOnlyList<TextSpan> spans) =>
        string.Join("|", spans.Select(s => s.Link is not null ? $"[{s.Text}]({s.Link})"
            : s.IsMention ? $"<{s.Text}>"
            : s.Style == TextStyle.None ? s.Text : $"{s.Style}:{s.Text}"));

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("*bold*", "Bold:bold")]
    [InlineData("a *bold* word", "a |Bold:bold| word")]
    [InlineData("_italic_ and ~gone~", "Italic:italic| and |Strike:gone")]
    [InlineData("*_both_*", "Bold, Italic:both")]
    [InlineData("use `code` here", "use |Mono:code| here")]
    [InlineData("(*inside parentheses*)", "(|Bold:inside parentheses|)")]
    public void Formats(string text, string expected) =>
        Assert.Equal(expected, Describe(WhatsAppText.Parse(text)));

    [Theory]
    [InlineData("snake_case_name")]
    [InlineData("2*3*4")]
    [InlineData("* not bold *")]
    [InlineData("**")]
    [InlineData("*across\nlines*")]
    [InlineData("an *unclosed marker")]
    public void LeavesMarkersThatAreNotFormatting(string text) =>
        Assert.Equal(text, Describe(WhatsAppText.Parse(text)));

    [Fact]
    public void KeepsCodeBlocksAsTheyAre()
    {
        var spans = WhatsAppText.Parse("before ```*not bold*\nline two``` after");
        Assert.Equal("before |Mono:*not bold*\nline two| after", Describe(spans));
    }

    [Theory]
    [InlineData("see https://example.com/a?b=1.", "see |[https://example.com/a?b=1](https://example.com/a?b=1)|.")]
    [InlineData("www.example.com rocks", "[www.example.com](https://www.example.com)| rocks")]
    [InlineData("(https://en.wikipedia.org/wiki/Foo_(bar))", "(|[https://en.wikipedia.org/wiki/Foo_(bar)](https://en.wikipedia.org/wiki/Foo_(bar))|)")]
    public void FindsLinks(string text, string expected)
    {
        var spans = WhatsAppText.Parse(text);
        string described = string.Join("|", spans.Select(s => s.Link is not null ? $"[{s.Text}]({s.Link})" : s.Style == TextStyle.None ? s.Text : $"{s.Style}:{s.Text}"));
        Assert.Equal(expected, described);
    }

    [Fact]
    public void FormatsLinks()
    {
        TextSpan link = Assert.Single(WhatsAppText.Parse("*https://example.com*"));
        Assert.Equal("https://example.com", link.Link);
        Assert.Equal(TextStyle.Bold, link.Style);
    }

    [Fact]
    public void LinksKeepTheirUnderscores()
    {
        var spans = WhatsAppText.Parse("https://example.com/_a_b_ and _x_");
        Assert.Equal("[https://example.com/_a_b](https://example.com/_a_b)|_ and |Italic:x", Describe(spans));
    }

    [Fact]
    public void ReplacesKnownMentions()
    {
        var mentions = new Dictionary<string, string> { ["491701234567"] = "Alice" };
        var spans = WhatsAppText.Parse("hi @491701234567 and @490000000000", mentions);
        Assert.Equal("hi |<@Alice>| and @490000000000", Describe(spans));
    }

    [Theory]
    [InlineData("👍", true)]
    [InlineData("😂😂😂", true)]
    [InlineData("👨‍👩‍👧", true)]
    [InlineData("🇩🇪", true)]
    [InlineData("👍🏽", true)]
    [InlineData("❤️", true)]
    [InlineData("😂😂😂😂", false)]
    [InlineData("ok 👍", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    public void RecognizesMessagesThatAreOnlyEmoji(string text, bool expected) =>
        Assert.Equal(expected, WhatsAppText.IsJumboEmoji(text));
}
