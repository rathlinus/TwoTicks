using System.Security;

namespace WinWhatsApp.App;

/// <summary>
/// Starting WinWhatsApp when the user logs in, without opening its window. On
/// Linux that is an entry in the autostart folder every desktop reads, on
/// macOS a launch agent.
/// </summary>
internal static class Startup
{
    private const string Id = Linux.LinuxNotifications.DesktopEntry;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string EntryPath => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "LaunchAgents", Id + ".plist")
        : Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Home, ".config"),
            "autostart", Id + ".desktop");

    public static bool IsEnabled => File.Exists(EntryPath);

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(EntryPath);
                return;
            }
            if (Environment.ProcessPath is not { } exe)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            File.WriteAllText(EntryPath, OperatingSystem.IsMacOS() ? LaunchAgent(exe) : DesktopEntry(exe));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not change whether the app starts at login", e);
        }
    }

    /// <summary>Points the entry at this copy of the app, in case it moved since it was set.</summary>
    public static void Refresh()
    {
        if (IsEnabled)
        {
            SetEnabled(true);
        }
    }

    private static string DesktopEntry(string exe) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=WinWhatsApp
        Exec={Quote(exe)} {Program.BackgroundSwitch}
        Icon={Id}
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    /// <summary>A path as the Exec line of a desktop entry takes it: in quotes, with what quotes do not cover escaped.</summary>
    internal static string Quote(string path) =>
        "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal) + "\"";

    private static string LaunchAgent(string exe) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Id}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(exe)}</string>
                <string>{Program.BackgroundSwitch}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;
}
