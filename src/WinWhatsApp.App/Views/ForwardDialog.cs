using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.App.Controls;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>Picks the chats to forward a message to: recent chats first, then the other contacts.</summary>
internal sealed partial class ForwardDialog : ContentDialog
{
    private readonly Session _session;
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly TextBlock _status;
    private readonly List<Target> _targets;
    private readonly HashSet<string> _chosen = [];
    private bool _filling;

    private sealed record Target(string Jid, string Name, string? Detail, bool IsGroup, ImageSource? Avatar);

    public ForwardDialog(Session session)
    {
        _session = session;
        Title = Loc.T("chats.forwardTitle");
        PrimaryButtonText = Loc.T("chats.forward");
        CloseButtonText = Loc.T("common.cancel");
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _targets = session.Chats.All
            .Where(c => !c.IsReadOnly)
            .OrderByDescending(c => c.Pinned > 0)
            .ThenByDescending(c => c.Ts)
            .Select(c => new Target(c.Jid, c.Name, null, c.IsGroup, c.Avatar))
            .ToList();

        _search = new TextBox { PlaceholderText = Loc.T("chats.forwardSearch") };
        _search.TextChanged += (_, _) => Fill();
        _list = new ListView { Height = 360, SelectionMode = ListViewSelectionMode.Multiple };
        _list.SelectionChanged += OnSelectionChanged;
        _status = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        Content = new StackPanel { Width = 380, Spacing = 10, Children = { _search, _list, _status } };

        Opened += async (_, _) =>
        {
            _search.Focus(FocusState.Programmatic);
            Fill();
            try
            {
                List<ContactData> contacts = await _session.Client.GetContactsAsync();
                var known = _targets.Select(t => t.Jid).ToHashSet();
                _targets.AddRange(contacts.Where(c => !known.Contains(c.Jid)).Select(c => new Target(c.Jid, c.Name, c.Phone, false, null)));
                Fill();
            }
            catch (BridgeException)
            {
                // Recent chats are enough to choose from.
            }
        };
    }

    /// <summary>The chats picked, in the order they show.</summary>
    public IReadOnlyList<string> Chosen => _targets.Where(t => _chosen.Contains(t.Jid)).Select(t => t.Jid).ToList();

    private void Fill()
    {
        string query = _search.Text.Trim();
        string digits = new(query.Where(char.IsDigit).ToArray());
        IEnumerable<Target> matches = _targets.Where(t =>
            query.Length == 0 ||
            t.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (digits.Length >= 3 && t.Detail?.Contains(digits, StringComparison.Ordinal) == true));

        // Making the list again would read as unselecting what was picked.
        _filling = true;
        _list.Items.Clear();
        foreach (Target target in matches.Take(200))
        {
            var row = new ListViewItem
            {
                Tag = target.Jid,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Padding = new Thickness(0, 4, 0, 4),
                    Children =
                    {
                        new Avatar { Size = 36, IsGroup = target.IsGroup, Source = target.Avatar },
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new EmojiLabel { Text = target.Name },
                                new TextBlock
                                {
                                    Text = target.Detail ?? "",
                                    FontSize = 12,
                                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                                    Visibility = target.Detail is null ? Visibility.Collapsed : Visibility.Visible,
                                },
                            },
                        },
                    },
                },
            };
            _list.Items.Add(row);
            if (_chosen.Contains(target.Jid))
            {
                _list.SelectedItems.Add(row);
            }
        }
        _filling = false;
        UpdateStatus();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }
        foreach (ListViewItem row in e.AddedItems.OfType<ListViewItem>())
        {
            _chosen.Add((string)row.Tag);
        }
        foreach (ListViewItem row in e.RemovedItems.OfType<ListViewItem>())
        {
            _chosen.Remove((string)row.Tag);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        IsPrimaryButtonEnabled = _chosen.Count > 0;
        _status.Text = _chosen.Count switch
        {
            0 => "",
            1 => _targets.First(t => _chosen.Contains(t.Jid)).Name,
            _ => Loc.Plural("chats.chatCount", _chosen.Count),
        };
    }
}
