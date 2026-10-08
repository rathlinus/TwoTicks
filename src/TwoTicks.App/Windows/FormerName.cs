using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Windows.UI.Notifications;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>
/// The app was called WinWhatsApp up to version 0.5, and what a version of
/// that time put on the computer still carries the name: its data folder, its
/// entry to start at sign-in, its notifications and its shortcuts. The app
/// takes them over, so someone who updates finds everything as it was. The
/// folder is <see cref="AppPaths.TakeOver"/>'s, the entry is
/// <see cref="Startup"/>'s and the shortcuts are <see cref="AppIcon"/>'s; the
/// rest is here.
/// </summary>
/// <remarks>
/// Only the app on its usual data does any of this. A copy that runs beside
/// it on data of its own, to try something out, leaves the computer alone.
/// </remarks>
internal static class FormerName
{
    public const string Name = AppPaths.FormerName;

    /// <summary>The program of the old version, as shortcuts name it.</summary>
    public const string Program = Name + ".exe";

    public static bool Applies => AppPaths.IsDefaultDataFolder;

    /// <summary>
    /// Ends a copy of the old version that still runs. It has the data open,
    /// which then cannot be taken over, and two versions on one account would
    /// throw each other off WhatsApp. Called before anything touches the data.
    /// </summary>
    public static void StopRunning()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
        if (!Applies || !Directory.Exists(data))
        {
            return;
        }
        int session = Process.GetCurrentProcess().SessionId;
        foreach (string name in (string[])[Name, Name + ".Bridge"])
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId == session)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5000);
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // Gone already, or not this user's.
                    }
                }
            }
        }
    }

    /// <summary>Removes what the old name registered with Windows for its notifications. The new name registers its own.</summary>
    public static void Forget()
    {
        if (!Applies)
        {
            return;
        }
        string key = $@"Software\Classes\AppUserModelId\{Name}";
        try
        {
            using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(key))
            {
                if (registered is null)
                {
                    return;
                }
            }
            try
            {
                // Clicking one of these would start nothing any more.
                ToastNotificationManager.History.Clear(Name);
            }
            catch (Exception)
            {
                // Windows does not know the name any more.
            }
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Could not remove what the old name registered", e);
        }
    }
}
