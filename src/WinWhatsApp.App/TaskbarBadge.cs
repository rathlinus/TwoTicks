using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinWhatsApp.App;

/// <summary>
/// The number of unread chats on the taskbar button, drawn as an overlay icon:
/// a green circle with the count in white.
/// </summary>
internal sealed class TaskbarBadge : IDisposable
{
    private readonly nint _window;
    private Native.ITaskbarList3? _taskbar;
    private nint _icon;
    private int _shown = -1;

    public TaskbarBadge(nint window)
    {
        _window = window;
    }

    public void Set(int count)
    {
        if (count == _shown)
        {
            return;
        }
        try
        {
            _taskbar ??= CreateTaskbar();
            nint previous = _icon;
            _icon = count > 0 ? Draw(count) : 0;
            _taskbar?.SetOverlayIcon(_window, _icon, count > 0 ? $"{count} unread chats" : null);
            if (previous != 0)
            {
                Native.DestroyIcon(previous);
            }
            _shown = count;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // The taskbar is not there, as while Explorer restarts. The next change tries again.
            _taskbar = null;
        }
    }

    /// <summary>Shows the badge again after the taskbar button was recreated.</summary>
    public void Refresh()
    {
        int count = _shown;
        _shown = -1;
        if (count >= 0)
        {
            Set(count);
        }
    }

    private static Native.ITaskbarList3 CreateTaskbar()
    {
        var taskbar = (Native.ITaskbarList3)new Native.TaskbarList();
        taskbar.HrInit();
        return taskbar;
    }

    private nint Draw(int count)
    {
        uint dpi = Native.GetDpiForWindow(_window);
        int size = Native.GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96 : dpi);
        string text = count > 99 ? "99+" : count.ToString();

        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(255, 0x1D, 0xAA, 0x61));
            g.FillEllipse(fill, 0, 0, size - 1, size - 1);

            float fontSize = size * (text.Length switch { 1 => 0.62f, 2 => 0.5f, _ => 0.36f });
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size + size * 0.06f), format);
        }
        return bitmap.GetHicon();
    }

    public void Dispose()
    {
        if (_icon != 0)
        {
            Native.DestroyIcon(_icon);
            _icon = 0;
        }
    }
}
