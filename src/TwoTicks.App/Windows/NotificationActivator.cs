using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using TwoTicks.Core;

namespace TwoTicks.App;

/// <summary>
/// Where Windows sends the clicks on the app's notifications, also those in
/// the notification centre and those after the app quit.
/// </summary>
/// <remarks>
/// An app that is not installed as a package gets the clicks of notifications
/// in the notification centre only through a COM class it names in its
/// AppUserModelId key. Windows calls the running app's class, or starts the
/// program with <see cref="Switch"/> when it does not run.
/// </remarks>
internal static class NotificationActivator
{
    /// <summary>On the command line when Windows starts the app for a click.</summary>
    public const string Switch = "-ToastActivated";

    private static readonly Guid s_defaultClass = new("6c2f8e4b-27a1-4f7d-9b0e-3d5a1c9e7f42");

    /// <summary>The class of this copy. A copy with its own data folder has its own, so it never takes the clicks of the usual one.</summary>
    public static Guid ClassId { get; } = AppPaths.IsDefaultDataFolder ? s_defaultClass : new Guid(Hash(AppPaths.DataFolder).AsSpan(0, 16));

    /// <summary>A short name of the data folder, the same at every start.</summary>
    public static string FolderTag => Convert.ToHexString(Hash(AppPaths.DataFolder).AsSpan(0, 4));

    private static byte[] Hash(string folder) => SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));

    private static Action<string, IReadOnlyDictionary<string, string>>? s_activated;
    private static uint s_cookie;

    /// <summary>
    /// Writes the class to the registry under the notifications' key and
    /// takes the clicks in this process from now on. activated gets the
    /// notification's arguments and what was typed into it, on a background thread.
    /// </summary>
    public static void Register(RegistryKey appKey, Action<string, IReadOnlyDictionary<string, string>> activated)
    {
        s_activated = activated;
        string command = $"\"{Environment.ProcessPath}\" {Switch}";
        if (!AppPaths.IsDefaultDataFolder)
        {
            // Started by Windows, a copy has no TWOTICKS_DATA and would take the usual folder.
            command += $" {Program.DataSwitch} \"{AppPaths.DataFolder}\"";
        }
        using (RegistryKey server = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{{{ClassId}}}\LocalServer32"))
        {
            server.SetValue("", command);
        }
        appKey.SetValue("CustomActivator", $"{{{ClassId}}}");

        if (s_cookie != 0)
        {
            return;
        }
        // From a thread of the multithreaded apartment: the calls then come on
        // threads of their own and never wait for the window's thread.
        Task.Run(() =>
        {
            Guid id = ClassId;
            int result = CoRegisterClassObject(ref id, new Factory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out s_cookie);
            Marshal.ThrowExceptionForHR(result);
        }).Wait();
    }

    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid classId, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool doLock);
    }

    [ComImport]
    [Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] InputData[] data, uint count);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InputData
    {
        public string Key;
        public string Value;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Factory : IClassFactory
    {
        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private static readonly Guid s_unknown = new("00000000-0000-0000-C000-000000000046");

        public int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero)
            {
                return CLASS_E_NOAGGREGATION;
            }
            var callback = new Callback();
            if (interfaceId == typeof(INotificationActivationCallback).GUID)
            {
                instance = Marshal.GetComInterfaceForObject<Callback, INotificationActivationCallback>(callback);
                return 0;
            }
            if (interfaceId == s_unknown)
            {
                instance = Marshal.GetIUnknownForObject(callback);
                return 0;
            }
            return E_NOINTERFACE;
        }

        public int LockServer(bool doLock) => 0;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Callback : INotificationActivationCallback
    {
        public void Activate(string appUserModelId, string invokedArgs, InputData[] data, uint count)
        {
            try
            {
                var input = new Dictionary<string, string>();
                for (int i = 0; i < count && i < (data?.Length ?? 0); i++)
                {
                    input[data![i].Key] = data[i].Value;
                }
                s_activated?.Invoke(invokedArgs ?? "", input);
            }
            catch (Exception e)
            {
                Log.Error("Failed to handle a notification click", e);
            }
        }
    }
}
