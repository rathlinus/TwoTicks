using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TwoTicks.App;

public partial class App
{
    /// <summary>How many pixels a window draws per unit of its layout.</summary>
    public double ScaleOf(Window window) => window.Content?.XamlRoot?.RasterizationScale ?? 1;

    /// <summary>Ties a file picker to the main window; nothing to do here.</summary>
    public void InitializePicker(object picker)
    {
    }

    partial void AdaptResources()
    {
        // App.xaml names the fixed-width fonts of Windows. Here it is the one the system uses for code.
        Resources["MonoFont"] = new FontFamily(OperatingSystem.IsMacOS() ? "Menlo" : OperatingSystem.IsWindows() ? "Consolas" : "monospace");
    }

    /// <summary>
    /// Whether there is a way back to a window that is off the screen: the icon
    /// in the notification area or the menu bar, or on macOS the one in the Dock.
    /// </summary>
    internal bool HasTray => _tray?.IsShown == true || Mac.MacApp.CanReopen;

    partial void Launched()
    {
        SelfCheck.Run(this);
        if (OperatingSystem.IsMacOS())
        {
            Mac.MacApp.OnReopen(() => _ui.TryEnqueue(ShowWindow));
        }
        if (Environment.GetCommandLineArgs().Contains(Program.BackgroundSwitch))
        {
            _ = ShowUnlessInTrayAsync();
        }
    }

    /// <summary>
    /// Started at login, the app stays out of sight in the notification area.
    /// A desktop without one gets the window, minimized: there would be no
    /// other way to it.
    /// </summary>
    private async Task ShowUnlessInTrayAsync()
    {
        // The desktop takes the icon a moment after the app asks.
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (!HasTray && !_quitting)
        {
            Window.ShowMinimized();
        }
    }

    private partial void ListenForSecondStart(Action started) => SingleInstance.Started += started;

    private partial void RestartProcess()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }
        try
        {
            SingleInstance.Release();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Log.Error("Failed to restart", e);
        }
    }
}
