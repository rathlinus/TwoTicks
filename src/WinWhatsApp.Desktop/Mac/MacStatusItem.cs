using WinWhatsApp.App.Linux;

namespace WinWhatsApp.App.Mac;

/// <summary>The app's icon in the menu bar of macOS, with its menu.</summary>
internal sealed class MacStatusItem : IDisposable
{
    /// <summary>The icon, or null where it cannot be shown.</summary>
    public static MacStatusItem? Create(string iconFile, string toolTip, IReadOnlyList<TrayMenuItem> menu) => null;

    public void SetIcon(string file)
    {
    }

    public void SetToolTip(string text)
    {
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> menu)
    {
    }

    public void Dispose()
    {
    }
}
