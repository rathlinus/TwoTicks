using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace TwoTicks.App;

/// <summary>
/// The microphones, speakers and cameras Windows has, by name: the settings
/// store a name, and the calling engine's page looks for the device of that name.
/// </summary>
internal static class AudioDevices
{
    public static async Task<IReadOnlyList<string>> NamesAsync(bool microphones)
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(microphones ? DeviceClass.AudioCapture : DeviceClass.AudioRender);
        return devices.Where(d => d.IsEnabled).Select(d => d.Name).ToList();
    }

    /// <summary>
    /// The cameras Windows lists, and the DirectShow ones besides: virtual
    /// cameras such as OBS's are often only those, and the browser sees them.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CameraNamesAsync()
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Where(d => d.IsEnabled).Select(d => d.Name).Concat(DirectShowCameraNames()).Distinct().ToList();
    }

    // DirectShow's category of video capture devices.
    private const string VideoInputCategory = @"CLSID\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\Instance";

    private static IEnumerable<string> DirectShowCameraNames()
    {
        using RegistryKey? category = Registry.ClassesRoot.OpenSubKey(VideoInputCategory);
        if (category is null)
        {
            return [];
        }
        var names = new List<string>();
        foreach (string id in category.GetSubKeyNames())
        {
            using RegistryKey? device = category.OpenSubKey(id);
            if (device?.GetValue("FriendlyName") is string { Length: > 0 } name)
            {
                names.Add(name);
            }
        }
        return names;
    }
}
