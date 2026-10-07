using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinWhatsApp.App.Linux;

namespace WinWhatsApp.App.Mac;

/// <summary>The app's icon in the menu bar of macOS. A click opens its menu, as menu bar icons do.</summary>
internal sealed unsafe class MacStatusItem : IDisposable
{
    // The one icon there is; the library calls back without saying which.
    private static MacStatusItem? s_current;
    private IReadOnlyList<TrayMenuItem> _menu;

    private MacStatusItem(IReadOnlyList<TrayMenuItem> menu) => _menu = menu;

    /// <summary>The icon, or null where it cannot be shown.</summary>
    public static MacStatusItem? Create(string iconFile, string toolTip, IReadOnlyList<TrayMenuItem> menu)
    {
        if (!MacNative.IsAvailable)
        {
            SelfCheck.Note("Menu bar icon", "no native library");
            return null;
        }
        var item = new MacStatusItem(menu);
        s_current = item;
        MacNative.wa_status_show(iconFile, toolTip, Describe(menu), &OnClicked);
        SelfCheck.Note("Menu bar icon", "shown");
        return item;
    }

    private static string Describe(IReadOnlyList<TrayMenuItem> menu) =>
        MacNative.Rows(menu.Select(item => (string[])[item.Id.ToString(), item.Label]));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClicked(int id)
    {
        try
        {
            s_current?._menu.FirstOrDefault(item => item.Id == id)?.Clicked?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("A click in the menu bar icon's menu failed", e);
        }
    }

    public void SetIcon(string file) => MacNative.wa_status_set_icon(file);

    public void SetToolTip(string text) => MacNative.wa_status_set_tooltip(text);

    public void SetMenu(IReadOnlyList<TrayMenuItem> menu)
    {
        _menu = menu;
        MacNative.wa_status_set_menu(Describe(menu));
    }

    public void Dispose()
    {
        if (s_current == this)
        {
            s_current = null;
        }
        MacNative.wa_status_remove();
    }
}
