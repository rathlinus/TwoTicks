using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwoTicks.Core;

public static class AppPaths
{
    /// <summary>
    /// Where everything lives: the session, the messages, downloaded media, the
    /// settings and the logs. TWOTICKS_DATA points a second copy elsewhere,
    /// for trying things without touching the real account.
    /// </summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("TWOTICKS_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwoTicks");

    /// <summary>Whether the data folder is the usual one, or one set for testing.</summary>
    public static bool IsDefaultDataFolder => Environment.GetEnvironmentVariable("TWOTICKS_DATA") is not { Length: > 0 };

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string LogFile => Path.Combine(DataFolder, "app.log");

    /// <summary>Pasted images wait here to be sent, and stay as the local copy of what was sent.</summary>
    public static string OutgoingFolder => Path.Combine(DataFolder, "media", "outgoing");
}

public sealed class AppSettings
{
    public bool Notifications { get; set; } = true;

    /// <summary>Whether notifications show the text of the message or only who sent it.</summary>
    public bool NotificationPreview { get; set; } = true;

    public bool NotificationSound { get; set; } = true;

    /// <summary>Closing the window keeps the app running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool AutoDownloadImages { get; set; } = true;

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>The language of the app, such as "de"; null for the one Windows shows.</summary>
    public string? Language { get; set; }

    /// <summary>Shows WhatsApp's own icon instead of the TwoTicks logo.</summary>
    public bool WhatsAppIcon { get; set; }

    public WindowPlacement? Window { get; set; }

    public double ChatListWidth { get; set; } = 380;

    /// <summary>The microphone for calls, by its name in Windows; null for the Windows default.</summary>
    public string? Microphone { get; set; }

    /// <summary>The speakers or headphones for calls, by name; null for the Windows default.</summary>
    public string? Speaker { get; set; }

    /// <summary>Looks for new versions on GitHub at start and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Downloads a new version and installs it while the window is closed, or when the app quits.</summary>
    public bool InstallUpdates { get; set; } = true;

    /// <summary>The newest version a notification already told about, so it does not come again.</summary>
    public string? AnnouncedUpdate { get; set; }

    /// <summary>The emoji picked last, newest first.</summary>
    public List<string> RecentEmoji { get; set; } = [];
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

public static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize(stream, SettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            string temporary = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
            File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The settings stay as they are for this session.
        }
    }
}
