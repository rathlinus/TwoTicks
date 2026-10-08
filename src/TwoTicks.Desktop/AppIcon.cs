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
    /// the settings. On macOS the app bundle has the icon.
    /// </summary>
    public static partial void UpdateShortcuts(bool whatsApp)
    {
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
            if (installed)
            {
                return;
            }

            string text =
                $"""
                [Desktop Entry]
                Type=Application
                Name=TwoTicks
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
