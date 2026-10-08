using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>
/// The entry point. TwoTicks runs once per user: a second start, also one
/// from a clicked notification, hands its activation to the running instance
/// and exits.
/// </summary>
public static class Program
{
    private const string InstanceKey = "TwoTicks";

    /// <summary>Started at sign-in: stays in the notification area without opening the window.</summary>
    public const string BackgroundSwitch = "--background";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Crashed", e.ExceptionObject as Exception);

        // Ties the taskbar button, the Start menu entry and the notifications together.
        Native.SetCurrentProcessExplicitAppUserModelID(Notifier.AppId);

        if (HandOverToRunningInstance())
        {
            Log.Info("Handed over to the running instance");
            return 0;
        }

        UseLanguage(SettingsStore.Load().Language);

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>
    /// Picks the language for the app's text, and for the text WinUI brings
    /// itself, such as the menu of a text box. It holds until the app quits:
    /// another one in the settings takes effect at the next start.
    /// </summary>
    private static void UseLanguage(string? language)
    {
        Loc.Use(language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = Loc.Culture;
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Loc.Language;
        }
        catch (Exception e)
        {
            Log.Error("Failed to set the language", e);
        }
    }

    private static bool HandOverToRunningInstance()
    {
        // A copy with its own data folder runs beside the usual one.
        string key = AppPaths.IsDefaultDataFolder ? InstanceKey : InstanceKey + ":" + AppPaths.DataFolder.ToLowerInvariant();
        AppInstance main = AppInstance.FindOrRegisterForKey(key);
        if (main.IsCurrent)
        {
            return false;
        }

        AppActivationArguments arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        // The hand-over must not block this thread's COM calls, so it runs on
        // another one while this one waits. Managed waits on an STA thread keep
        // pumping messages.
        using var done = new ManualResetEvent(false);
        Task.Run(async () =>
        {
            try
            {
                await main.RedirectActivationToAsync(arguments);
            }
            finally
            {
                done.Set();
            }
        });
        done.WaitOne(TimeSpan.FromSeconds(10));
        return true;
    }
}
