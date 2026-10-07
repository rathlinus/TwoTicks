using Tmds.DBus.Protocol;

namespace WinWhatsApp.App.Linux;

/// <summary>
/// The app's connection to the D-Bus session bus, over which a Linux desktop
/// takes notifications, the icon in the notification area and the count on
/// the app's launcher.
/// </summary>
internal static class SessionBus
{
    private static readonly Lazy<Task<Connection?>> s_connection = new(ConnectAsync);

    /// <summary>The connection, or null where there is no session bus.</summary>
    public static Task<Connection?> GetAsync() => s_connection.Value;

    private static async Task<Connection?> ConnectAsync()
    {
        if (Address.Session is not { Length: > 0 } address)
        {
            Log.Info("No D-Bus session bus: no notifications and no icon in the notification area");
            return null;
        }
        try
        {
            var connection = new Connection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch (Exception e)
        {
            Log.Error("Could not connect to the D-Bus session bus", e);
            return null;
        }
    }

    /// <summary>Whether a service is on the bus right now.</summary>
    public static async Task<bool> HasOwnerAsync(Connection connection, string service)
    {
        try
        {
            return await connection.CallMethodAsync(Call(connection), static (Message m, object? _) => m.GetBodyReader().ReadBool()).ConfigureAwait(false);
        }
        catch (DBusException)
        {
            return false;
        }

        MessageBuffer Call(Connection c)
        {
            using MessageWriter writer = c.GetMessageWriter();
            writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s");
            writer.WriteString(service);
            return writer.CreateMessage();
        }
    }

    /// <summary>Calls back whenever a service comes onto the bus, also when it comes again after it was gone.</summary>
    public static async Task WatchForAsync(Connection connection, string service, Action appeared)
    {
        try
        {
            await connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = "org.freedesktop.DBus",
                    Path = "/org/freedesktop/DBus",
                    Interface = "org.freedesktop.DBus",
                    Member = "NameOwnerChanged",
                    Arg0 = service,
                },
                static (Message m, object? _) =>
                {
                    Reader reader = m.GetBodyReader();
                    reader.ReadString();
                    reader.ReadString();
                    return reader.ReadString();
                },
                (Exception? error, string newOwner, object? _, object? _) =>
                {
                    if (error is null && newOwner.Length > 0)
                    {
                        appeared();
                    }
                },
                ObserverFlags.None,
                emitOnCapturedContext: false).ConfigureAwait(false);
        }
        catch (DBusException e)
        {
            Log.Error($"Could not watch for {service}", e);
        }
    }
}
