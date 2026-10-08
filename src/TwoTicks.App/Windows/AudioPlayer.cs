namespace TwoTicks.App;

internal static partial class AudioPlayer
{
    /// <summary>Windows plays what WhatsApp sends as it is.</summary>
    private static partial Task<string> PlayableAsync(string path) => Task.FromResult(path);
}
