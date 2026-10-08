using System.Buffers.Binary;
using System.Text;
using WinWhatsApp.Core;

namespace WinWhatsApp.Core.Tests;

public class MediaFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("winwhatsapp-media").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Save(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Box(string name, params byte[][] parts)
    {
        byte[] content = parts.SelectMany(p => p).ToArray();
        var box = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(name, box.AsSpan(4));
        content.CopyTo(box, 8);
        return box;
    }

    private static byte[] Big(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>A movie header: ticks per second and the length in ticks.</summary>
    private static byte[] MovieHeader(uint scale, uint length) =>
        Box("mvhd", new byte[12], Big(scale), Big(length), new byte[80]);

    /// <summary>A track header with the size of its picture, turned a quarter or not.</summary>
    private static byte[] TrackHeader(uint width, uint height, bool turned)
    {
        byte[] one = Big(0x00010000);
        byte[] matrix = turned
            ? [.. new byte[4], .. one, .. new byte[4], .. Big(0xFFFF0000), .. new byte[16], .. Big(0x40000000)]
            : [.. one, .. new byte[12], .. one, .. new byte[12], .. Big(0x40000000)];
        return Box("tkhd", new byte[40], matrix, Big(width << 16), Big(height << 16));
    }

    [Fact]
    public void ReadsTheLengthAndSizeOfAVideo()
    {
        byte[] movie = Box("moov",
            MovieHeader(600, 600 * 24),
            Box("trak", TrackHeader(0, 0, turned: false)),
            Box("trak", TrackHeader(1280, 720, turned: false)));
        string path = Save("video.mp4", [.. Box("ftyp", Encoding.ASCII.GetBytes("isom")), .. Box("mdat", new byte[100]), .. movie]);

        Assert.Equal(new MediaFacts(24, 1280, 720), MediaFile.Read(path));
    }

    [Fact]
    public void AVideoFilmedUprightIsTallerThanWide()
    {
        byte[] movie = Box("moov", MovieHeader(1000, 5500), Box("trak", TrackHeader(1920, 1080, turned: true)));
        string path = Save("upright.mp4", [.. Box("ftyp", Encoding.ASCII.GetBytes("isom")), .. movie]);

        Assert.Equal(new MediaFacts(5.5, 1080, 1920), MediaFile.Read(path));
    }

    private static byte[] OggPage(long samples, byte[] packet)
    {
        var page = new byte[27 + 1 + packet.Length];
        Encoding.ASCII.GetBytes("OggS", page);
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), samples);
        page[26] = 1;
        page[27] = (byte)packet.Length;
        packet.CopyTo(page, 28);
        return page;
    }

    [Fact]
    public void ReadsTheLengthOfAVoiceMessage()
    {
        var head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead", head);
        head[8] = 1;
        head[9] = 1;
        // The run-in before the sound, which does not count.
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        string path = Save("voice.ogg", [.. OggPage(0, head), .. OggPage(0, new byte[40]), .. OggPage(48000 * 18 + 312, new byte[200])]);

        Assert.Equal(18, MediaFile.Read(path).Seconds, 3);
    }

    [Fact]
    public void ReadsTheLengthOfAWavFile()
    {
        var file = new byte[44 + 32000];
        Encoding.ASCII.GetBytes("RIFF", file);
        Encoding.ASCII.GetBytes("WAVEfmt ", file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        // 16000 bytes a second.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 16000);
        Encoding.ASCII.GetBytes("data", file.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), 32000);

        Assert.Equal(2, MediaFile.Read(Save("sound.wav", file)).Seconds, 3);
    }

    [Fact]
    public void CountsTheFramesOfAnAmrRecording()
    {
        // Fifty frames of the densest kind, 31 bytes after the byte that says so: one second.
        byte[] frame = [7 << 3, .. new byte[31]];
        byte[] file = [.. Encoding.ASCII.GetBytes("#!AMR\n"), .. Enumerable.Repeat(frame, 50).SelectMany(f => f)];

        Assert.Equal(1, MediaFile.Read(Save("note.amr", file)).Seconds, 3);
    }

    [Fact]
    public void TakesAnMp3sLengthFromItsSizeWhenItDoesNotSay()
    {
        // MPEG 1 layer III at 128 kbit/s and 44.1 kHz: 16000 bytes a second.
        byte[] file = [.. Encoding.ASCII.GetBytes("ID3"), 3, 0, 0, 0, 0, 0, 10, .. new byte[10], 0xFF, 0xFB, 0x90, 0x00, .. new byte[48000 - 4]];

        Assert.Equal(3, MediaFile.Read(Save("song.mp3", file)).Seconds, 3);
    }

    [Fact]
    public void AFileItCannotReadHasNoFacts()
    {
        Assert.Equal(default, MediaFile.Read(Save("text.mp4", Encoding.ASCII.GetBytes("This is no video at all."))));
        Assert.Equal(default, MediaFile.Read(Save("short.ogg", [1, 2, 3])));
        Assert.Equal(default, MediaFile.Read(Path.Combine(_folder, "missing.mp4")));
    }
}
