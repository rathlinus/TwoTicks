using System.Text;
using TwoTicks.Core;

namespace TwoTicks.Core.Tests;

public class EmojiTests
{
    // The real data, as the app ships it.
    private static readonly Lazy<EmojiSet> s_set = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TwoTicks.App", "Assets", "WhatsApp", "emoji.json");
        using FileStream stream = File.OpenRead(path);
        EmojiSet set = EmojiSet.Load(stream);
        using FileStream names = File.OpenRead(Path.Combine(Path.GetDirectoryName(path)!, "emoji-names.json"));
        set.LoadNames(names);
        return set;
    });

    [Fact]
    public void FindsEmojiByTheStartOfTheirName()
    {
        List<string> found = Set.Search("thumbs");
        Assert.Equal("👍", found[0]);
        Assert.Contains("👎", found);
    }

    [Fact]
    public void FindsEmojiByKeywordAfterThoseFoundByName()
    {
        List<string> found = Set.Search("lol");
        Assert.Contains("😂", found);
        Assert.Empty(Set.Search("   "));
    }

    [Fact]
    public void EveryWordOfTheQueryHasToMatch()
    {
        List<string> found = Set.Search("flag germ");
        Assert.Equal(["🇩🇪"], found);
    }

    private static EmojiSet Set => s_set.Value;

    private static EmojiSet Small(int count)
    {
        var glyphs = string.Join(",", Enumerable.Range(0, count).Select(i => $"[\"{char.ConvertFromUtf32(0x1F600 + i)}\"]"));
        string json = $"{{\"glyphs\":[{glyphs}],\"legacy\":{{}},\"categories\":{{}}}}";
        return EmojiSet.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void PlacesEmojiFiveByFive()
    {
        EmojiSet set = Small(60);
        Assert.Equal(new EmojiCell(0, 0, 0, 200, 200), set.CellOf(0));
        Assert.Equal(new EmojiCell(0, 160, 0, 200, 200), set.CellOf(4));
        Assert.Equal(new EmojiCell(0, 0, 40, 200, 200), set.CellOf(5));
        Assert.Equal(new EmojiCell(1, 40, 0, 200, 200), set.CellOf(26));
    }

    [Fact]
    public void PlacesTheRestOnANarrowerLastSheet()
    {
        // 50 full, 7 left: two columns, four rows, as WhatsApp Web lays them out.
        EmojiSet set = Small(57);
        Assert.Equal(new EmojiCell(2, 0, 0, 80, 160), set.CellOf(50));
        Assert.Equal(new EmojiCell(2, 40, 0, 80, 160), set.CellOf(51));
        Assert.Equal(new EmojiCell(2, 0, 120, 80, 160), set.CellOf(56));
    }

    [Fact]
    public void KnowsAllOfWhatsAppsEmoji()
    {
        Assert.Equal(3782, Set.Count);
        Assert.Equal(152, Set.CellOf(Set.Count - 1).Sheet + 1);
        foreach (string emoji in new[] { "😂", "👍", "❤️", "❤", "🇩🇪", "👍🏽", "👨‍👩‍👧", "1️⃣", "🫠" })
        {
            Assert.True(Set.Find(emoji) >= 0, emoji);
        }
        Assert.Equal(Set.Find("❤️"), Set.Find("❤"));
    }

    [Fact]
    public void SplitsTextAroundEmoji()
    {
        List<EmojiSegment> segments = Set.Split("Hi 👋🏽 du 👨‍👩‍👧!");
        Assert.Equal(["Hi ", "👋🏽", " du ", "👨‍👩‍👧", "!"], segments.Select(s => s.Text));
        Assert.Equal([false, true, false, true, false], segments.Select(s => s.IsEmoji));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("1st place, 2026 #3 *")]
    [InlineData("")]
    public void LeavesTextWithoutEmoji(string text) =>
        Assert.All(Set.Split(text), s => Assert.False(s.IsEmoji));

    [Fact]
    public void ScalesPathsIntoTheirBox()
    {
        Assert.Equal("M 0 0 L 12 6 h 2 a 1 1 0 0 1 2 0 Z", SvgPath.Transform("M 0 0 L 24 12 h 4 a 2 2 0 0 1 4 0 Z", 0, 0, 0.5));
        Assert.Equal("M 10 20 C 0 0 1 1 2 2", SvgPath.Transform("M 10 -940 C 0 -960 1 -959 2 -958", 0, -960, 1));
        Assert.Equal("m 1 1 2 2", SvgPath.Transform("m 2 2 4 4", 0, 0, 0.5));
    }
}
