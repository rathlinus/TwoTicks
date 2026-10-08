using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Concentus;
using Concentus.Oggfile;

namespace WinWhatsApp.App;

internal static partial class AudioPlayer
{
    private static readonly string s_copies = Path.Combine(Path.GetTempPath(), "winwhatsapp-audio");

    /// <summary>
    /// Voice messages are Opus in an Ogg file. VLC plays that on Linux. macOS
    /// does not, so there the app decodes the recording itself and hands the
    /// player a WAV file, kept for as long as the app runs.
    /// </summary>
    private static partial Task<string> PlayableAsync(string path)
    {
        if (!NeedsDecoding(path))
        {
            return Task.FromResult(path);
        }
        return Task.Run(() =>
        {
            var file = new FileInfo(path);
            string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..32];
            string copy = Path.Combine(s_copies, name + ".wav");
            if (!File.Exists(copy))
            {
                Directory.CreateDirectory(s_copies);
                string unfinished = copy + ".tmp";
                DecodeOpus(path, unfinished);
                File.Move(unfinished, copy, overwrite: true);
            }
            return copy;
        });
    }

    /// <summary>
    /// Whether the system's player needs the recording decoded: an Ogg file on
    /// a Mac, whatever it is named. WINWHATSAPP_DECODE_OPUS=1 asks for the same
    /// on Linux, to try this without a Mac.
    /// </summary>
    private static bool NeedsDecoding(string path)
    {
        if (!OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("WINWHATSAPP_DECODE_OPUS") != "1")
        {
            return false;
        }
        try
        {
            using FileStream file = File.OpenRead(path);
            Span<byte> start = stackalloc byte[4];
            return file.Read(start) == 4 && start.SequenceEqual("OggS"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Removes the copies an earlier run left behind.</summary>
    public static void ClearCopies()
    {
        try
        {
            if (Directory.Exists(s_copies))
            {
                Directory.Delete(s_copies, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes an Opus recording as a WAV file: one channel, 16 bits, 48 kHz.</summary>
    private static void DecodeOpus(string source, string target)
    {
        const int rate = 48000;
        using FileStream input = File.OpenRead(source);
        using FileStream output = File.Create(target);
        // The header first, with sizes filled in once they are known.
        var header = new byte[44];
        output.Write(header);

        var decoder = OpusCodecFactory.CreateDecoder(rate, 1);
        var ogg = new OpusOggReadStream(decoder, input);
        long bytes = 0;
        while (ogg.HasNextPacket)
        {
            short[]? samples = ogg.DecodeNextPacket();
            if (samples is null || samples.Length == 0)
            {
                continue;
            }
            var chunk = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 2), samples[i]);
            }
            output.Write(chunk);
            bytes += chunk.Length;
        }
        if (bytes == 0)
        {
            throw new InvalidDataException("No sound in " + source);
        }

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + bytes));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)bytes);
        output.Position = 0;
        output.Write(header);
    }
}
