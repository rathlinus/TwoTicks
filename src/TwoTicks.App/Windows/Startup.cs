using Microsoft.Win32;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>Starting TwoTicks at sign-in, in the notification area.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TwoTicks";

    private static string Command => $"\"{Environment.ProcessPath}\" {Program.BackgroundSwitch}";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Points the entry at this copy of the app, in case it moved since it was
    /// set. An entry under the app's former name becomes one under this name;
    /// see <see cref="FormerName"/>.
    /// </summary>
    public static void Refresh()
    {
        bool hadFormer = false;
        if (FormerName.Applies)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(FormerName.Name) is string)
            {
                key.DeleteValue(FormerName.Name, throwOnMissingValue: false);
                hadFormer = true;
            }
        }
        if (hadFormer || IsEnabled)
        {
            SetEnabled(true);
        }
    }
}
