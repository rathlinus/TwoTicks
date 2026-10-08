using System.Text.RegularExpressions;
using TwoTicks.Core;

namespace TwoTicks.Core.Tests;

/// <summary>Checks the text of every language against English and against the code that uses it.</summary>
public partial class LocTests
{
    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (AppLanguage language in Loc.Languages.Where(l => l.Code != Loc.English))
        {
            data.Add(language.Code);
        }
        return data;
    }

    [Fact]
    public void HasEnglishAndGerman()
    {
        Assert.Contains(Loc.Languages, l => l.Code == "en");
        Assert.Contains(Loc.Languages, l => l.Code == "de" && l.Name == "Deutsch");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void HasTheSameKeysAsEnglish(string language)
    {
        IReadOnlyDictionary<string, string> english = Loc.Table(Loc.English);
        IReadOnlyDictionary<string, string> translated = Loc.Table(language);
        Assert.Empty(english.Keys.Except(translated.Keys).Order());
        Assert.Empty(translated.Keys.Except(english.Keys).Order());
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void KeepsThePlaceholders(string language)
    {
        IReadOnlyDictionary<string, string> english = Loc.Table(Loc.English);
        IReadOnlyDictionary<string, string> translated = Loc.Table(language);
        var wrong = english.Keys
            .Where(translated.ContainsKey)
            .Where(key => !Placeholders(english[key]).SetEquals(Placeholders(translated[key])))
            .ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void PluralsHaveBothForms()
    {
        IReadOnlyDictionary<string, string> english = Loc.Table(Loc.English);
        var incomplete = english.Keys
            .Where(k => k.EndsWith(".one", StringComparison.Ordinal) || k.EndsWith(".other", StringComparison.Ordinal))
            .Select(k => k[..k.LastIndexOf('.')])
            .Distinct()
            .Where(k => !english.ContainsKey(k + ".one") || !english.ContainsKey(k + ".other"))
            .ToList();
        Assert.Empty(incomplete);
    }

    [Fact]
    public void EveryKeyTheCodeUsesExists()
    {
        IReadOnlyDictionary<string, string> english = Loc.Table(Loc.English);
        var missing = UsedKeys().Where(k => !english.ContainsKey(k)).Order().ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryKeyIsUsed()
    {
        HashSet<string> used = UsedKeys();
        var unused = Loc.Table(Loc.English).Keys.Where(k => !used.Contains(k)).Order().ToList();
        Assert.Empty(unused);
    }

    [Fact]
    public void FillsInValues()
    {
        // The tests run with English, which no test changes: they run side by side.
        Assert.Equal("last seen today at 10:00", Loc.T("time.lastSeenToday", ("time", "10:00")));
        Assert.Equal("1 day", Loc.Plural("time.days", 1));
        Assert.Equal("7 days", Loc.Plural("time.days", 7));
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
        Assert.Equal("Gestern", Loc.Table("de")["time.yesterday"]);
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).ToHashSet();

    /// <summary>
    /// The keys in the code and the XAML: Loc.T("key"), Loc.Plural("key", …)
    /// for key.one and key.other, and {app:L Key=key}. Keys are always written
    /// out whole, so this finds them all.
    /// </summary>
    private static HashSet<string> UsedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string src = Path.Combine(RepositoryRoot(), "src");
        foreach (string file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in TextKey().Matches(text))
            {
                keys.Add(match.Groups[1].Value);
            }
            foreach (Match match in PluralKey().Matches(text))
            {
                keys.Add(match.Groups[1].Value + ".one");
                keys.Add(match.Groups[1].Value + ".other");
            }
            foreach (Match match in XamlKey().Matches(text))
            {
                keys.Add(match.Groups[1].Value);
            }
        }
        return keys;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "TwoTicks.slnx")))
            {
                return folder.FullName;
            }
        }
        throw new DirectoryNotFoundException("TwoTicks.slnx not found above the test's folder.");
    }

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"Loc\.T\(""([^""]+)""")]
    private static partial Regex TextKey();

    [GeneratedRegex(@"Loc\.Plural\(""([^""]+)""")]
    private static partial Regex PluralKey();

    [GeneratedRegex(@"\{app:L Key=([^},\s]+)\}")]
    private static partial Regex XamlKey();
}
