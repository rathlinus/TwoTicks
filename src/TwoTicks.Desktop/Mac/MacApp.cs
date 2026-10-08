using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TwoTicks.App.Mac;

/// <summary>What the app asks of macOS itself: its icon in the Dock.</summary>
internal static unsafe class MacApp
{
    private static Action? s_reopened;

    /// <summary>Whether a click on the Dock icon brings the window back, so that it can be taken off the screen.</summary>
    public static bool CanReopen => s_reopened is not null;

    /// <summary>The text on the app's icon in the Dock; empty for none.</summary>
    public static void SetDockBadge(string text)
    {
        if (MacNative.IsAvailable)
        {
            MacNative.wa_dock_set_badge(text);
        }
    }

    /// <summary>The app's icon in the Dock while it runs, from a picture file; null for the app bundle's own.</summary>
    public static void SetDockIcon(string? file)
    {
        if (MacNative.IsAvailable)
        {
            MacNative.wa_dock_set_icon(file ?? "");
        }
    }

    /// <summary>
    /// Calls back when the Dock icon is clicked, or the app is opened again
    /// from the Finder, while it runs: the way back to a window that was closed.
    /// </summary>
    public static void OnReopen(Action reopened)
    {
        if (MacNative.IsAvailable)
        {
            s_reopened = reopened;
            MacNative.wa_app_on_reopen(&OnReopened);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReopened()
    {
        try
        {
            s_reopened?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("Reopening the window failed", e);
        }
    }
}
