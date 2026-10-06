using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>The app's log, next to the helper's in the data folder.</summary>
internal static class Log
{
    private const long MaxSize = 2 << 20;
    private static readonly Lock s_lock = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        lock (s_lock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report it.
            }
        }
    }
}
