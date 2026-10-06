using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinWhatsApp.App.Controls;

public sealed partial class EmojiPicker : UserControl
{
    private const int RecentCount = 36;

    // WhatsApp's categories, with the icon of each tab.
    private static readonly (string Key, string Icon, string Name)[] s_categories =
    [
        ("SMILEYS_PEOPLE", "EmojiPeople", "Smileys and people"),
        ("ANIMALS_NATURE", "EmojiNature", "Animals and nature"),
        ("FOOD_DRINK", "EmojiFood", "Food and drink"),
        ("ACTIVITY", "EmojiActivity", "Activity"),
        ("TRAVEL_PLACES", "EmojiTravel", "Travel and places"),
        ("OBJECTS", "EmojiObjects", "Objects"),
        ("SYMBOLS", "EmojiSymbols", "Symbols"),
        ("FLAGS", "EmojiFlags", "Flags"),
    ];

    private readonly List<ToggleButton> _tabs = [];

    public EmojiPicker()
    {
        InitializeComponent();
        AddTab("Recent", "EmojiRecent", "Recently used");
        foreach ((string key, string icon, string name) in s_categories)
        {
            AddTab(key, icon, name);
        }
        Loaded += (_, _) => Show(RecentEmoji.Count > 0 ? "Recent" : "SMILEYS_PEOPLE");
    }

    /// <summary>Raised with the emoji that was clicked.</summary>
    public event Action<string>? Picked;

    private static List<string> RecentEmoji => App.Current.Session.Settings.RecentEmoji;

    private void AddTab(string key, string icon, string name)
    {
        var tab = new ToggleButton
        {
            Tag = key,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new WaIcon { Kind = icon, Size = 20 },
        };
        ToolTipService.SetToolTip(tab, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab, name);
        tab.Click += (_, _) => Show(key);
        _tabs.Add(tab);
        Tabs.Children.Add(tab);
    }

    private void Show(string key)
    {
        foreach (ToggleButton tab in _tabs)
        {
            tab.IsChecked = (string)tab.Tag == key;
        }
        if (Emoji.Set is null)
        {
            return;
        }
        IEnumerable<string> list = key == "Recent"
            ? RecentEmoji
            : Emoji.Set.Categories.TryGetValue(key, out IReadOnlyList<string>? emoji) ? emoji : [];
        EmojiGrid.ItemsSource = list.Where(e => Emoji.Set.Find(e) >= 0).ToList();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid host || args.Item is not string text || Emoji.Set is null)
        {
            return;
        }
        host.Children.Clear();
        int index = Emoji.Set.Find(text);
        if (index >= 0)
        {
            host.Children.Add(Emoji.Create(index, 32));
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string emoji)
        {
            return;
        }
        List<string> recent = RecentEmoji;
        recent.Remove(emoji);
        recent.Insert(0, emoji);
        if (recent.Count > RecentCount)
        {
            recent.RemoveRange(RecentCount, recent.Count - RecentCount);
        }
        Picked?.Invoke(emoji);
    }
}
