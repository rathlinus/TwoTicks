using System.Diagnostics;
using System.Reflection;
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
            await NoteFontsAsync();
            Note("Microphones", string.Join(", ", await AudioDevices.NamesAsync(microphones: true)) is { Length: > 0 } microphones ? microphones : "none listed");
            Note("Speakers", string.Join(", ", await AudioDevices.NamesAsync(microphones: false)) is { Length: > 0 } speakers ? speakers : "none listed");
            Note("Browser for calls", Calls.CallBrowser.Find()?.Name ?? "none");
            Note("Chats", session.Chats.Visible.Count.ToString());
            Note("State", session.State);
            if (session.Chats.Visible.FirstOrDefault() is ChatItem first)
            {
                await session.OpenAsync(first.Jid);
                await Task.Delay(400);
                CaptureScreen("screen-opening.png");
                await Task.Delay(3000);
                Note("Open chat", session.Current is { } open ? $"{open.Chat.Name}, {open.Items.Count} rows" : "none");
                ok = session.Current is { Items.Count: > 0 };
            }
            await NoteCallAsync(session);
            await Task.Delay(1000);
            if (app.Window.Content is FrameworkElement root)
            {
                Note("Scale", (root.XamlRoot?.RasterizationScale ?? 0).ToString("0.##"));
                Note("Theme", root.ActualTheme.ToString());
                await SaveAsync(root, Path.Combine(s_folder, "window.png"));
                Note("Picture", $"{root.ActualWidth:0} x {root.ActualHeight:0}");
            }
            CaptureScreen("screen.png");
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

    /// <summary>
    /// Calls the first person of the made-up chats, to see the calling engine
    /// come up in a browser of this system. Nobody answers there, so the call
    /// is ended as soon as the engine is ready, or after a while when it is not.
    /// A system without a browser for it is not a failed check.
    /// </summary>
    private static async Task NoteCallAsync(Session session)
    {
        if (Calls.CallBrowser.Find() is null || session.Chats.Visible.FirstOrDefault(chat => !chat.IsGroup) is not { } person)
        {
            return;
        }
        var watch = Stopwatch.StartNew();
        await session.Calls.StartAsync(person.Jid, person.Name);
        while (!session.Calls.IsEngineReady && session.Calls.Phase != Calls.CallPhase.Idle && watch.Elapsed < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(250);
        }
        Note("Calling engine", session.Calls.IsEngineReady ? $"ready after {watch.Elapsed.TotalSeconds:0.#} s" : $"not ready: {session.Calls.Status}");
        session.Calls.HangUp();
        await Task.Delay(500);
    }

    /// <summary>
    /// Which font the text engine takes for an emoji, once written as the
    /// private code point the app shows it with and once as it is: the fonts
    /// of a system have their say in this, and no two systems have the same.
    /// </summary>
    private static async Task NoteFontsAsync()
    {
        try
        {
            if (Controls.Emoji.Set is not { Count: > 0 })
            {
                return;
            }
            using SKTypeface? emoji = SKTypeface.FromFile(Controls.Emoji.FontFile);
            using SKTypeface? text = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Roboto-Regular.ttf"));
            // The first emoji of the list as the app writes it, and raised hands as everyone does.
            (string, int)[] points = [("private", Controls.Emoji.PrivateUse), ("plain", 0x1F64C)];
            foreach ((string name, int point) in points)
            {
                Note($"Font for {name} U+{point:X}",
                    $"emoji font glyph {emoji?.GetGlyph(point)}, text font glyph {text?.GetGlyph(point)}, " +
                    $"system default {SKTypeface.Default.FamilyName} glyph {SKTypeface.Default.GetGlyph(point)}, " +
                    $"system match {SKFontManager.Default.MatchCharacter(point)?.FamilyName ?? "none"}, " +
                    $"symbols font {await EngineFontAsync("GetFont", point, Uno.UI.FeatureConfiguration.Font.SymbolsFont)}, " +
                    $"fallback {await EngineFontAsync("GetFontForCodepoint", point, point)}");

            }
            // Where the emoji font's picture lands, from a line to write on at
            // x 20 and y 100. The picture is made for size 30: there it is 40
            // wide and high, 2 right of the start and 8 below the line, which
            // is from 22, 68 to 62, 108.
            foreach (float size in (float[])[15, 30, 60])
            {
                using var bitmap = new SKBitmap(200, 200);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);
                using var font = new SKFont(emoji, size);
                using var paint = new SKPaint();
                canvas.DrawText(char.ConvertFromUtf32(Controls.Emoji.PrivateUse + 500), 20, 100, font, paint);
                int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (bitmap.GetPixel(x, y).Alpha > 0)
                        {
                            (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                        }
                    }
                }
                var widths = new float[1];
                var bounds = new SKRect[1];
                font.GetGlyphWidths([emoji?.GetGlyph(Controls.Emoji.PrivateUse + 500) ?? 0], widths, bounds);
                Note($"Emoji picture at size {size}", right < 0
                    ? $"nothing drawn; the font says {bounds[0]}, {widths[0]:0.#} wide"
                    : $"{left}, {top} to {right}, {bottom}; the font says {bounds[0]}, {widths[0]:0.#} wide");
            }
        }
        catch (Exception e)
        {
            Note("Font check", "failed: " + e);
        }
    }

    /// <summary>What the text engine's own font cache answers, which it keeps to itself.</summary>
    private static async Task<string> EngineFontAsync(string method, int point, object first)
    {
        Type? cache = typeof(Microsoft.UI.Xaml.Documents.Run).Assembly.GetType("Microsoft.UI.Xaml.Documents.TextFormatting.FontDetailsCache");
        MethodInfo? get = cache?.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (get is null)
        {
            return "unknown";
        }
        object? result = get.Invoke(null, [first, 14f, Microsoft.UI.Text.FontWeights.Normal, Windows.UI.Text.FontStretch.Normal, Windows.UI.Text.FontStyle.Normal]);
        // GetFont answers with the font for now and a task for the one that loads.
        if (result?.GetType().GetField("Item2")?.GetValue(result) is Task both)
        {
            result = both;
        }
        if (result is not Task task)
        {
            return "unknown";
        }
        await task;
        object? details = task.GetType().GetProperty("Result")?.GetValue(task);
        if (details is null)
        {
            return "none";
        }
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object? font = details.GetType().GetProperty("SKFont", any)?.GetValue(details) ?? details.GetType().GetField("SKFont", any)?.GetValue(details);
        return font is SKFont sk ? $"{sk.Typeface.FamilyName} glyph {sk.GetGlyph(point)}" : "unknown";
    }

    /// <summary>
    /// What the screen shows, beside what the app draws of itself: on a Mac,
    /// where the system's own tool takes the picture.
    /// </summary>
    private static void CaptureScreen(string name)
    {
        if (!OperatingSystem.IsMacOS() || s_folder is null)
        {
            return;
        }
        try
        {
            using var capture = System.Diagnostics.Process.Start("screencapture", ["-x", Path.Combine(s_folder, name)]);
            capture.WaitForExit(5000);
        }
        catch (Exception e)
        {
            Note("Screen capture", "failed: " + e.Message);
        }
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
