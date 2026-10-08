using System.Globalization;
using System.Text.Json;

namespace TwoTicks.Core;

/// <summary>Where an emoji is drawn: a sprite sheet and the cell on it, in pixels of the sheet.</summary>
public readonly record struct EmojiCell(int Sheet, int X, int Y, int SheetWidth, int SheetHeight);

/// <summary>A piece of text: plain text, or one emoji that is drawn from the sprites.</summary>
public readonly record struct EmojiSegment(string Text, int Emoji)
{
    public bool IsEmoji => Emoji >= 0;
}

/// <summary>
/// WhatsApp's emoji: which ones there are, in which order, and where each is on
/// the sprite sheets WhatsApp Web draws them from.
/// </summary>
/// <remarks>
/// The sheets hold 25 emoji each, five by five, 40 pixels per emoji, in the
/// order of the list. The last sheet holds what is left, in a square of
/// columns as wide as fits.
/// </remarks>
public sealed class EmojiSet
{
    public const int CellSize = 40;
    private const int PerSheet = 25;
    private const int Columns = 5;

    private readonly Dictionary<string, int> _byText = new(StringComparer.Ordinal);
    private readonly List<string> _texts = [];
    private readonly List<(int Index, string[] Name, string[] Keywords)> _names = [];

    private EmojiSet()
    {
    }

    public int Count => _texts.Count;

    /// <summary>The emoji of each category of the picker, in WhatsApp's order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Categories { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Reads emoji.json, as scripts/whatsapp-assets/build.py writes it.</summary>
    public static EmojiSet Load(Stream json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var set = new EmojiSet();

        foreach (JsonElement glyph in root.GetProperty("glyphs").EnumerateArray())
        {
            int index = set._texts.Count;
            string? first = null;
            foreach (JsonElement variant in glyph.EnumerateArray())
            {
                string text = variant.GetString() ?? "";
                first ??= text;
                set.Add(text, index);
            }
            set._texts.Add(first ?? "");
        }
        foreach (JsonProperty legacy in root.GetProperty("legacy").EnumerateObject())
        {
            set.Add(legacy.Name, legacy.Value.GetInt32());
        }

        var categories = new Dictionary<string, IReadOnlyList<string>>();
        foreach (JsonProperty category in root.GetProperty("categories").EnumerateObject())
        {
            categories[category.Name] = category.Value.EnumerateArray().Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToList();
        }
        set.Categories = categories;
        return set;
    }

    /// <summary>Reads emoji-names.json: the name and keywords of each emoji, to search by.</summary>
    public void LoadNames(Stream json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            int index = Find(entry.Name);
            List<string> words = entry.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            if (index < 0 || words.Count == 0)
            {
                continue;
            }
            _names.Add((index, Words(words[0]), words.Skip(1).SelectMany(Words).Distinct().ToArray()));
        }
        _names.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    /// <summary>
    /// The emoji whose name or keywords have a word starting with each word of
    /// the query: those found by name first, then by keyword, each in
    /// WhatsApp's order.
    /// </summary>
    public List<string> Search(string query, int limit = 200)
    {
        string[] wanted = Words(query);
        if (wanted.Length == 0)
        {
            return [];
        }
        bool Has(string[] words, string start) => words.Any(w => w.StartsWith(start, StringComparison.Ordinal));
        var byName = new List<string>();
        var byKeyword = new List<string>();
        foreach ((int index, string[] name, string[] keywords) in _names)
        {
            if (wanted.All(w => Has(name, w)))
            {
                byName.Add(_texts[index]);
            }
            else if (wanted.All(w => Has(name, w) || Has(keywords, w)))
            {
                byKeyword.Add(_texts[index]);
            }
        }
        return byName.Concat(byKeyword).Take(limit).ToList();
    }

    private static string[] Words(string text) =>
        text.ToLowerInvariant().Split([' ', ':', ',', '-', '.', '’', '\''],StringSplitOptions.RemoveEmptyEntries);

    private void Add(string text, int index)
    {
        _byText.TryAdd(text, index);
        // Emoji are written with and without the variation selector that asks
        // for the colourful form; both mean the same.
        _byText.TryAdd(text.Replace("️", "", StringComparison.Ordinal), index);
    }

    /// <summary>The emoji a piece of text is, or -1.</summary>
    public int Find(string text)
    {
        if (_byText.TryGetValue(text, out int index))
        {
            return index;
        }
        return _byText.TryGetValue(text.Replace("️", "", StringComparison.Ordinal), out index) ? index : -1;
    }

    /// <summary>The text of an emoji, as it is sent.</summary>
    public string TextOf(int index) => _texts[index];

    public EmojiCell CellOf(int index)
    {
        int sheet = index / PerSheet;
        int position = index % PerSheet;
        int lastSheetStart = Count - Count % PerSheet;
        int columns = Columns;
        int rows = Columns;
        if (index >= lastSheetStart)
        {
            int left = Count - lastSheetStart;
            columns = (int)Math.Floor(Math.Sqrt(left));
            rows = (left + columns - 1) / columns;
        }
        return new EmojiCell(sheet, position % columns * CellSize, position / columns * CellSize, columns * CellSize, rows * CellSize);
    }

    /// <summary>
    /// Splits text into plain runs and single emoji. Text is split into what a
    /// reader sees as one character each, so an emoji made of several code
    /// points, such as a family or a flag, stays together.
    /// </summary>
    public List<EmojiSegment> Split(string text)
    {
        var segments = new List<EmojiSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }
        int plainStart = 0;
        int position = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int index = MightBeEmoji(element) ? Find(element) : -1;
            if (index >= 0)
            {
                if (position > plainStart)
                {
                    segments.Add(new EmojiSegment(text[plainStart..position], -1));
                }
                segments.Add(new EmojiSegment(element, index));
                plainStart = position + element.Length;
            }
            position += element.Length;
        }
        if (plainStart < text.Length)
        {
            segments.Add(new EmojiSegment(text[plainStart..], -1));
        }
        return segments;
    }

    /// <summary>Whether text has any emoji in it, without splitting it.</summary>
    public bool ContainsEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        foreach (char c in text)
        {
            if (char.IsSurrogate(c) || c >= 0x2000)
            {
                return Split(text).Exists(s => s.IsEmoji);
            }
        }
        return false;
    }

    // Plain letters and digits are never looked up; the few emoji that start
    // with one (keycaps such as 1️⃣) have a combining mark after it.
    private static bool MightBeEmoji(string element) =>
        element.Length > 1 || element[0] >= 0x00A9;
}
