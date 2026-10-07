namespace WinWhatsApp.App;

internal sealed class TrayIcon : IDisposable
{
    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action? UpdateRequested;

    public string? UpdateVersion { get; set; }

    public TrayIcon(string assets) { }
    public void SetIcons(string assets) { }
    public void SetUnread(int chats) { }
    public void Dispose() { }
}
