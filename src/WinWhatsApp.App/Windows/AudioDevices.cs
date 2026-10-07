using Windows.Devices.Enumeration;

namespace WinWhatsApp.App;

/// <summary>
/// The microphones and speakers Windows has, by name: the settings store a
/// name, and the calling engine's page looks for the device of that name.
/// </summary>
internal static class AudioDevices
{
    public static async Task<IReadOnlyList<string>> NamesAsync(bool microphones)
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(microphones ? DeviceClass.AudioCapture : DeviceClass.AudioRender);
        return devices.Where(d => d.IsEnabled).Select(d => d.Name).ToList();
    }
}
