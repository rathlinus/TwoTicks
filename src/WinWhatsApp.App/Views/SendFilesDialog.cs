using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>Asks before sending files: shows them, and takes a caption.</summary>
internal sealed partial class SendFilesDialog : ContentDialog
{
    private static readonly HashSet<string> s_imageTypes = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

    private readonly TextBox _caption;
    private readonly CheckBox _asDocument;

    public SendFilesDialog(IReadOnlyList<string> paths, string chatName, bool asDocument, string caption)
    {
        Title = $"Send to {chatName}";
        PrimaryButtonText = paths.Count == 1 ? "Send" : $"Send {paths.Count} files";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var list = new StackPanel { Spacing = 8 };
        foreach (string path in paths.Take(8))
        {
            list.Children.Add(Row(path));
        }
        if (paths.Count > 8)
        {
            list.Children.Add(new TextBlock { Text = $"and {paths.Count - 8} more", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        }

        _caption = new TextBox
        {
            PlaceholderText = "Add a caption",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            Text = caption.Trim(),
        };
        _asDocument = new CheckBox
        {
            Content = "Send as files, in their original quality",
            IsChecked = asDocument,
            Visibility = paths.All(p => s_imageTypes.Contains(Path.GetExtension(p)) || MediaInfo.IsVideo(p)) ? Visibility.Visible : Visibility.Collapsed,
        };

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children =
            {
                new ScrollViewer { Content = list, MaxHeight = 320 },
                _caption,
                _asDocument,
            },
        };
        Opened += (_, _) => _caption.Focus(FocusState.Programmatic);
    }

    public string Caption => _caption.Text.Trim();
    public bool AsDocument => _asDocument.IsChecked == true;

    private static FrameworkElement Row(string path)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement preview;
        if (s_imageTypes.Contains(Path.GetExtension(path)))
        {
            preview = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(6),
                Child = new Image { Source = Images.FromFile(path, 112), Stretch = Stretch.UniformToFill },
            };
        }
        else
        {
            preview = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                Child = new Controls.WaIcon { Kind = MediaInfo.IsVideo(path) ? MessagePreview.VideoGlyph : MessagePreview.DocumentGlyph, Size = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }
        grid.Children.Add(preview);

        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = Formatting.FileSize(size),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}
