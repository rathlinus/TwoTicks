using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>
/// Keeps the app to one running copy per data folder. The first copy listens
/// on a socket of its own; a second start finds it there, tells it to show its
/// window, and exits.
/// </summary>
internal static class SingleInstance
{
    private static Socket? s_listener;
    private static string? s_path;

    /// <summary>The app was started again while this copy runs. Raised on a background thread.</summary>
    public static event Action? Started;

    /// <summary>
    /// The socket: in the folder the session keeps for such things, named
    /// after the data folder, so a copy with its own data runs beside the
    /// usual one. Not in the data folder itself, whose path may be longer than
    /// a socket's name can be.
    /// </summary>
    private static string SocketPath()
    {
        string folder = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)
            ? runtime
            : Path.GetTempPath();
        string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataFolder)))[..12];
        return Path.Combine(folder, $"winwhatsapp-{Environment.UserName}-{id}.sock");
    }

    /// <summary>
    /// True when this is the only copy, which then listens for others. False
    /// when another copy runs: it was told to show its window.
    /// </summary>
    public static bool TryBecomeFirst()
    {
        string path = SocketPath();
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send("show\n"u8);
            return false;
        }
        catch (SocketException)
        {
            // Nobody listens: this is the first copy, or the last one left its socket behind.
        }

        try
        {
            File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            s_listener = listener;
            s_path = path;
            _ = Task.Run(() => AcceptAsync(listener));
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not listen for a second start of the app", e);
        }
        return true;
    }

    private static async Task AcceptAsync(Socket listener)
    {
        while (true)
        {
            try
            {
                using Socket other = await listener.AcceptAsync().ConfigureAwait(false);
                Started?.Invoke();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>Stops listening, so the copy started for a restart becomes the first one.</summary>
    public static void Release()
    {
        s_listener?.Dispose();
        s_listener = null;
        if (s_path is not null)
        {
            try
            {
                File.Delete(s_path);
            }
            catch (IOException)
            {
            }
            s_path = null;
        }
    }
}
