using System.Buffers.Binary;
using System.Text;

namespace WinWhatsApp.Core;

/// <summary>How long a recording or video is and, for a video, how large its picture is.</summary>
public readonly record struct MediaFacts(double Seconds, int Width, int Height);

/// <summary>
/// Reads what a sound or video file says about itself: its length and the
/// size of its picture. WhatsApp shows both before the file is played, so
/// they go along when one is sent.
/// </summary>
/// <remarks>
/// Windows answers this for the Windows app. Elsewhere the app reads the
/// files itself, which for these few facts takes no decoder: MP4 and its
/// relatives (m4a, mov, 3gp), Ogg with Opus or Vorbis, WAV, MP3, AAC and AMR.
/// A file it cannot make sense of has no facts, and nothing here throws for it.
/// </remarks>
public static class MediaFile
{
    public static MediaFacts Read(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            var start = new byte[12];
            int read = file.Read(start, 0, start.Length);
            file.Position = 0;
            if (read < 12)
            {
                return default;
            }
            if (Tag(start, 4) is "ftyp" or "moov" or "mdat" or "free" or "wide" or "skip")
            {
                return ReadMp4(file);
            }
            return Tag(start, 0) switch
            {
                "OggS" => new MediaFacts(OggSeconds(file), 0, 0),
                "RIFF" when Tag(start, 8) == "WAVE" => new MediaFacts(WavSeconds(file), 0, 0),
                "#!AM" => new MediaFacts(AmrSeconds(file), 0, 0),
                _ => new MediaFacts(Mp3OrAacSeconds(file), 0, 0),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return default;
        }
    }

    private static string Tag(ReadOnlySpan<byte> bytes, int at) => Encoding.ASCII.GetString(bytes.Slice(at, 4));

    private static bool Fill(Stream file, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = file.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }
            total += read;
        }
        return true;
    }

    // ---- MP4 ----
    //
    // A file of boxes, each with its size and a name of four letters. The
    // movie's header (mvhd) has the length; each track's header (tkhd) has the
    // size of its picture, which is zero for sound, and how it is turned.

    private static MediaFacts ReadMp4(FileStream file)
    {
        double seconds = 0;
        int width = 0, height = 0;
        foreach ((string name, long start, long end) in Boxes(file, 0, file.Length))
        {
            if (name != "moov")
            {
                continue;
            }
            foreach ((string inner, long innerStart, long innerEnd) in Boxes(file, start, end))
            {
                if (inner == "mvhd")
                {
                    seconds = MovieSeconds(file, innerStart);
                }
                else if (inner == "trak" && width == 0)
                {
                    foreach ((string part, long partStart, long _) in Boxes(file, innerStart, innerEnd))
                    {
                        if (part == "tkhd")
                        {
                            (width, height) = TrackSize(file, partStart);
                        }
                    }
                }
            }
        }
        return new MediaFacts(seconds, width, height);
    }

    /// <summary>The boxes between two places, each with where its content starts and ends.</summary>
    private static List<(string Name, long Start, long End)> Boxes(FileStream file, long from, long to)
    {
        var boxes = new List<(string, long, long)>();
        var head = new byte[16];
        long at = from;
        while (at + 8 <= to)
        {
            file.Position = at;
            if (!Fill(file, head.AsSpan(0, 8)))
            {
                break;
            }
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            long content = at + 8;
            if (size == 1)
            {
                // A box too large for 32 bits has its size after its name.
                if (!Fill(file, head.AsSpan(8, 8)))
                {
                    break;
                }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
                content = at + 16;
            }
            else if (size == 0)
            {
                size = to - at;
            }
            if (size < content - at || at + size > to)
            {
                break;
            }
            boxes.Add((Tag(head, 4), content, at + size));
            at += size;
        }
        return boxes;
    }

    private static double MovieSeconds(FileStream file, long at)
    {
        var data = new byte[32];
        file.Position = at;
        if (!Fill(file, data))
        {
            return 0;
        }
        // Version 1 keeps its times in 64 bits.
        bool wide = data[0] == 1;
        uint scale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(wide ? 20 : 12));
        ulong length = wide ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        return scale == 0 ? 0 : (double)length / scale;
    }

    private static (int Width, int Height) TrackSize(FileStream file, long at)
    {
        file.Position = at;
        int version = file.ReadByte();
        // Version 1 keeps its times in 64 bits, which moves the rest back.
        int matrix = version == 1 ? 52 : 40;
        var data = new byte[matrix + 44];
        file.Position = at;
        if (version < 0 || !Fill(file, data))
        {
            return (0, 0);
        }
        // Fixed point numbers, 16 bits before the point.
        int width = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(matrix + 36)) >> 16);
        int height = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(matrix + 40)) >> 16);
        // A video filmed upright is stored on its side with a quarter turn:
        // the first number of the matrix is then zero and the second is not.
        int a = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrix));
        int b = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrix + 4));
        return a == 0 && b != 0 ? (height, width) : (width, height);
    }

    // ---- Ogg ----
    //
    // Pages that each say how many samples have passed when they end. The
    // first page names what is inside and the last one has the total.

    private static double OggSeconds(FileStream file)
    {
        var first = new byte[64];
        if (!Fill(file, first))
        {
            return 0;
        }
        // The first packet starts after the page's header and its list of segments.
        int packet = 27 + first[26];
        if (packet + 20 > first.Length)
        {
            return 0;
        }
        double rate;
        long skip = 0;
        if (Encoding.ASCII.GetString(first, packet, 8) == "OpusHead")
        {
            // Opus counts at 48 kHz whatever it was recorded at, after a run-in.
            rate = 48000;
            skip = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(packet + 10));
        }
        else if (Encoding.ASCII.GetString(first, packet + 1, 6) == "vorbis")
        {
            rate = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(packet + 12));
        }
        else
        {
            return 0;
        }

        int tailSize = (int)Math.Min(file.Length, 128 * 1024);
        var tail = new byte[tailSize];
        file.Position = file.Length - tailSize;
        if (!Fill(file, tail))
        {
            return 0;
        }
        for (int i = tailSize - 14; i >= 0; i--)
        {
            if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S')
            {
                long samples = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6));
                return rate > 0 && samples > skip ? (samples - skip) / rate : 0;
            }
        }
        return 0;
    }

    // ---- WAV ----

    private static double WavSeconds(FileStream file)
    {
        var head = new byte[8];
        uint bytesPerSecond = 0;
        file.Position = 12;
        while (Fill(file, head))
        {
            string name = Tag(head, 0);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            if (name == "fmt ")
            {
                var format = new byte[16];
                if (!Fill(file, format))
                {
                    return 0;
                }
                bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                file.Position += size - 16 + (size & 1);
            }
            else if (name == "data")
            {
                // A recording still being written says zero or all ones: then the rest of the file is the sound.
                long sound = size is 0 or uint.MaxValue ? file.Length - file.Position : size;
                return bytesPerSecond == 0 ? 0 : (double)sound / bytesPerSecond;
            }
            else
            {
                file.Position += size + (size & 1);
            }
        }
        return 0;
    }

    // ---- AMR ----
    //
    // Frames of 20 milliseconds, each starting with a byte that says how it is packed and so how long it is.

    private static readonly int[] s_amrFrame = [12, 13, 15, 17, 19, 20, 26, 31, 5, 0, 0, 0, 0, 0, 0, 0];
    private static readonly int[] s_amrWideFrame = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5, 0, 0, 0, 0, 0, 0];

    private static double AmrSeconds(FileStream file)
    {
        var magic = new byte[9];
        if (!Fill(file, magic))
        {
            return 0;
        }
        bool wide = Encoding.ASCII.GetString(magic) == "#!AMR-WB\n";
        file.Position = wide ? 9 : 6;
        int[] sizes = wide ? s_amrWideFrame : s_amrFrame;
        long frames = 0;
        int head;
        while ((head = file.ReadByte()) >= 0)
        {
            file.Position += sizes[(head >> 3) & 15];
            frames++;
        }
        return frames * 0.02;
    }

    // ---- MP3 and AAC ----
    //
    // Both are runs of frames that start with eleven or twelve set bits. An
    // AAC file is short enough to count them. An MP3 says how many there are
    // in its first frame, when its encoder was kind; otherwise the size of
    // the file and the bit rate of the first frame have to do.

    private static readonly int[] s_mp3Rates = [44100, 48000, 32000];
    private static readonly int[] s_mp3BitsV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] s_mp3BitsV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] s_aacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private static double Mp3OrAacSeconds(FileStream file)
    {
        var head = new byte[10];
        if (!Fill(file, head))
        {
            return 0;
        }
        long start = 0;
        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            // A tag before the sound, its size in four bytes of seven bits.
            start = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F));
        }

        var window = new byte[4096];
        file.Position = start;
        int have = file.Read(window, 0, window.Length);
        for (int i = 0; i + 4 <= have; i++)
        {
            if (window[i] != 0xFF || (window[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }
            // Layer bits of zero mark AAC in its transport wrapping.
            if ((window[i + 1] & 0xF6) == 0xF0)
            {
                return AacSeconds(file, start + i);
            }
            if (((window[i + 1] >> 1) & 3) == 1)
            {
                return Mp3Seconds(file, start + i, window.AsSpan(i, have - i));
            }
        }
        return 0;
    }

    private static double AacSeconds(FileStream file, long at)
    {
        var head = new byte[7];
        long frames = 0;
        int rate = 0;
        while (at + 7 <= file.Length)
        {
            file.Position = at;
            if (!Fill(file, head) || head[0] != 0xFF || (head[1] & 0xF0) != 0xF0)
            {
                break;
            }
            int rateIndex = (head[2] >> 2) & 15;
            if (rateIndex >= s_aacRates.Length)
            {
                break;
            }
            rate = s_aacRates[rateIndex];
            int size = (head[3] & 3) << 11 | head[4] << 3 | head[5] >> 5;
            if (size < 7)
            {
                break;
            }
            frames++;
            at += size;
        }
        return rate == 0 ? 0 : frames * 1024.0 / rate;
    }

    private static double Mp3Seconds(FileStream file, long at, ReadOnlySpan<byte> frame)
    {
        bool versionOne = ((frame[1] >> 3) & 3) == 3;
        bool versionTwoFive = ((frame[1] >> 3) & 3) == 0;
        int bitIndex = frame[2] >> 4;
        int rateIndex = (frame[2] >> 2) & 3;
        if (bitIndex is 0 or 15 || rateIndex == 3)
        {
            return 0;
        }
        int rate = s_mp3Rates[rateIndex] / (versionOne ? 1 : versionTwoFive ? 4 : 2);
        int samples = versionOne ? 1152 : 576;
        bool mono = (frame[3] >> 6) == 3;

        // Where an encoder leaves the number of frames: after the side information of the first.
        int info = 4 + (versionOne ? (mono ? 17 : 32) : (mono ? 9 : 17));
        if (frame.Length >= info + 12 && Tag(frame, info) is "Xing" or "Info" && (frame[info + 7] & 1) != 0)
        {
            uint frames = BinaryPrimitives.ReadUInt32BigEndian(frame[(info + 8)..]);
            return (double)frames * samples / rate;
        }
        int bits = (versionOne ? s_mp3BitsV1 : s_mp3BitsV2)[bitIndex] * 1000;
        return (file.Length - at) * 8.0 / bits;
    }
}
