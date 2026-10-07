using System.Globalization;
using System.Runtime.InteropServices;
using Uno.UI.Hosting;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// The entry point on macOS and Linux. WinWhatsApp runs once per user: a
/// second start tells the running one to show its window, and exits.
/// </summary>
public static partial class Program
{
    /// <summary>Started at sign-in: stays in the notification area without opening the window.</summary>
    public const string BackgroundSwitch = "--background";

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Crashed", e.ExceptionObject as Exception);

        if (!SingleInstance.TryBecomeFirst())
        {
            Log.Info("Handed over to the running instance");
            return 0;
        }

        LoadForDrawing();
        Loc.Use(SettingsStore.Load().Language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = Loc.Culture;

        // Before any text is laid out: the text engine reads these once. Controls
        // that name no font get WhatsApp's too, as App.xaml gives it to the rest.
        Uno.UI.FeatureConfiguration.Font.DefaultTextFontFamily = "ms-appx:///Assets/Fonts/Roboto.ttf";
        Uno.UI.FeatureConfiguration.Font.FallbackService = new Controls.EmojiFontFallback();

        UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseMacOS()
            .UseWin32()
            .Build()
            .Run();
        return 0;
    }

    /// <summary>
    /// The drawing library for ARM on Linux calls into libuuid without telling
    /// the system that it needs it, and the app stops at its first text unless
    /// something else brought libuuid in. Loaded here for all to see, it is found.
    /// </summary>
    private static void LoadForDrawing()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return;
        }
        const int now = 2, global = 0x100;
        try
        {
            nint library;
            try
            {
                library = dlopen("libuuid.so.1", now | global);
            }
            catch (EntryPointNotFoundException)
            {
                // Before glibc 2.34 the function was in a library of its own.
                library = dlopen_old("libuuid.so.1", now | global);
            }
            if (library == 0)
            {
                Log.Info("libuuid.so.1 is missing, which the app needs to draw; the package libuuid1 has it");
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Could not load libuuid", e);
        }
    }

    [LibraryImport("libc.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen(string file, int mode);

    [LibraryImport("libdl.so.2", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen_old(string file, int mode);
}
