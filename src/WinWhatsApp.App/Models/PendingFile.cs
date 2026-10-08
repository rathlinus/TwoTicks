using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Models;

/// <summary>A file about to be sent: its tile in the row of files, and its caption.</summary>
public sealed class PendingFile : Observable
{
    private static readonly HashSet<string> s_pictureTypes = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic" };

    private ImageSource? _thumb;
    private bool _isSelected;
    private bool _isHovered;

    public PendingFile(string path)
    {
        Path = path;
        IsPicture = s_pictureTypes.Contains(System.IO.Path.GetExtension(path));
        IsVideo = MediaInfo.IsVideo(path);
        try
        {
            Size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
        }
        if (IsPicture)
        {
            _thumb = Images.FromFile(path, 112);
        }
        else if (IsVideo)
        {
            _ = LoadVideoFrameAsync();
        }
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public long Size { get; }
    public bool IsPicture { get; }
    public bool IsVideo { get; }

    /// <summary>Photos and videos can go as they are, or as files in their original quality.</summary>
    public bool IsMedia => IsPicture || IsVideo;

    /// <summary>What is written under it, kept while another file is selected.</summary>
    public string Caption { get; set; } = "";

    public string Info
    {
        get
        {
            string extension = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            return extension.Length is > 0 and <= 5 ? $"{extension} · {Formatting.FileSize(Size)}" : Formatting.FileSize(Size);
        }
    }

    public ImageSource? Thumb { get => _thumb; private set => Set(ref _thumb, value); }

    public string Glyph => IsVideo ? MessagePreview.VideoGlyph : MessagePreview.DocumentGlyph;
    public Visibility GlyphVisibility => IsPicture ? Visibility.Collapsed : Visibility.Visible;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) { OnPropertyChanged(nameof(SelectionOpacity)); } }
    }

    public double SelectionOpacity => _isSelected ? 1 : 0;

    public bool IsHovered
    {
        get => _isHovered;
        set { if (Set(ref _isHovered, value)) { OnPropertyChanged(nameof(RemoveVisibility)); } }
    }

    public Visibility RemoveVisibility => _isHovered ? Visibility.Visible : Visibility.Collapsed;

    private async Task LoadVideoFrameAsync()
    {
        try
        {
            using IRandomAccessStream? frame = await MediaInfo.VideoFrameAsync(Path, 112);
            if (frame is null)
            {
                return;
            }
            var image = new BitmapImage();
            await image.SetSourceAsync(frame);
            Thumb = image;
        }
        catch (Exception)
        {
            // The video could not be read; the tile keeps its icon.
        }
    }
}
