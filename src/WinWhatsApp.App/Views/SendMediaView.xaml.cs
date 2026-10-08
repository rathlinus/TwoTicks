using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using WinWhatsApp.App.Models;

namespace WinWhatsApp.App.Views;

/// <summary>
/// Files about to be sent, as WhatsApp shows them over the chat: the selected
/// one large with its caption, all of them in a row below, and send.
/// </summary>
public sealed partial class SendMediaView : UserControl
{
    public static readonly string[] PhotoAndVideoTypes = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".mp4", ".mov", ".m4v", ".3gp"];

    // Larger photos are decoded at this size for the preview, not at their own.
    private const int MaxPreviewSide = 2400;

    private readonly ObservableCollection<PendingFile> _files = [];
    private PendingFile? _selected;
    private bool _documents;
    private int _previewVersion;

    public SendMediaView()
    {
        InitializeComponent();
        Tiles.ItemsSource = _files;
        EmojiPickerPanel.Picked += OnEmojiPicked;
    }

    /// <summary>Send was pressed: the files with their captions, and whether photos and videos go as files.</summary>
    public event Action<IReadOnlyList<PendingFile>, bool>? SendRequested;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <param name="documents">Opened to send documents: the add tile then picks any file, and photos go as files.</param>
    /// <param name="caption">What the first file starts with as its caption, such as what was typed in the chat.</param>
    public void Open(IReadOnlyList<string> paths, bool documents, string caption)
    {
        Close();
        _documents = documents;
        AsDocumentBox.IsChecked = documents;
        Add(paths);
        if (_files.Count == 0)
        {
            return;
        }
        _files[0].Caption = caption;
        Select(_files[0]);
        Visibility = Visibility.Visible;
        CaptionBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Adds files, as more are dropped, pasted or picked while it is open.</summary>
    public void Add(IEnumerable<string> paths)
    {
        PendingFile? first = null;
        foreach (string path in paths)
        {
            if (File.Exists(path) && !_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                var file = new PendingFile(path);
                _files.Add(file);
                first ??= file;
            }
        }
        if (IsOpen && first is not null)
        {
            Select(first);
        }
        UpdateControls();
    }

    public void Close()
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
            _selected = null;
        }
        _previewVersion++;
        StopVideo();
        Picture.Source = null;
        _files.Clear();
        CaptionBox.Text = "";
        Visibility = Visibility.Collapsed;
    }

    private void Select(PendingFile file)
    {
        if (_selected is { } previous)
        {
            previous.Caption = CaptionBox.Text;
            previous.IsSelected = false;
        }
        _selected = file;
        file.IsSelected = true;
        CaptionBox.Text = file.Caption;
        _ = ShowPreviewAsync(file);
    }

    private async Task ShowPreviewAsync(PendingFile file)
    {
        int version = ++_previewVersion;
        StopVideo();
        Picture.Source = null;
        PictureBox.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        FileCard.Visibility = Visibility.Collapsed;

        if (file.IsVideo)
        {
            VideoPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(file.Path));
            VideoPlayer.Visibility = Visibility.Visible;
            return;
        }
        BitmapImage? picture = file.IsPicture ? await LoadPictureAsync(file.Path) : null;
        if (version != _previewVersion)
        {
            return;
        }
        if (picture is not null)
        {
            Picture.Source = picture;
            PictureBox.Visibility = Visibility.Visible;
        }
        else
        {
            FileNameText.Text = file.Name;
            FileInfoText.Text = file.Info;
            FileCard.Visibility = Visibility.Visible;
        }
    }

    /// <summary>A photo at its own size, or at screen size when it is larger. Null when it cannot be read.</summary>
    private static async Task<BitmapImage?> LoadPictureAsync(string path)
    {
        try
        {
            (uint width, uint height) = await MediaInfo.PictureSizeAsync(path);
            var image = new BitmapImage();
            if (width >= height && width > MaxPreviewSide)
            {
                image.DecodePixelWidth = MaxPreviewSide;
            }
            else if (height > width && height > MaxPreviewSide)
            {
                image.DecodePixelHeight = MaxPreviewSide;
            }
            image.UriSource = new Uri(path);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void StopVideo()
    {
        VideoPlayer.MediaPlayer?.Pause();
        VideoPlayer.Source = null;
    }

    private void UpdateControls()
    {
        AsDocumentBox.Visibility = _files.Count > 0 && _files.All(f => f.IsMedia) ? Visibility.Visible : Visibility.Collapsed;
        CountBadge.Value = _files.Count;
        CountBadge.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Send()
    {
        if (_selected is { } selected)
        {
            selected.Caption = CaptionBox.Text;
        }
        var files = _files.ToList();
        // Without the choice, as when documents are mixed in, they go as they were opened.
        bool asDocument = AsDocumentBox.Visibility == Visibility.Visible ? AsDocumentBox.IsChecked == true : _documents;
        Close();
        if (files.Count > 0)
        {
            SendRequested?.Invoke(files, asDocument);
        }
    }

    // ---- Input ----

    private void OnSendClick(object sender, RoutedEventArgs e) => Send();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !EmojiFlyout.IsOpen)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnCaptionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && !shift)
        {
            e.Handled = true;
            Send();
        }
    }

    /// <summary>Pasting a picture or files adds them, as in the chat's message box.</summary>
    private async void OnCaptionPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync();
            Add(items.OfType<StorageFile>().Select(f => f.Path));
        }
        else if (content.Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            if (await MediaInfo.SaveClipboardImageAsync(content) is { } path)
            {
                Add([path]);
            }
        }
    }

    private void OnEmojiPicked(string emoji)
    {
        CaptionBox.Insert(emoji);
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        App.Current.InitializePicker(picker);
        foreach (string type in _documents ? ["*"] : PhotoAndVideoTypes)
        {
            picker.FileTypeFilter.Add(type);
        }
        IReadOnlyList<StorageFile> picked = await picker.PickMultipleFilesAsync();
        Add(picked.Select(f => f.Path));
        CaptionBox.Focus(FocusState.Programmatic);
    }

    // ---- The row of files ----

    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        // A tap on its cross comes here too, after the file was taken out.
        if (sender is FrameworkElement { Tag: PendingFile file } && file != _selected && _files.Contains(file))
        {
            Select(file);
        }
    }

    private void OnTilePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingFile file })
        {
            file.IsHovered = true;
        }
    }

    private void OnTilePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingFile file })
        {
            file.IsHovered = false;
        }
    }

    private void OnRemoveTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PendingFile file })
        {
            return;
        }
        int index = _files.IndexOf(file);
        _files.Remove(file);
        if (_files.Count == 0)
        {
            Close();
            return;
        }
        if (file == _selected)
        {
            _selected = null;
            Select(_files[Math.Min(index, _files.Count - 1)]);
        }
        UpdateControls();
    }
}
