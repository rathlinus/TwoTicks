using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TwoTicks.App.Mac;

/// <summary>
/// The functions of libTwoTicksMac, the app's own small library for what
/// macOS only offers in Objective-C; see Mac/TwoTicksMac.m. Lists go over
/// as text, one entry per line with a tab between its parts.
/// </summary>
internal static unsafe partial class MacNative
{
    private const string Library = "TwoTicksMac";

    /// <summary>Whether the library is there: on a Mac, in a build made on one.</summary>
    public static bool IsAvailable { get; } = Load();

    private static bool Load()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        // In the app bundle the libraries are next to the program, the rest in Resources.
        string?[] folders = [AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath)];
        foreach (string? folder in folders)
        {
            if (folder is not null && NativeLibrary.TryLoad(Path.Combine(folder, "libTwoTicksMac.dylib"), out nint handle))
            {
                NativeLibrary.SetDllImportResolver(typeof(MacNative).Assembly,
                    (string name, Assembly _, DllImportSearchPath? _) => name == Library ? handle : 0);
                return true;
            }
        }
        Log.Info("libTwoTicksMac.dylib is missing: no menu bar icon and no notifications");
        return false;
    }

    /// <summary>A list as the library reads it: a line per row, a tab between the parts of a row.</summary>
    public static string Rows(IEnumerable<IEnumerable<string>> rows) =>
        string.Join('\n', rows.Select(row => string.Join('\t', row.Select(Clean))));

    private static string Clean(string part) => part.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_status_show(string iconPath, string toolTip, string menu, delegate* unmanaged[Cdecl]<int, void> clicked);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_status_set_icon(string iconPath);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_status_set_tooltip(string toolTip);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_status_set_menu(string menu);

    [LibraryImport(Library)]
    public static partial void wa_status_remove();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_dock_set_badge(string text);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_dock_set_icon(string iconPath);

    [LibraryImport(Library)]
    public static partial void wa_app_on_reopen(delegate* unmanaged[Cdecl]<void> callback);

    [LibraryImport(Library)]
    public static partial int wa_notifications_start(delegate* unmanaged[Cdecl]<byte*, byte*, void> activated);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_notifications_set_category(string identifier, string actions);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_notifications_show(string identifier, string category, string thread, string title,
        string body, string picture, int sound, string arguments);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void wa_notifications_remove(string identifiers);

    [LibraryImport(Library)]
    public static partial void wa_notifications_remove_all();
}
