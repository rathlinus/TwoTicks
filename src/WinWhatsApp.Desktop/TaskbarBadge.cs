using Tmds.DBus.Protocol;
using WinWhatsApp.App.Linux;

namespace WinWhatsApp.App;

/// <summary>
/// The number of unread chats on the app's icon: on the Dock of macOS, and on
/// the launchers of Linux desktops that show such a count, which are Ubuntu's
/// dock, Dash to Dock, KDE's task manager and Plank.
/// </summary>
internal sealed class TaskbarBadge : IDisposable
{
    // The launchers find the app by the name of its menu entry.
    private const string LauncherUri = "application://" + LinuxNotifications.DesktopEntry + ".desktop";
    private const string LauncherPath = "/io/github/rathlinus/WinWhatsApp";

    private int _shown = -1;

    public TaskbarBadge(Microsoft.UI.Xaml.Window window)
    {
    }

    public void Set(int count)
    {
        if (count == _shown)
        {
            return;
        }
        _shown = count;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                _ = SetLauncherCountAsync(count);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Mac.MacApp.SetDockBadge(count > 0 ? count.ToString() : "");
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not show the unread count on the app's icon", e);
        }
    }

    private static async Task SetLauncherCountAsync(int count)
    {
        if (await SessionBus.GetAsync().ConfigureAwait(false) is not { } bus)
        {
            return;
        }
        using MessageWriter writer = bus.GetMessageWriter();
        writer.WriteSignalHeader(null, LauncherPath, "com.canonical.Unity.LauncherEntry", "Update", "sa{sv}");
        writer.WriteString(LauncherUri);
        ArrayStart properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("count");
        writer.WriteVariantInt64(count);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("count-visible");
        writer.WriteVariantBool(count > 0);
        writer.WriteDictionaryEnd(properties);
        bus.TrySendMessage(writer.CreateMessage());
    }

    public void Dispose() => Set(0);
}
