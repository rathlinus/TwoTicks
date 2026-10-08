using System.Runtime.InteropServices;
using System.Text;

namespace TwoTicks.App;

internal static partial class AppIcon
{
    /// <summary>
    /// Points the Start menu entry and a pinned taskbar button at the icon.
    /// Windows takes the icon of a running app's taskbar button from the
    /// shortcut with the same AppUserModelID, so the window's icon alone does
    /// not change it once the app is installed.
    /// </summary>
    public static partial void UpdateShortcuts(bool whatsApp)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        ];
        // An empty location means the icon of the program the shortcut starts.
        string icon = whatsApp ? Path.Combine(Folder(true), "AppIcon.ico") : "";

        bool changed = false;
        foreach (string folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.GetFiles(folder, "*.lnk");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string path in shortcuts)
            {
                try
                {
                    changed |= SetShortcutIcon(path, exe, icon);
                }
                catch (Exception e) when (e is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
                {
                    Log.Error($"Failed to change the icon of {path}", e);
                }
            }
        }
        if (changed)
        {
            // Tells Explorer to draw the icons again.
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, null, 0);
        }
    }

    /// <summary>Gives the shortcut the icon if it starts this app; true if it changed.</summary>
    private static bool SetShortcutIcon(string path, string exe, string icon)
    {
        var link = (Native.IShellLinkW)new Native.ShellLink();
        try
        {
            var file = (Native.IPersistFile)link;
            file.Load(path, 0);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, 0, 0);
            if (!string.Equals(target.ToString(), exe, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var current = new StringBuilder(1024);
            link.GetIconLocation(current, current.Capacity, out _);
            if (string.Equals(current.ToString(), icon, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            link.SetIconLocation(icon, 0);
            file.Save(path, true);
            Native.SHChangeNotify(Native.SHCNE_UPDATEITEM, Native.SHCNF_PATHW, path, 0);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
