using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinWhatsApp.Core;

public static class AppPaths
{
    /// <summary>
    /// Where everything lives: the session, the messages, downloaded media, the
    /// settings and the logs. WINWHATSAPP_DATA points a second copy elsewhere,
    /// for trying things without touching the real account.
    /// </summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("WINWHATSAPP_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWhatsApp");

    /// <summary>Whether the data folder is the usual one, or one set for testing.</summary>
    public static bool IsDefaultDataFolder => Environment.GetEnvironmentVariable("WINWHATSAPP_DATA") is not { Length: > 0 };

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

    public WindowPlacement? Window { get; set; }

    public double ChatListWidth { get; set; } = 380;

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
