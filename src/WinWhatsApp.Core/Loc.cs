using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WinWhatsApp.Core;

/// <summary>A language the app has its text in.</summary>
/// <param name="Code">Such as "de", the name of its folder under Strings.</param>
/// <param name="Name">Its name in itself, such as "Deutsch".</param>
public sealed record AppLanguage(string Code, string Name);

/// <summary>
/// The app's text in the language picked in the settings, or in the one Windows
/// shows. Every language has a folder under Strings with JSON files of keys and
/// text; the files are only split by area, all keys of a language are one table.
/// Text missing in a language comes from English.
/// </summary>
/// <remarks>
/// Text names its values in braces, such as "last seen today at {time}". A
/// count goes in {count}, and text that depends on it has a key for one and
/// one for any other count: "photos.one" and "photos.other".
/// </remarks>
public static class Loc
{
    public const string English = "en";

    private const string ResourcePrefix = "WinWhatsApp.Core.Strings.";

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = LoadTables();

    private static Dictionary<string, string> _current = Tables[English];

    public static IReadOnlyList<AppLanguage> Languages { get; } = Tables.Keys
        .Select(code => new AppLanguage(code, NativeName(code)))
        .OrderBy(l => l.Code == English ? 0 : 1)
        .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>The language the text is in, such as "de".</summary>
    public static string Language { get; private set; } = English;

    /// <summary>
    /// For the names of days and months. The regional format from Windows when
    /// it is for the same language, so its choices stay; otherwise that of the
    /// language, so "Montag" does not stand next to "Monday".
    /// </summary>
    public static CultureInfo Culture => _culture ?? CultureInfo.CurrentCulture;

    private static CultureInfo? _culture;

    /// <summary>
    /// Picks the language: the one asked for, or with null the first of Windows'
    /// display languages the app has, or English.
    /// </summary>
    public static void Use(string? language)
    {
        Language = language is not null && Tables.ContainsKey(language) ? language : FromWindows();
        _current = Tables[Language];
        _culture = CultureInfo.CurrentCulture.TwoLetterISOLanguageName == Language
            ? CultureInfo.CurrentCulture
            : CultureInfo.GetCultureInfo(Language);
    }

    /// <summary>The text for a key.</summary>
    public static string T(string key) =>
        _current.TryGetValue(key, out string? text) || Tables[English].TryGetValue(key, out text) ? text : key;

    /// <summary>The text for a key with its values filled in.</summary>
    public static string T(string key, params (string Name, object? Value)[] values) => Fill(T(key), values);

    /// <summary>The text for a count, from "key.one" or "key.other", with the count in {count}.</summary>
    public static string Plural(string key, long count, params (string Name, object? Value)[] values) =>
        Fill(T(key + (count == 1 ? ".one" : ".other")), [("count", count), .. values]);

    private static string Fill(string text, (string Name, object? Value)[] values)
    {
        if (values.Length == 0 || !text.Contains('{'))
        {
            return text;
        }
        var result = new StringBuilder(text);
        foreach ((string name, object? value) in values)
        {
            string formatted = value is IFormattable f ? f.ToString(null, CultureInfo.CurrentCulture) : value?.ToString() ?? "";
            result.Replace("{" + name + "}", formatted);
        }
        return result.ToString();
    }

    private static string FromWindows()
    {
        for (CultureInfo culture = CultureInfo.CurrentUICulture; culture.Name.Length > 0; culture = culture.Parent)
        {
            if (Tables.ContainsKey(culture.Name))
            {
                return culture.Name;
            }
        }
        return English;
    }

    private static string NativeName(string code)
    {
        string name = CultureInfo.GetCultureInfo(code).NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(code)) + name[1..] : code;
    }

    /// <summary>All keys and text of a language, for the tests.</summary>
    internal static IReadOnlyDictionary<string, string> Table(string language) => Tables[language];

    /// <summary>
    /// Reads the embedded files. Their resource names are
    /// WinWhatsApp.Core.Strings.{language}.{area}.json.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Assembly assembly = typeof(Loc).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            string language = resource[ResourcePrefix.Length..].Split('.')[0];
            if (!tables.TryGetValue(language, out Dictionary<string, string>? table))
            {
                tables[language] = table = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using JsonDocument document = JsonDocument.Parse(stream);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                table[property.Name] = property.Value.GetString() ?? "";
            }
        }
        if (!tables.ContainsKey(English))
        {
            tables[English] = [];
        }
        foreach (Dictionary<string, string> table in tables.Values)
        {
            UseSystemWording(table);
        }
        return tables;
    }

    /// <summary>
    /// Text that names Windows or a part of it has another wording for where
    /// the app runs elsewhere, under the same key with "@mac", "@linux" or, for
    /// both, "@unix" after it. The one for this system takes the place of the
    /// text, and the others are dropped, so the tables hold plain keys only.
    /// </summary>
    private static void UseSystemWording(Dictionary<string, string> table)
    {
        // Later ones win: the wording for one system over the one for both.
        string[] mine = OperatingSystem.IsMacOS() ? ["@unix", "@mac"] : OperatingSystem.IsLinux() ? ["@unix", "@linux"] : [];
        List<string> worded = table.Keys.Where(k => k.Contains('@')).ToList();
        foreach (string suffix in mine)
        {
            foreach (string key in worded.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)))
            {
                table[key[..^suffix.Length]] = table[key];
            }
        }
        foreach (string key in worded)
        {
            table.Remove(key);
        }
    }
}
