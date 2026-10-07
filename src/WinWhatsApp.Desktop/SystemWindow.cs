using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;

namespace WinWhatsApp.App;

/// <summary>
/// What the app asks of a window that Uno does not offer: taking it off the
/// screen without closing it, and bringing it back in front.
/// </summary>
internal static partial class SystemWindow
{
    /// <summary>Takes the window off the screen and out of the list of open windows; false when that is not possible here.</summary>
    public static bool Hide(Window window)
    {
        try
        {
            if (OperatingSystem.IsLinux() && X11Window(window) is { } x11)
            {
                nint display = Display();
                if (display == 0)
                {
                    return false;
                }
                XWithdrawWindow(display, x11, XDefaultScreen(display));
                XFlush(display);
                return true;
            }
            if (OperatingSystem.IsMacOS() && NSWindow(window) is { } ns and not 0)
            {
                objc_msgSend(ns, sel_registerName("orderOut:"), 0);
                return true;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Could not hide the window", e);
        }
        return false;
    }

    /// <summary>Puts a hidden window back on the screen.</summary>
    public static void Show(Window window)
    {
        try
        {
            if (OperatingSystem.IsLinux() && X11Window(window) is { } x11)
            {
                nint display = Display();
                if (display != 0)
                {
                    XMapRaised(display, x11);
                    XFlush(display);
                }
            }
            else if (OperatingSystem.IsMacOS() && NSWindow(window) is { } ns and not 0)
            {
                objc_msgSend(ns, sel_registerName("makeKeyAndOrderFront:"), 0);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Could not show the window", e);
        }
    }

    /// <summary>In front of the windows of other apps, as after a click on a notification.</summary>
    public static void BringToFront(Window window)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                nint app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                objc_msgSend(app, sel_registerName("activateIgnoringOtherApps:"), 1);
            }
            else if (OperatingSystem.IsLinux() && X11Window(window) is { } x11)
            {
                nint display = Display();
                if (display != 0)
                {
                    XRaiseWindow(display, x11);
                    XFlush(display);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Could not bring the window to the front", e);
        }
    }

    /// <summary>The window's NSWindow on macOS, or null.</summary>
    public static nint? NSWindow(Window window)
    {
        // Uno keeps the handle in a class of its own, which it does not show.
        object? native = window.GetNativeWindow();
        return native?.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(native) as nint?;
    }

    private static nint? X11Window(Window window) => window.GetNativeWindow() is X11NativeWindow native ? native.WindowId : null;

    // A connection of the app's own to the X server; window ids hold on all connections.
    private static nint s_display;

    private static nint Display()
    {
        if (s_display == 0)
        {
            s_display = XOpenDisplay(0);
        }
        return s_display;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XDefaultScreen(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XWithdrawWindow(nint display, nint window, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XMapRaised(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XRaiseWindow(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("/usr/lib/libobjc.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport("/usr/lib/libobjc.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.dylib")]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport("/usr/lib/libobjc.dylib")]
    private static partial nint objc_msgSend(nint receiver, nint selector, nint argument);
}
