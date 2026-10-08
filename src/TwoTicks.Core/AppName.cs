namespace TwoTicks.Core;

/// <summary>
/// The name the app goes by outside its own window: on the taskbar, in the
/// Start menu or the launcher, in the notification area and above its
/// notifications. That is its own name, or WhatsApp's for whoever picked
/// WhatsApp's icon in the settings, as an icon with another name beside it
/// would be neither. Inside its window the app is always TwoTicks.
/// </summary>
public static class AppName
{
    public const string Own = "TwoTicks";
    public const string WhatsApp = "WhatsApp";

    /// <summary>The name to show now. The app sets it from the settings when it starts and when the icon is changed.</summary>
    public static string Shown { get; private set; } = Own;

    public static string Of(bool whatsApp) => whatsApp ? WhatsApp : Own;

    public static void Use(bool whatsApp) => Shown = Of(whatsApp);
}
