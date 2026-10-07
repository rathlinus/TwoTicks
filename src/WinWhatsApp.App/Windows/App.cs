using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WinWhatsApp.App;

public partial class App
{
    public nint WindowHandle => WindowHandleOf(Window);

    public nint WindowHandleOf(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>How many pixels a window draws per unit of its layout.</summary>
    public double ScaleOf(Window window) => Native.GetDpiForWindow(WindowHandleOf(window)) / 96.0;

    /// <summary>Ties a file picker to the main window, which a picker needs before it can show.</summary>
    public void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

    private partial void ListenForSecondStart(Action started) =>
        AppInstance.GetCurrent().Activated += (_, _) => started();

    private partial void RestartProcess()
    {
        // Ends this process when it works.
        var failure = AppInstance.Restart("");
        Log.Info($"Failed to restart: {failure}");
    }
}
