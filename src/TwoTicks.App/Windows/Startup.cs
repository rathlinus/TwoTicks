using Microsoft.Win32;

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

    /// <summary>Points the entry at this copy of the app, in case it moved since it was set.</summary>
    public static void Refresh()
    {
        if (IsEnabled)
        {
            SetEnabled(true);
        }
    }
}
