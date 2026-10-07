namespace WinWhatsApp.App;

/// <summary>Keeps the app to one running copy per data folder.</summary>
internal static class SingleInstance
{
    /// <summary>The app was started again while this copy runs. Raised on a background thread.</summary>
    public static event Action? Started;

    public static void Release()
    {
    }
}
