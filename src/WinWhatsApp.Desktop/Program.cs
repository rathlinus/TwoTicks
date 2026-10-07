using System.Globalization;
using Uno.UI.Hosting;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// The entry point on macOS and Linux. WinWhatsApp runs once per user: a
/// second start tells the running one to show its window, and exits.
/// </summary>
public static class Program
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
}
