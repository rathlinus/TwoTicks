using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwoTicks.Core;

namespace TwoTicks.App.Views;

/// <summary>The settings, and the account with the button to log out.</summary>
internal sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(Session session)
    {
        AppSettings settings = session.Settings;
        Title = Loc.T("settings.title");
        CloseButtonText = Loc.T("settings.done");
        DefaultButton = ContentDialogButton.Close;

        var panel = new StackPanel { Spacing = 4, Width = 420 };

        panel.Children.Add(Heading(Loc.T("settings.notifications")));
        panel.Children.Add(Toggle(Loc.T("settings.showNotifications"), settings.Notifications, v => settings.Notifications = v));
        panel.Children.Add(Toggle(Loc.T("settings.notificationPreview"), settings.NotificationPreview, v => settings.NotificationPreview = v));
        panel.Children.Add(Toggle(Loc.T("settings.notificationSound"), settings.NotificationSound, v => settings.NotificationSound = v));

        panel.Children.Add(Heading(Loc.T("settings.app")));
        panel.Children.Add(Toggle(Loc.T("settings.closeToTray"), settings.CloseToTray, v => settings.CloseToTray = v));
        panel.Children.Add(Toggle(Loc.T("settings.startAtSignIn"), Startup.IsEnabled, Startup.SetEnabled));
        panel.Children.Add(Toggle(Loc.T("settings.autoDownloadImages"), settings.AutoDownloadImages, v => settings.AutoDownloadImages = v));

        panel.Children.Add(LanguagePicker(settings));

        var theme = new RadioButtons { Header = Loc.T("settings.theme"), MaxColumns = 3, Margin = new Thickness(0, 8, 0, 0) };
        string[] themes = ["System", "Light", "Dark"];
        theme.Items.Add(Loc.T("settings.themeSystem"));
        theme.Items.Add(Loc.T("settings.themeLight"));
        theme.Items.Add(Loc.T("settings.themeDark"));
        // By position: SelectedItem compares the boxed strings by reference, and the
        // one from the settings file is never the same object, so nothing was selected.
        theme.SelectedIndex = Math.Max(0, Array.IndexOf(themes, settings.Theme));
        theme.SelectionChanged += (_, _) =>
        {
            settings.Theme = themes[Math.Max(0, theme.SelectedIndex)];
            App.Current.Window.ApplyTheme();
            // The dialog is not inside the window's content, so it follows by itself.
            RequestedTheme = App.Current.Window.Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default;
        };
        panel.Children.Add(theme);

        var icon = new RadioButtons { Header = Loc.T("settings.appIcon"), MaxColumns = 2, Margin = new Thickness(0, 8, 0, 0) };
        icon.Items.Add("TwoTicks");
        icon.Items.Add("WhatsApp");
        icon.SelectedIndex = settings.WhatsAppIcon ? 1 : 0;
        icon.SelectionChanged += (_, _) =>
        {
            settings.WhatsAppIcon = icon.SelectedIndex == 1;
            App.Current.ApplyIcon();
        };
        panel.Children.Add(icon);

        panel.Children.Add(Heading(Loc.T("settings.calls")));
        panel.Children.Add(DevicePicker(Loc.T("settings.microphone"), () => AudioDevices.NamesAsync(microphones: true), settings.Microphone, name =>
        {
            settings.Microphone = name;
            session.Calls.ApplyDevices();
        }));
        panel.Children.Add(DevicePicker(Loc.T("settings.speaker"), () => AudioDevices.NamesAsync(microphones: false), settings.Speaker, name =>
        {
            settings.Speaker = name;
            session.Calls.ApplyDevices();
        }));
        panel.Children.Add(DevicePicker(Loc.T("settings.camera"), AudioDevices.CameraNamesAsync, settings.Camera, name =>
        {
            settings.Camera = name;
            session.Calls.ApplyDevices();
        }));

        panel.Children.Add(Heading(Loc.T("settings.account")));
        string who = session.Me is { } me ? $"{me.Name}  (+{me.Jid.Split('@')[0]})" : Loc.T("settings.notLinked");
        panel.Children.Add(new TextBlock { Text = who, Margin = new Thickness(0, 0, 0, 8) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dataButton = new Button { Content = Loc.T("settings.openDataFolder") };
        dataButton.Click += (_, _) => Process.Start("explorer.exe", $"\"{AppPaths.DataFolder}\"");
        buttons.Children.Add(dataButton);

        var logout = new Button { Content = Loc.T("settings.logOut"), IsEnabled = session.Me is not null };
        logout.Click += async (_, _) =>
        {
            Hide();
            var confirm = new ContentDialog
            {
                Title = Loc.T("settings.logOutTitle"),
                Content = Loc.T("settings.logOutText"),
                PrimaryButtonText = Loc.T("settings.logOut"),
                CloseButtonText = Loc.T("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                await session.LogoutAsync();
            }
        };
        buttons.Children.Add(logout);
        panel.Children.Add(buttons);

        panel.Children.Add(Heading(Loc.T("settings.updates")));
        AddUpdates(panel, App.Current.Updater, settings);

        panel.Children.Add(Heading(Loc.T("settings.about")));
        panel.Children.Add(new TextBlock { Text = $"TwoTicks {typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3)}" });
        panel.Children.Add(Paragraph(Loc.T("settings.aboutText")));
        panel.Children.Add(new HyperlinkButton
        {
            Content = Loc.T("settings.sourceCode"),
            NavigateUri = new Uri("https://github.com/rathlinus/TwoTicks"),
            // No padding instead of a negative margin: the scroll viewer clips anything
            // left of the panel, which cut the hover background off at the left edge.
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 4),
        });

        panel.Children.Add(Heading(Loc.T("settings.disclaimer")));
        panel.Children.Add(Paragraph(Loc.T("settings.disclaimerAffiliation")));
        panel.Children.Add(Paragraph(Loc.T("settings.disclaimerRisk")));
        panel.Children.Add(Paragraph(Loc.T("settings.disclaimerLicenses")));

        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
    }

    /// <summary>The switches for updates, what the updater is doing and a button for the next step.</summary>
    private void AddUpdates(StackPanel panel, Updater updater, AppSettings settings)
    {
        panel.Children.Add(Toggle(Loc.T("settings.checkForUpdates"), settings.CheckForUpdates, v => settings.CheckForUpdates = v));
        CheckBox install = Toggle(Loc.T("settings.installUpdates"), settings.InstallUpdates, v => settings.InstallUpdates = v);
        install.IsEnabled = Updater.CanInstall;
        panel.Children.Add(install);
        if (!Updater.CanInstall)
        {
            panel.Children.Add(Paragraph(Loc.T("settings.cannotUpdate")));
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var button = new Button();
        button.Click += async (_, _) =>
        {
            switch (updater.State)
            {
                // The dialog shows the download; the app quits once setup starts.
                case UpdateState.Available or UpdateState.Ready:
                    await updater.InstallNowAsync();
                    break;
                default:
                    await updater.CheckAsync(manual: true);
                    break;
            }
        };

        void Refresh()
        {
            status.Text = updater.Describe();
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            button.Content = updater.State switch
            {
                UpdateState.Available or UpdateState.Ready when Updater.CanInstall => Loc.T("settings.installAndRestart"),
                UpdateState.Available or UpdateState.Ready => Loc.T("settings.download"),
                _ => Loc.T("settings.checkNow"),
            };
            button.IsEnabled = updater.State is not (UpdateState.Checking or UpdateState.Downloading);
        }
        Refresh();
        updater.Changed += Refresh;
        Closed += (_, _) => updater.Changed -= Refresh;

        panel.Children.Add(status);
        panel.Children.Add(button);
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

    /// <summary>
    /// The languages the app has, by their own names, after the one that follows
    /// Windows. The app reads its text at start, so a change asks to restart.
    /// </summary>
    private static StackPanel LanguagePicker(AppSettings settings)
    {
        var box = new ComboBox { Header = Loc.T("settings.language"), MinWidth = 300, Margin = new Thickness(0, 8, 0, 0) };
        box.Items.Add(Loc.T("settings.languageWindows"));
        foreach (AppLanguage language in Loc.Languages)
        {
            box.Items.Add(language.Name);
        }
        box.SelectedIndex = 1 + Loc.Languages.ToList().FindIndex(l => l.Code == settings.Language);

        var restart = new Button { Content = Loc.T("settings.restart"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        restart.Click += (_, _) =>
        {
            SettingsStore.Save(settings);
            App.Current.Restart();
        };
        box.SelectionChanged += (_, _) =>
        {
            settings.Language = box.SelectedIndex > 0 ? Loc.Languages[box.SelectedIndex - 1].Code : null;
            restart.Visibility = Visibility.Visible;
        };

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(restart);
        return panel;
    }

    /// <summary>
    /// A list of the microphones or speakers the system has, by name, with the
    /// system's default first. A device picked earlier that is not connected now
    /// stays in the list, so the choice is not lost.
    /// </summary>
    private static ComboBox DevicePicker(string header, Func<Task<IReadOnlyList<string>>> list, string? current, Action<string?> set)
    {
        string windowsDefault = Loc.T("settings.windowsDefault");
        var box = new ComboBox { Header = header, MinWidth = 300, Margin = new Thickness(0, 4, 0, 4) };
        box.Items.Add(windowsDefault);
        if (current is not null)
        {
            box.Items.Add(current);
        }
        box.SelectedIndex = current is null ? 0 : 1;
        box.Loaded += async (_, _) =>
        {
            IReadOnlyList<string> devices = await list();
            foreach (string name in devices.Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
            {
                if (name != current)
                {
                    box.Items.Add(name);
                }
            }
            box.SelectionChanged += (_, _) => set(box.SelectedIndex <= 0 ? null : box.SelectedItem as string);
        };
        return box;
    }

    private static CheckBox Toggle(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }
}
