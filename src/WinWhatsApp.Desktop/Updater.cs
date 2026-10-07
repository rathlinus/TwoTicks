namespace WinWhatsApp.App;

/// <summary>
/// On macOS and Linux the app tells about a new version and opens its download
/// page; it does not replace itself.
/// </summary>
internal sealed partial class Updater
{
    public static bool CanInstall => false;

    private partial void ShowWindow()
    {
    }

    public partial void CloseWindow()
    {
    }

    private partial bool Install(bool relaunch, bool background) => false;
}
