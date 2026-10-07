namespace WinWhatsApp.App;

/// <summary>
/// The icon the app shows: the WinWhatsApp logo, or WhatsApp's own icon when
/// the setting asks for it. Both come as the same set of files, the logo in
/// Assets and WhatsApp's icon in Assets\WhatsAppIcon.
/// </summary>
internal static partial class AppIcon
{
    public static string Folder(bool whatsApp) => whatsApp
        ? Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsAppIcon")
        : Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>Gives the shortcuts and launchers that start the app the icon too, where the system keeps one for them.</summary>
    public static partial void UpdateShortcuts(bool whatsApp);
}
