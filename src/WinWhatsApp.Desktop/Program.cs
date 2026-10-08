using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Uno.Foundation.Extensibility;
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
        FindVlc();
        AudioPlayer.ClearCopies();
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
    /// The drawing library for ARM on Linux calls into libuuid and FreeType
    /// without telling the system that it needs them, and the app stops at its
    /// first text unless something else brought them in. Loaded here for all to
    /// see, they are found. On x64 the library names what it needs.
    /// </summary>
    private static void LoadForDrawing()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return;
        }
        const int now = 2, global = 0x100;
        foreach (string name in (string[])["libfontconfig.so.1", "libfreetype.so.6", "libuuid.so.1"])
        {
            try
            {
                nint library;
                try
                {
                    library = dlopen(name, now | global);
                }
                catch (EntryPointNotFoundException)
                {
                    // Before glibc 2.34 the function was in a library of its own.
                    library = dlopen_old(name, now | global);
                }
                if (library == 0)
                {
                    Log.Info(name + " is missing, which the app needs to draw text");
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Error("Could not load " + name, e);
            }
        }
    }

    /// <summary>
    /// Sound and video play through VLC's library on Linux. Uno and the code
    /// it calls VLC with both look for "libvlc.so", a name the library only
    /// has where VLC's development package is installed; everyone else has
    /// libvlc.so.5. This makes the player work with that one.
    /// </summary>
    private static void FindVlc()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        if (NativeLibrary.TryLoad("libvlc.so", out nint found))
        {
            // The name is there: nothing to make up for.
            NativeLibrary.Free(found);
            return;
        }
        if (!NativeLibrary.TryLoad("libvlc.so.5", out _))
        {
            Log.Info("VLC's library is not installed: voice messages and videos do not play. The package libvlc5 or vlc-libs has it.");
            return;
        }

        // The calls into VLC ask .NET for "libvlc", which it does not find; this answers then.
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            string? file = name switch
            {
                "libvlc" => "libvlc.so.5",
                "libvlccore" => "libvlccore.so.9",
                _ => null,
            };
            return file is not null && NativeLibrary.TryLoad(file, out nint library) ? library : 0;
        };

        // Uno hands sound and video to its VLC player only when it found
        // "libvlc.so" itself. Told here what it would have told itself.
        try
        {
            const string player = "Uno.UI.MediaPlayer.Skia.X11";
            Type playerType = Type.GetType($"{player}.SharedMediaPlayerExtension, {player}", throwOnError: true)!;
            Type presenterType = Type.GetType($"{player}.X11MediaPlayerPresenterExtension, {player}", throwOnError: true)!;
            ApiExtensibility.Register(Role(playerType, "IMediaPlayerExtension"), owner => Create(playerType, owner));
            ApiExtensibility.Register(Role(presenterType, "IMediaPlayerPresenterExtension"), owner => Create(presenterType, owner));
        }
        catch (Exception e) when (e is TypeLoadException or FileNotFoundException or FileLoadException or InvalidOperationException)
        {
            Log.Error("Could not set up the player for voice messages and videos", e);
        }

        // What Uno asks for when it needs a player: the interface the type stands in for.
        static Type Role(Type type, string name) =>
            type.GetInterfaces().FirstOrDefault(role => role.Name == name) ?? throw new TypeLoadException($"{type.Name} is no {name}");

        static object Create(Type type, object owner) =>
            Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [owner], null)!;
    }

    [LibraryImport("libc.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen(string file, int mode);

    [LibraryImport("libdl.so.2", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen_old(string file, int mode);
}
