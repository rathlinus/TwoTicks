using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinWhatsApp.App;

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
