using System.Runtime.InteropServices;
using System.Text;
using TwoTicks.Core;

namespace TwoTicks.App;

internal static partial class AppIcon
{
    /// <summary>
    /// Gives the Start menu entry and a pinned taskbar button the icon, and
    /// the Start menu entry the name that goes with it. Windows takes the icon
    /// and the name of a running app's taskbar button from the shortcut with
    /// the same AppUserModelID, so the window's own icon and title do not
    /// change them once the app is installed.
    /// </summary>
    /// <remarks>
    /// Shortcuts the app's former name left behind are taken over on the way;
    /// see <see cref="FormerName"/>. A pinned taskbar button keeps the name of
    /// its file: Windows knows the button by that file, and renaming it could
    /// lose the button.
    /// </remarks>
    public static partial void UpdateShortcuts(bool whatsApp)
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        (string Folder, bool IsMenu)[] folders =
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
            (Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
        ];
        // An empty location means the icon of the program the shortcut starts.
        string icon = whatsApp ? Path.Combine(Folder(true), "AppIcon.ico") : "";

        bool changed = false;
        foreach ((string folder, bool isMenu) in folders)
        {
            if (Directory.Exists(folder))
            {
                changed |= UpdateShortcuts(folder, isMenu, exe, icon, AppName.Of(whatsApp), FormerName.Applies);
            }
        }
        if (changed)
        {
            // Tells Explorer to draw the icons again.
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, null, 0);
        }
    }

    /// <summary>Does the above for the shortcuts in one folder; true if any of them changed.</summary>
    /// <param name="isMenu">Whether the folder is the Start menu, where a shortcut's file name is the app's name.</param>
    /// <param name="takeOver">Whether shortcuts of the former name that start nothing any more become this app's.</param>
    internal static bool UpdateShortcuts(string folder, bool isMenu, string exe, string icon, string name, bool takeOver)
    {
        string[] shortcuts;
        try
        {
            shortcuts = Directory.GetFiles(folder, "*.lnk");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var mine = new List<string>();
        var former = new List<string>();
        foreach (string path in shortcuts)
        {
            Try(path, () =>
            {
                string target = TargetOf(path);
                if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase))
                {
                    mine.Add(path);
                }
                else if (takeOver && string.Equals(Path.GetFileName(target), FormerName.Program, StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
                {
                    former.Add(path);
                }
            });
        }

        bool changed = false;
        foreach (string path in former)
        {
            Try(path, () =>
            {
                if (isMenu && mine.Count > 0)
                {
                    // Setup wrote a new entry; the old one starts a program that is gone.
                    File.Delete(path);
                    Native.SHChangeNotify(Native.SHCNE_DELETE, Native.SHCNF_PATHW, path, 0);
                }
                else
                {
                    Retarget(path, exe);
                    mine.Add(path);
                }
                changed = true;
            });
        }

        if (isMenu && mine.Count > 1)
        {
            // One entry is enough. Setup writes one under the app's own name at
            // every update, beside one this renamed before.
            string keep = mine.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? mine[0];
            foreach (string path in mine.Where(p => p != keep).ToList())
            {
                Try(path, () =>
                {
                    File.Delete(path);
                    Native.SHChangeNotify(Native.SHCNE_DELETE, Native.SHCNF_PATHW, path, 0);
                    mine.Remove(path);
                    changed = true;
                });
            }
        }

        foreach (string path in mine)
        {
            Try(path, () =>
            {
                changed |= SetIcon(path, icon);
                if (isMenu)
                {
                    changed |= Rename(path, name);
                }
            });
        }
        return changed;

        static void Try(string path, Action change)
        {
            try
            {
                change();
            }
            catch (Exception e) when (e is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
            {
                Log.Error($"Failed to update the shortcut {path}", e);
            }
        }
    }

    /// <summary>The program a shortcut starts, as the shortcut has it, whether it is still there or not.</summary>
    private static string TargetOf(string path)
    {
        var link = (Native.IShellLinkW)new Native.ShellLink();
        try
        {
            ((Native.IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, 0, 0);
            return target.ToString();
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>Gives the shortcut the icon; true if it changed.</summary>
    private static bool SetIcon(string path, string icon)
    {
        var link = (Native.IShellLinkW)new Native.ShellLink();
        try
        {
            var file = (Native.IPersistFile)link;
            file.Load(path, 0);

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

    /// <summary>
    /// Makes a shortcut of the former name start this program, under this
    /// app's AppUserModelID, so a taskbar button pinned back then is the
    /// button of the running app again.
    /// </summary>
    private static void Retarget(string path, string exe)
    {
        var link = (Native.IShellLinkW)new Native.ShellLink();
        try
        {
            var file = (Native.IPersistFile)link;
            // To write: a shortcut that was opened to read does not take properties.
            file.Load(path, Native.STGM_READWRITE);
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");

            var store = (Native.IPropertyStore)link;
            Native.PropertyKey id = Native.AppUserModelIdKey;
            var value = new Native.PropVariant { Type = Native.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(Notifier.AppId) };
            try
            {
                store.SetValue(ref id, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }

            file.Save(path, true);
            Native.SHChangeNotify(Native.SHCNE_UPDATEITEM, Native.SHCNF_PATHW, path, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// Gives a Start menu entry the name, which is its file's name; true if it
    /// changed. An entry of that name that is another program's stays, and
    /// this one keeps its name then.
    /// </summary>
    private static bool Rename(string path, string name)
    {
        string renamed = Path.Combine(Path.GetDirectoryName(path)!, name + ".lnk");
        if (string.Equals(path, renamed, StringComparison.OrdinalIgnoreCase) || File.Exists(renamed))
        {
            return false;
        }
        File.Move(path, renamed);
        Native.SHChangeNotifyRename(Native.SHCNE_RENAMEITEM, Native.SHCNF_PATHW, path, renamed);
        return true;
    }
}
