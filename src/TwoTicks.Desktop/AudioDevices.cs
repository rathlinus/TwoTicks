using System.Diagnostics;
using System.Text.Json;

namespace TwoTicks.App;

/// <summary>
/// The microphones and speakers of the computer, by the names the browser
/// that runs the calling engine knows them by: the settings store a name, and
/// the engine's page looks for the device of that name.
/// </summary>
/// <remarks>
/// Asked of the system's own tools: pactl on Linux, which PulseAudio and
/// PipeWire both answer, and system_profiler on macOS. Where the tool is
/// missing the list is empty and the settings only offer the system's default.
/// </remarks>
internal static class AudioDevices
{
    public static async Task<IReadOnlyList<string>> NamesAsync(bool microphones)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return FromPulse(await RunAsync("pactl", "-f", "json", "list", microphones ? "sources" : "sinks"), microphones);
            }
            if (OperatingSystem.IsMacOS())
            {
                return FromProfiler(await RunAsync("system_profiler", "SPAudioDataType", "-json"), microphones);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Log.Info("Could not list the sound devices: " + e.Message);
        }
        return [];
    }

    /// <summary>What a program prints, or an empty text when it does not run or takes too long.</summary>
    private static async Task<string> RunAsync(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException(program + " did not start");
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            _ = process.StandardError.ReadToEndAsync(patience.Token);
            string output = await process.StandardOutput.ReadToEndAsync(patience.Token);
            await process.WaitForExitAsync(patience.Token);
            return output;
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
    }

    /// <summary>The devices in pactl's list, without the ones that only listen to a speaker.</summary>
    private static List<string> FromPulse(string json, bool microphones)
    {
        var names = new List<string>();
        if (json.Length == 0)
        {
            return names;
        }
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement device in document.RootElement.EnumerateArray())
        {
            bool monitor = device.TryGetProperty("properties", out JsonElement properties)
                && properties.TryGetProperty("device.class", out JsonElement kind) && kind.GetString() == "monitor";
            if (!(microphones && monitor) && device.TryGetProperty("description", out JsonElement description) && description.GetString() is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }
        return names;
    }

    /// <summary>The devices in system_profiler's report that take sound in, or give it out.</summary>
    private static List<string> FromProfiler(string json, bool microphones)
    {
        var names = new List<string>();
        if (json.Length == 0)
        {
            return names;
        }
        using JsonDocument document = JsonDocument.Parse(json);
        string channels = microphones ? "coreaudio_device_input" : "coreaudio_device_output";
        foreach (JsonElement group in document.RootElement.GetProperty("SPAudioDataType").EnumerateArray())
        {
            if (!group.TryGetProperty("_items", out JsonElement devices))
            {
                continue;
            }
            foreach (JsonElement device in devices.EnumerateArray())
            {
                if (device.TryGetProperty(channels, out _) && device.TryGetProperty("_name", out JsonElement name) && name.GetString() is { Length: > 0 } text)
                {
                    names.Add(text);
                }
            }
        }
        return names;
    }
}
