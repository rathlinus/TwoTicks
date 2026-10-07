using WinWhatsApp.Core;

namespace WinWhatsApp.App;

internal sealed class Notifier
{
    public const string AppId = "WinWhatsApp";

    public event Action<string?>? Opened;
    public event Action<string, string>? Replied;
    public event Action<string>? MarkedRead;
    public event Action? CallAnswered;
    public event Action? CallDeclined;
    public event Action? CallOpened;
    public event Action? UpdateRequested;

    public void Register(string assets) { }
    public void SetIcon(string assets) { }
    public void ClearAll() { }
    public void ShowMessage(string chatName, MessageData message, string? avatarPath, bool showText, bool sound) { }
    public void ShowCall(CallData call, string? avatarPath) { }
    public void ShowIncomingCall(string name, string? avatarPath) { }
    public void ClearIncomingCall() { }
    public void ShowUpdate(string version, bool canInstall) { }
    public void Clear(string chat) { }
}
