using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

public sealed partial class EmojiPicker : UserControl
{
    private const int RecentCount = 36;

    // WhatsApp's categories, with the icon of each tab.
    private static (string Key, string Icon, string Name)[] Categories =>
    [
        ("SMILEYS_PEOPLE", "EmojiPeople", Loc.T("conversation.emojiSmileys")),
        ("ANIMALS_NATURE", "EmojiNature", Loc.T("conversation.emojiAnimals")),
        ("FOOD_DRINK", "EmojiFood", Loc.T("conversation.emojiFood")),
        ("ACTIVITY", "EmojiActivity", Loc.T("conversation.emojiActivity")),
        ("TRAVEL_PLACES", "EmojiTravel", Loc.T("conversation.emojiTravel")),
        ("OBJECTS", "EmojiObjects", Loc.T("conversation.emojiObjects")),
        ("SYMBOLS", "EmojiSymbols", Loc.T("conversation.emojiSymbols")),
        ("FLAGS", "EmojiFlags", Loc.T("conversation.emojiFlags")),
    ];

    private readonly List<ToggleButton> _tabs = [];
    private string _tab = "SMILEYS_PEOPLE";

    public EmojiPicker()
    {
        InitializeComponent();
        AddTab("Recent", "EmojiRecent", Loc.T("conversation.emojiRecent"));
        foreach ((string key, string icon, string name) in Categories)
        {
            AddTab(key, icon, name);
        }
        Loaded += (_, _) => Show(RecentEmoji.Count > 0 ? "Recent" : "SMILEYS_PEOPLE");
    }

    /// <summary>Starts with an empty search and the cursor in it, so typing searches.</summary>
    public void FocusSearch()
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
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
        _tab = key;
        if (SearchBox.Text.Length > 0)
        {
            // Clearing the search shows this tab.
            SearchBox.Text = "";
            return;
        }
        NothingFound.Visibility = Visibility.Collapsed;
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0 || Emoji.Set is null)
        {
            Show(_tab);
            return;
        }
        foreach (ToggleButton tab in _tabs)
        {
            tab.IsChecked = false;
        }
        List<string> found = Emoji.Set.Search(query);
        EmojiGrid.ItemsSource = found;
        NothingFound.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
