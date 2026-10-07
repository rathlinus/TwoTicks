using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using WinWhatsApp.App.Models;

namespace WinWhatsApp.App;

/// <summary>
/// A run of the app that checks itself, for the build on machines nobody
/// looks at. With WINWHATSAPP_CHECK set to a folder, the app starts as usual,
/// opens the first chat, saves a picture of its window and a report there,
/// and quits: with exit code 0 when the chats showed, 1 when not.
/// </summary>
/// <remarks>
/// It is meant for the demo helper of scripts/screenshots, which serves
/// made-up chats; the build workflow runs it on macOS and Linux.
/// </remarks>
internal static class SelfCheck
{
    private static readonly string? s_folder = Environment.GetEnvironmentVariable("WINWHATSAPP_CHECK") is { Length: > 0 } folder ? Path.GetFullPath(folder) : null;
    private static readonly StringBuilder s_report = new();

    public static bool IsOn => s_folder is not null;

    /// <summary>A line for the report: what was tried and how it went.</summary>
    public static void Note(string what, string result)
    {
        if (IsOn)
        {
            lock (s_report)
            {
                s_report.AppendLine($"{what}: {result}");
            }
        }
    }

    public static async void Run(App app)
    {
        if (s_folder is null)
        {
            return;
        }
        bool ok = false;
        try
        {
            Directory.CreateDirectory(s_folder);
            Note("System", $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
            Note("Emoji font", File.Exists(Controls.Emoji.FontFile) ? "there" : "missing");

            Session session = app.Session;
            for (int i = 0; i < 100 && session.Chats.Visible.Count == 0; i++)
            {
                await Task.Delay(200);
            }
            Note("Chats", session.Chats.Visible.Count.ToString());
            Note("State", session.State);
            if (session.Chats.Visible.FirstOrDefault() is ChatItem first)
            {
                await session.OpenAsync(first.Jid);
                await Task.Delay(3000);
                Note("Open chat", session.Current is { } open ? $"{open.Chat.Name}, {open.Items.Count} rows" : "none");
                ok = session.Current is { Items.Count: > 0 };
            }
            await Task.Delay(1000);
            if (app.Window.Content is FrameworkElement root)
            {
                await SaveAsync(root, Path.Combine(s_folder, "window.png"));
                Note("Picture", $"{root.ActualWidth:0} x {root.ActualHeight:0}");
            }
        }
        catch (Exception e)
        {
            ok = false;
            Note("Failed", e.ToString());
        }
        try
        {
            lock (s_report)
            {
                File.WriteAllText(Path.Combine(s_folder, "report.txt"), s_report.ToString());
            }
        }
        catch (IOException)
        {
        }
        Environment.ExitCode = ok ? 0 : 1;
        app.Quit();
    }

    private static async Task SaveAsync(FrameworkElement element, string path)
    {
        var picture = new RenderTargetBitmap();
        await picture.RenderAsync(element);
        byte[] pixels = (await picture.GetPixelsAsync()).ToArray();
        var info = new SKImageInfo(picture.PixelWidth, picture.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), Math.Min(pixels.Length, bitmap.ByteCount));
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, png.ToArray());
    }
}
