using TwoTicks.Core;

namespace TwoTicks.App;

internal static partial class AppIcon
{
    private const string Id = Linux.LinuxNotifications.DesktopEntry;

    /// <summary>
    /// On Linux a desktop finds an app's name and icon, for its dock and its
    /// notifications, in the app's menu entry. A package installs one. A copy
    /// unpacked from the archive has none, so it writes its own into the
    /// user's folder, pointing at where it runs from, with the icon picked in
    /// the settings and the name that goes with it. A user's entry comes
    /// before the package's, so an installed copy gets one too while it goes
    /// by WhatsApp's name. On macOS the app bundle has the icon and the name.
    /// The icon in the Dock can be another while the app runs; the name
    /// beside it cannot.
    /// </summary>
    public static partial void UpdateShortcuts(bool whatsApp)
    {
        if (OperatingSystem.IsMacOS())
        {
            Mac.MacApp.SetDockIcon(whatsApp ? Path.Combine(Folder(true), "AppIcon.png") : null);
            return;
        }
        // Not for a copy that only tries something out on its own data.
        if (!OperatingSystem.IsLinux() || !AppPaths.IsDefaultDataFolder || Environment.ProcessPath is not { } exe)
        {
            return;
        }
        try
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home
                ? home
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            string icon = Path.Combine(data, "icons", "hicolor", "256x256", "apps", Id + ".png");
            string entry = Path.Combine(data, "applications", Id + ".desktop");
            bool installed = File.Exists($"/usr/share/applications/{Id}.desktop") || File.Exists($"/usr/local/share/applications/{Id}.desktop");

            // The user's icon comes before the package's, which is how the other icon shows there.
            if (!installed || whatsApp)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
                File.Copy(Path.Combine(Folder(whatsApp), "AppIcon.png"), icon, overwrite: true);
            }
            else if (File.Exists(icon))
            {
                File.Delete(icon);
            }
            if (installed && !whatsApp)
            {
                // The package's entry says it all.
                if (File.Exists(entry))
                {
                    File.Delete(entry);
                }
                return;
            }

            string text =
                $"""
                [Desktop Entry]
                Type=Application
                Name={AppName.Of(whatsApp)}
                GenericName=WhatsApp client
                Comment=A native WhatsApp app
                Exec={Startup.Quote(exe)}
                Icon={Id}
                Terminal=false
                Categories=Network;InstantMessaging;Chat;
                StartupWMClass=TwoTicks
                StartupNotify=true

                """;
            if (!File.Exists(entry) || File.ReadAllText(entry) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
                File.WriteAllText(entry, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not write the app's menu entry", e);
        }
    }
}
