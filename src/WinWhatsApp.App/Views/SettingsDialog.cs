using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>The settings, and the account with the button to log out.</summary>
internal sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(Session session)
    {
        AppSettings settings = session.Settings;
        Title = "Settings";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;

        var panel = new StackPanel { Spacing = 4, Width = 420 };

        panel.Children.Add(Heading("Notifications"));
        panel.Children.Add(Toggle("Show notifications for new messages", settings.Notifications, v => settings.Notifications = v));
        panel.Children.Add(Toggle("Show the message text in notifications", settings.NotificationPreview, v => settings.NotificationPreview = v));
        panel.Children.Add(Toggle("Play a sound", settings.NotificationSound, v => settings.NotificationSound = v));

        panel.Children.Add(Heading("App"));
        panel.Children.Add(Toggle("Keep running in the notification area when the window is closed", settings.CloseToTray, v => settings.CloseToTray = v));
        panel.Children.Add(Toggle("Start WinWhatsApp when I sign in", Startup.IsEnabled, Startup.SetEnabled));
        panel.Children.Add(Toggle("Download photos automatically", settings.AutoDownloadImages, v => settings.AutoDownloadImages = v));

        var theme = new RadioButtons { Header = "Theme", MaxColumns = 3, Margin = new Thickness(0, 8, 0, 0) };
        foreach (string name in new[] { "System", "Light", "Dark" })
        {
            theme.Items.Add(name);
        }
        theme.SelectedItem = settings.Theme;
        theme.SelectionChanged += (_, _) =>
        {
            settings.Theme = theme.SelectedItem as string ?? "System";
            App.Current.Window.ApplyTheme();
        };
        panel.Children.Add(theme);

        var icon = new RadioButtons { Header = "App icon", MaxColumns = 2, Margin = new Thickness(0, 8, 0, 0) };
        icon.Items.Add("WinWhatsApp");
        icon.Items.Add("WhatsApp");
        icon.SelectedIndex = settings.WhatsAppIcon ? 1 : 0;
        icon.SelectionChanged += (_, _) =>
        {
            settings.WhatsAppIcon = icon.SelectedIndex == 1;
            App.Current.ApplyIcon();
        };
        panel.Children.Add(icon);

        panel.Children.Add(Heading("Account"));
        string who = session.Me is { } me ? $"{me.Name}  (+{me.Jid.Split('@')[0]})" : "Not linked";
        panel.Children.Add(new TextBlock { Text = who, Margin = new Thickness(0, 0, 0, 8) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dataButton = new Button { Content = "Open data folder" };
        dataButton.Click += (_, _) => Process.Start("explorer.exe", $"\"{AppPaths.DataFolder}\"");
        buttons.Children.Add(dataButton);

        var logout = new Button { Content = "Log out", IsEnabled = session.Me is not null };
        logout.Click += async (_, _) =>
        {
            Hide();
            var confirm = new ContentDialog
            {
                Title = "Log out?",
                Content = "This unlinks the PC from your phone and deletes the messages and files stored on it. They stay on your phone.",
                PrimaryButtonText = "Log out",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                await session.LogoutAsync();
            }
        };
        buttons.Children.Add(logout);
        panel.Children.Add(buttons);

        panel.Children.Add(Heading("About"));
        panel.Children.Add(new TextBlock { Text = $"WinWhatsApp {typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3)}" });
        panel.Children.Add(Paragraph(
            "A native WhatsApp app for Windows. It links to your phone the way WhatsApp Web does: the phone keeps your account, and this PC is one of its linked devices."));
        panel.Children.Add(new HyperlinkButton
        {
            Content = "Source code and updates on GitHub",
            NavigateUri = new Uri("https://github.com/rathlinus/WinWhatsApp"),
            Margin = new Thickness(-12, 0, 0, 0),
        });

        panel.Children.Add(Heading("Disclaimer"));
        panel.Children.Add(Paragraph(
            "WinWhatsApp is not made by, affiliated with or endorsed by WhatsApp or Meta. The name WhatsApp, its logo, emoji, icons and wallpapers belong to them."));
        panel.Children.Add(Paragraph(
            "WhatsApp has no public API for personal accounts. Using a client WhatsApp did not make is against its terms of service, and WhatsApp can ban accounts for it. Bans of accounts that only chat normally are rare, but you use WinWhatsApp at your own risk."));
        panel.Children.Add(Paragraph(
            "WinWhatsApp talks to WhatsApp with whatsmeow, under the Mozilla Public License 2.0. Its font is Roboto, by Google, under the Apache License 2.0."));

        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 12, 0, 2),
    };

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 4),
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static CheckBox Toggle(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }
}
