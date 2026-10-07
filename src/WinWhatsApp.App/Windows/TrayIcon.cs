using System.Runtime.InteropServices;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// The icon in the notification area. A click opens the window; the menu has
/// Open and Quit, and an entry to install a new version when there is one. The icon gets a red dot while chats are unread.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WM_APP + 1;
    private const int OpenCommand = 1;
    private const int QuitCommand = 2;
    private const int UpdateCommand = 3;
    private const string WindowClass = "WinWhatsApp.TrayIcon";

    // Kept in a field: the window procedure must outlive every call Windows makes to it.
    private readonly Native.WndProc _windowProcedure;
    private readonly nint _window;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private nint _unreadIcon;
    private bool _unread;
    private string _tooltip = "WinWhatsApp";
    private bool _added;

    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action? UpdateRequested;

    /// <summary>The version the menu offers to install; null for none.</summary>
    public string? UpdateVersion { get; set; }

    /// <param name="assets">The folder with Tray.ico and TrayUnread.ico; see <see cref="AppIcon"/>.</param>
    public TrayIcon(string assets)
    {
        _windowProcedure = WindowProcedure;
        nint instance = Native.GetModuleHandle(0);
        fixed (char* className = WindowClass)
        {
            var windowClass = new Native.WNDCLASSEX
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEX),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                hInstance = instance,
                lpszClassName = (nint)className,
            };
            Native.RegisterClassEx(windowClass);
        }
        // A message-only window: it receives the icon's clicks and is never shown.
        _window = Native.CreateWindowEx(0, WindowClass, "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, instance, 0);
        // Sent to all windows when Explorer restarts; the icon has to be added again.
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        _icon = LoadIcon(Path.Combine(assets, "Tray.ico"));
        _unreadIcon = LoadIcon(Path.Combine(assets, "TrayUnread.ico"));
        Add();
    }

    /// <summary>Switches to the icons in another folder.</summary>
    public void SetIcons(string assets)
    {
        nint icon = _icon, unreadIcon = _unreadIcon;
        _icon = LoadIcon(Path.Combine(assets, "Tray.ico"));
        _unreadIcon = LoadIcon(Path.Combine(assets, "TrayUnread.ico"));
        if (_added)
        {
            var data = CreateData(Native.NIF_ICON);
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }
        Native.DestroyIcon(icon);
        Native.DestroyIcon(unreadIcon);
    }

    private nint LoadIcon(string path)
    {
        uint dpi = Native.GetDpiForWindow(_window);
        int size = Native.GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96 : dpi);
        return Native.LoadImage(0, path, Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);
    }

    private Native.NOTIFYICONDATA CreateData(uint flags)
    {
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATA),
            hWnd = _window,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _unread ? _unreadIcon : _icon,
        };
        ReadOnlySpan<char> tip = _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));
        data.szTip[tip.Length] = '\0';
        return data;
    }

    private void Add()
    {
        var data = CreateData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    /// <summary>Shows how many chats are unread, in the icon and its tooltip.</summary>
    public void SetUnread(int chats)
    {
        _unread = chats > 0;
        _tooltip = chats > 0 ? Loc.Plural("notify.trayUnread", chats) : "WinWhatsApp";
        if (_added)
        {
            var data = CreateData(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            // With version 4 the low word of lParam is the mouse or keyboard event.
            switch ((int)(lParam & 0xFFFF))
            {
                case Native.WM_LBUTTONUP:
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    OpenRequested?.Invoke();
                    break;
                case Native.WM_CONTEXTMENU:
                case Native.WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return 0;
        }
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            Add();
            return 0;
        }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = Native.CreatePopupMenu();
        Native.AppendMenu(menu, Native.MF_STRING, OpenCommand, Loc.T("notify.trayOpen"));
        if (UpdateVersion is not null)
        {
            Native.AppendMenu(menu, Native.MF_STRING, UpdateCommand, Loc.T("notify.trayInstall", ("version", UpdateVersion)));
        }
        Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
        Native.AppendMenu(menu, Native.MF_STRING, QuitCommand, Loc.T("notify.trayQuit"));
        Native.GetCursorPos(out Native.POINT point);
        // Without this the menu would not close when clicking elsewhere.
        Native.SetForegroundWindow(_window);
        int command = Native.TrackPopupMenuEx(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY, point.X, point.Y, _window, 0);
        Native.DestroyMenu(menu);
        switch (command)
        {
            case OpenCommand:
                OpenRequested?.Invoke();
                break;
            case QuitCommand:
                QuitRequested?.Invoke();
                break;
            case UpdateCommand:
                UpdateRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }
        Native.DestroyIcon(_icon);
        Native.DestroyIcon(_unreadIcon);
        Native.DestroyWindow(_window);
    }
}
