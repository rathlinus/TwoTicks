using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>Starts a chat with a saved contact or with any phone number.</summary>
internal sealed partial class NewChatDialog : ContentDialog
{
    private readonly Session _session;
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly TextBlock _status;
    private List<ContactData> _contacts = [];

    public NewChatDialog(Session session)
    {
        _session = session;
        Title = Loc.T("main.newChat");
        PrimaryButtonText = Loc.T("chats.startChat");
        CloseButtonText = Loc.T("common.cancel");
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _search = new TextBox { PlaceholderText = Loc.T("chats.newChatSearch") };
        _search.TextChanged += (_, _) => Filter();
        _list = new ListView { Height = 340, SelectionMode = ListViewSelectionMode.Single };
        _list.SelectionChanged += (_, _) => IsPrimaryButtonEnabled = _list.SelectedItem is not null || LooksLikeNumber(_search.Text);
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is not null)
            {
                Choose();
                Hide();
            }
        };
        _status = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        Content = new StackPanel { Width = 380, Spacing = 10, Children = { _search, _list, _status } };

        PrimaryButtonClick += OnPrimaryClick;
        Opened += async (_, _) =>
        {
            _search.Focus(FocusState.Programmatic);
            try
            {
                _contacts = await _session.Client.GetContactsAsync();
            }
            catch (BridgeException e)
            {
                ShowStatus(e.Message);
            }
            Filter();
        };
    }

    public string? ChosenJid { get; private set; }

    private static bool LooksLikeNumber(string text) => text.Count(char.IsDigit) >= 7 && text.All(c => char.IsDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '/');

    private void Filter()
    {
        string query = _search.Text.Trim();
        string digits = new(query.Where(char.IsDigit).ToArray());
        IEnumerable<ContactData> matches = _contacts.Where(c =>
            query.Length == 0 ||
            c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (digits.Length >= 3 && c.Phone.Contains(digits, StringComparison.Ordinal)));

        _list.Items.Clear();
        foreach (ContactData contact in matches.Take(200))
        {
            _list.Items.Add(new ListViewItem
            {
                Tag = contact.Jid,
                Content = new StackPanel
                {
                    Padding = new Thickness(0, 4, 0, 4),
                    Children =
                    {
                        new TextBlock { Text = contact.Name },
                        new TextBlock { Text = contact.Phone, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                    },
                },
            });
        }
        if (_list.Items.Count > 0 && query.Length > 0)
        {
            _list.SelectedIndex = 0;
        }
        IsPrimaryButtonEnabled = _list.SelectedItem is not null || LooksLikeNumber(query);
    }

    private void Choose() => ChosenJid = (_list.SelectedItem as ListViewItem)?.Tag as string;

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_list.SelectedItem is not null)
        {
            Choose();
            return;
        }
        if (!LooksLikeNumber(_search.Text))
        {
            args.Cancel = true;
            return;
        }

        // Look the number up before closing; the dialog waits for it.
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            ChosenJid = await _session.Client.CheckNumberAsync(_search.Text);
        }
        catch (BridgeException e)
        {
            ShowStatus(e.Message);
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
    }
}
