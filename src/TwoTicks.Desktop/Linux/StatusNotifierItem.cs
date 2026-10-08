using System.Text;
using SkiaSharp;
using Tmds.DBus.Protocol;
using TwoTicks.Core;

namespace TwoTicks.App.Linux;

/// <summary>An entry of the icon's menu.</summary>
internal sealed record TrayMenuItem(int Id, string Label, Action? Clicked)
{
    public bool IsSeparator => Clicked is null && Label.Length == 0;
}

/// <summary>
/// The icon in the notification area of a Linux desktop, as a
/// StatusNotifierItem on the session bus: the way KDE, the AppIndicator
/// extension of GNOME, Xfce and the others take such icons. Its menu is a
/// com.canonical.dbusmenu object next to it, which the desktop draws.
/// </summary>
/// <remarks>
/// The desktop asks for everything: the icon as pixels, the tooltip, the menu's
/// entries. The app answers and tells it with signals when something changed.
/// Where no desktop part takes such icons, <see cref="IsShown"/> stays false.
/// </remarks>
internal sealed class StatusNotifierItem : IDisposable
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string MenuPath = "/MenuBar";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly string _name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    private Connection? _bus;
    private List<(int Size, byte[] Pixels)> _icon = [];
    private string _title = AppName.Shown;
    private string _tooltip = AppName.Shown;
    private IReadOnlyList<TrayMenuItem> _menu = [];
    private uint _menuRevision = 1;
    private volatile bool _disposed;

    /// <summary>A click on the icon. Raised on a background thread.</summary>
    public event Action? Activated;

    /// <summary>Whether a desktop part that shows such icons took this one.</summary>
    public bool IsShown { get; private set; }

    public async Task StartAsync()
    {
        Connection? bus = await SessionBus.GetAsync().ConfigureAwait(false);
        if (bus is null || _disposed)
        {
            return;
        }
        try
        {
            bus.AddMethodHandler(new Handler(ItemPath, HandleItem));
            bus.AddMethodHandler(new Handler(MenuPath, HandleMenu));
            await bus.RequestNameAsync(_name, RequestNameOptions.None).ConfigureAwait(false);
            _bus = bus;
            // The part of the desktop that keeps the icons may start after the app, or again.
            await SessionBus.WatchForAsync(bus, WatcherService, () => _ = RegisterAsync()).ConfigureAwait(false);
            await RegisterAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error("Could not set up the icon in the notification area", e);
        }
    }

    private async Task RegisterAsync()
    {
        if (_bus is not { } bus || _disposed)
        {
            return;
        }
        try
        {
            if (!await SessionBus.HasOwnerAsync(bus, WatcherService).ConfigureAwait(false))
            {
                IsShown = false;
                Log.Info("This desktop has no notification area for the app's icon");
                SelfCheck.Note("Tray", "no StatusNotifierWatcher");
                return;
            }
            await bus.CallMethodAsync(Register(bus)).ConfigureAwait(false);
            IsShown = await bus.CallMethodAsync(HostRegistered(bus), static (Message m, object? _) =>
            {
                Reader reader = m.GetBodyReader();
                reader.ReadSignature("b");
                return reader.ReadBool();
            }).ConfigureAwait(false);
            SelfCheck.Note("Tray", IsShown ? "registered" : "registered, but nothing shows it");
        }
        catch (Exception e)
        {
            IsShown = false;
            Log.Info("The desktop did not take the icon for the notification area: " + e.Message);
            SelfCheck.Note("Tray", "failed: " + e.Message);
        }

        MessageBuffer Register(Connection c)
        {
            using MessageWriter writer = c.GetMessageWriter();
            writer.WriteMethodCallHeader(WatcherService, WatcherPath, WatcherService, "RegisterStatusNotifierItem", "s");
            writer.WriteString(_name);
            return writer.CreateMessage();
        }

        static MessageBuffer HostRegistered(Connection c)
        {
            using MessageWriter writer = c.GetMessageWriter();
            writer.WriteMethodCallHeader(WatcherService, WatcherPath, PropertiesInterface, "Get", "ss");
            writer.WriteString(WatcherService);
            writer.WriteString("IsStatusNotifierHostRegistered");
            return writer.CreateMessage();
        }
    }

    // ---- What the app sets ----

    /// <summary>The icon, from an .ico or .png file, in the sizes desktops ask for.</summary>
    public void SetIcon(string file)
    {
        var sizes = new List<(int, byte[])>();
        try
        {
            using SKBitmap? source = SKBitmap.Decode(file);
            if (source is not null)
            {
                foreach (int size in (int[])[22, 24, 32, 48])
                {
                    sizes.Add((size, Pixels(source, size)));
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not read the icon {file}", e);
        }
        _icon = sizes;
        Emit(ItemPath, ItemInterface, "NewIcon");
    }

    /// <summary>The picture at a size, as the bytes desktops want: alpha, red, green, blue for each pixel.</summary>
    private static byte[] Pixels(SKBitmap source, int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var scaled = new SKBitmap(info);
        source.ScalePixels(scaled, new SKSamplingOptions(SKCubicResampler.Mitchell));
        ReadOnlySpan<byte> rgba = scaled.GetPixelSpan();
        byte[] argb = new byte[size * size * 4];
        for (int i = 0; i < argb.Length; i += 4)
        {
            argb[i] = rgba[i + 3];
            argb[i + 1] = rgba[i];
            argb[i + 2] = rgba[i + 1];
            argb[i + 3] = rgba[i + 2];
        }
        return argb;
    }

    public void SetToolTip(string text)
    {
        _tooltip = text;
        Emit(ItemPath, ItemInterface, "NewToolTip");
    }

    /// <summary>The name of the app, which a panel shows where it lists its icons.</summary>
    public void SetTitle(string text)
    {
        _title = text;
        Emit(ItemPath, ItemInterface, "NewTitle");
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        _menu = items;
        _menuRevision++;
        if (_bus is { } bus && !_disposed)
        {
            using MessageWriter writer = bus.GetMessageWriter();
            writer.WriteSignalHeader(null, MenuPath, MenuInterface, "LayoutUpdated", "ui");
            writer.WriteUInt32(_menuRevision);
            writer.WriteInt32(0);
            bus.TrySendMessage(writer.CreateMessage());
        }
    }

    private void Emit(string path, string @interface, string signal)
    {
        if (_bus is { } bus && !_disposed)
        {
            using MessageWriter writer = bus.GetMessageWriter();
            writer.WriteSignalHeader(null, path, @interface, signal);
            bus.TrySendMessage(writer.CreateMessage());
        }
    }

    // ---- What the desktop asks of the icon ----

    private void HandleItem(MethodContext context)
    {
        Message request = context.Request;
        string? @interface = request.InterfaceAsString;
        string? member = request.MemberAsString;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([ItemXml]);
        }
        else if (@interface == PropertiesInterface && member == "Get")
        {
            Reader reader = request.GetBodyReader();
            reader.ReadString();
            string name = reader.ReadString();
            if (ItemProperties.Contains(name))
            {
                context.Reply(Build(context.CreateReplyWriter("v"), (ref MessageWriter writer) => WriteItemProperty(ref writer, name)));
            }
            else
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
            }
        }
        else if (@interface == PropertiesInterface && member == "GetAll")
        {
            context.Reply(Build(context.CreateReplyWriter("a{sv}"), (ref MessageWriter writer) =>
            {
                ArrayStart all = writer.WriteDictionaryStart();
                foreach (string name in ItemProperties)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WriteItemProperty(ref writer, name);
                }
                writer.WriteDictionaryEnd(all);
            }));
        }
        else if (@interface is null or ItemInterface)
        {
            switch (member)
            {
                case "Activate" or "SecondaryActivate":
                    ReplyEmpty(context);
                    Activated?.Invoke();
                    break;
                // The desktop shows the menu itself, from MenuBar.
                case "ContextMenu" or "Scroll" or "ProvideXdgActivationToken":
                    ReplyEmpty(context);
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }
        else
        {
            context.ReplyUnknownMethodError();
        }
    }

    private static readonly string[] ItemProperties =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName", "OverlayIconPixmap",
        "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName", "ToolTip", "ItemIsMenu", "Menu", "IconThemePath",
    ];

    /// <summary>Writes one of <see cref="ItemProperties"/> as a variant.</summary>
    private void WriteItemProperty(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "Category":
                writer.WriteVariantString("Communications");
                break;
            case "Id":
                writer.WriteVariantString("TwoTicks");
                break;
            case "Title":
                writer.WriteVariantString(_title);
                break;
            case "Status":
                writer.WriteVariantString("Active");
                break;
            case "WindowId":
                writer.WriteVariantInt32(0);
                break;
            case "IconPixmap":
                writer.WriteSignature("a(iiay)");
                WritePixmaps(ref writer, _icon);
                break;
            case "OverlayIconPixmap" or "AttentionIconPixmap":
                writer.WriteSignature("a(iiay)");
                WritePixmaps(ref writer, []);
                break;
            case "ToolTip":
                writer.WriteSignature("(sa(iiay)ss)");
                writer.WriteStructureStart();
                writer.WriteString("");
                WritePixmaps(ref writer, []);
                writer.WriteString(_tooltip);
                writer.WriteString("");
                break;
            case "ItemIsMenu":
                writer.WriteVariantBool(false);
                break;
            case "Menu":
                writer.WriteVariantObjectPath(MenuPath);
                break;
            default:
                // The names of icons and of the folder to find them in: the icon comes as pixels.
                writer.WriteVariantString("");
                break;
        }
    }

    private static void WritePixmaps(ref MessageWriter writer, List<(int Size, byte[] Pixels)> pixmaps)
    {
        ArrayStart array = writer.WriteArrayStart(DBusType.Struct);
        foreach ((int size, byte[] pixels) in pixmaps)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(size);
            writer.WriteInt32(size);
            writer.WriteArray(pixels);
        }
        writer.WriteArrayEnd(array);
    }

    // ---- What the desktop asks of the menu ----

    private void HandleMenu(MethodContext context)
    {
        Message request = context.Request;
        string? @interface = request.InterfaceAsString;
        string? member = request.MemberAsString;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([MenuXml]);
            return;
        }
        if (@interface == PropertiesInterface)
        {
            HandleMenuProperties(context, member);
            return;
        }
        Reader reader = request.GetBodyReader();
        switch (member)
        {
            case "GetLayout":
            {
                context.Reply(Build(context.CreateReplyWriter("u(ia{sv}av)"), (ref MessageWriter writer) =>
                {
                    writer.WriteUInt32(_menuRevision);
                    // The menu itself, with its entries inside it.
                    writer.WriteStructureStart();
                    writer.WriteInt32(0);
                    ArrayStart properties = writer.WriteDictionaryStart();
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("children-display");
                    writer.WriteVariantString("submenu");
                    writer.WriteDictionaryEnd(properties);
                    ArrayStart children = writer.WriteArrayStart(DBusType.Variant);
                    foreach (TrayMenuItem item in _menu)
                    {
                        writer.WriteSignature("(ia{sv}av)");
                        writer.WriteStructureStart();
                        writer.WriteInt32(item.Id);
                        WriteMenuProperties(ref writer, item);
                        writer.WriteArrayEnd(writer.WriteArrayStart(DBusType.Variant));
                    }
                    writer.WriteArrayEnd(children);
                }));
                break;
            }
            case "GetGroupProperties":
            {
                int[] ids = reader.ReadArrayOfInt32();
                context.Reply(Build(context.CreateReplyWriter("a(ia{sv})"), (ref MessageWriter writer) =>
                {
                    ArrayStart array = writer.WriteArrayStart(DBusType.Struct);
                    foreach (TrayMenuItem item in _menu.Where(i => ids.Length == 0 || ids.Contains(i.Id)))
                    {
                        writer.WriteStructureStart();
                        writer.WriteInt32(item.Id);
                        WriteMenuProperties(ref writer, item);
                    }
                    writer.WriteArrayEnd(array);
                }));
                break;
            }
            case "GetProperty":
            {
                int id = reader.ReadInt32();
                string name = reader.ReadString();
                TrayMenuItem? item = _menu.FirstOrDefault(i => i.Id == id);
                using MessageWriter writer = context.CreateReplyWriter("v");
                switch (name)
                {
                    case "label":
                        writer.WriteVariantString(item?.Label ?? "");
                        break;
                    case "type":
                        writer.WriteVariantString(item is { IsSeparator: true } ? "separator" : "standard");
                        break;
                    default:
                        writer.WriteVariantBool(true);
                        break;
                }
                context.Reply(writer.CreateMessage());
                break;
            }
            case "Event":
            {
                int id = reader.ReadInt32();
                string what = reader.ReadString();
                ReplyEmpty(context);
                if (what == "clicked")
                {
                    Click(id);
                }
                break;
            }
            case "EventGroup":
            {
                var clicked = new List<int>();
                ArrayEnd end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    int id = reader.ReadInt32();
                    string what = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    if (what == "clicked")
                    {
                        clicked.Add(id);
                    }
                }
                using (MessageWriter writer = context.CreateReplyWriter("ai"))
                {
                    writer.WriteArray(Array.Empty<int>());
                    context.Reply(writer.CreateMessage());
                }
                clicked.ForEach(Click);
                break;
            }
            case "AboutToShow":
            {
                using MessageWriter writer = context.CreateReplyWriter("b");
                writer.WriteBool(false);
                context.Reply(writer.CreateMessage());
                break;
            }
            case "AboutToShowGroup":
            {
                using MessageWriter writer = context.CreateReplyWriter("aiai");
                writer.WriteArray(Array.Empty<int>());
                writer.WriteArray(Array.Empty<int>());
                context.Reply(writer.CreateMessage());
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void HandleMenuProperties(MethodContext context, string? member)
    {
        if (member == "Get")
        {
            Reader reader = context.Request.GetBodyReader();
            reader.ReadString();
            string name = reader.ReadString();
            context.Reply(Build(context.CreateReplyWriter("v"), (ref MessageWriter writer) => WriteMenuBarProperty(ref writer, name)));
        }
        else if (member == "GetAll")
        {
            context.Reply(Build(context.CreateReplyWriter("a{sv}"), (ref MessageWriter writer) =>
            {
                ArrayStart all = writer.WriteDictionaryStart();
                foreach (string name in (string[])["Version", "TextDirection", "Status", "IconThemePath"])
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WriteMenuBarProperty(ref writer, name);
                }
                writer.WriteDictionaryEnd(all);
            }));
        }
        else
        {
            context.ReplyUnknownMethodError();
        }
    }

    private static void WriteMenuBarProperty(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "Version":
                writer.WriteVariantUInt32(3);
                break;
            case "TextDirection":
                writer.WriteVariantString("ltr");
                break;
            case "IconThemePath":
                writer.WriteSignature("as");
                writer.WriteArray(Array.Empty<string>());
                break;
            default:
                writer.WriteVariantString("normal");
                break;
        }
    }

    private static void WriteMenuProperties(ref MessageWriter writer, TrayMenuItem item)
    {
        ArrayStart properties = writer.WriteDictionaryStart();
        if (item.IsSeparator)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("type");
            writer.WriteVariantString("separator");
        }
        else
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("label");
            // An underscore marks the next letter as the entry's key; a real one is doubled.
            writer.WriteVariantString(item.Label.Replace("_", "__", StringComparison.Ordinal));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("enabled");
            writer.WriteVariantBool(true);
        }
        writer.WriteDictionaryEntryStart();
        writer.WriteString("visible");
        writer.WriteVariantBool(true);
        writer.WriteDictionaryEnd(properties);
    }

    private void Click(int id) => _menu.FirstOrDefault(i => i.Id == id)?.Clicked?.Invoke();

    private delegate void WriteBody(ref MessageWriter writer);

    /// <summary>A message with what write puts into it. The writer is passed on by reference, which a using variable cannot be.</summary>
    private static MessageBuffer Build(MessageWriter writer, WriteBody write)
    {
        try
        {
            write(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using MessageWriter writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    public void Dispose()
    {
        _disposed = true;
        IsShown = false;
        if (_bus is { } bus)
        {
            try
            {
                bus.RemoveMethodHandlers([ItemPath, MenuPath]);
                // Giving the name back takes the icon out of the notification area.
                bus.ReleaseNameAsync(_name).Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception)
            {
                // The bus is gone with the session.
            }
        }
    }

    /// <summary>An object on the bus whose calls go to a method.</summary>
    private sealed class Handler(string path, Action<MethodContext> handle) : IPathMethodHandler
    {
        public string Path => path;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                handle(context);
            }
            catch (Exception e)
            {
                Log.Error($"A call from the desktop to {path} failed", e);
                if (!context.ReplySent && !context.NoReplyExpected)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.Failed", e.Message);
                }
            }
            return default;
        }
    }

    private static readonly ReadOnlyMemory<byte> ItemXml = Encoding.UTF8.GetBytes("""
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewTitle"/>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewToolTip"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>
        """);

    private static readonly ReadOnlyMemory<byte> MenuXml = Encoding.UTF8.GetBytes("""
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg name="parentId" type="i" direction="in"/><arg name="recursionDepth" type="i" direction="in"/><arg name="propertyNames" type="as" direction="in"/>
            <arg name="revision" type="u" direction="out"/><arg name="layout" type="(ia{sv}av)" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg name="ids" type="ai" direction="in"/><arg name="propertyNames" type="as" direction="in"/>
            <arg name="properties" type="a(ia{sv})" direction="out"/>
          </method>
          <method name="GetProperty"><arg name="id" type="i" direction="in"/><arg name="name" type="s" direction="in"/><arg name="value" type="v" direction="out"/></method>
          <method name="Event">
            <arg name="id" type="i" direction="in"/><arg name="eventId" type="s" direction="in"/><arg name="data" type="v" direction="in"/><arg name="timestamp" type="u" direction="in"/>
          </method>
          <method name="EventGroup"><arg name="events" type="a(isvu)" direction="in"/><arg name="idErrors" type="ai" direction="out"/></method>
          <method name="AboutToShow"><arg name="id" type="i" direction="in"/><arg name="needUpdate" type="b" direction="out"/></method>
          <method name="AboutToShowGroup">
            <arg name="ids" type="ai" direction="in"/><arg name="updatesNeeded" type="ai" direction="out"/><arg name="idErrors" type="ai" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated"><arg type="a(ia{sv})"/><arg type="a(ias)"/></signal>
          <signal name="LayoutUpdated"><arg name="revision" type="u"/><arg name="parent" type="i"/></signal>
          <signal name="ItemActivationRequested"><arg name="id" type="i"/><arg name="timestamp" type="u"/></signal>
        </interface>
        """);
}
