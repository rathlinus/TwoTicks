using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// WhatsApp's doodle wallpaper behind the messages: a tile repeated across
/// the chat, faint over the background colour, as WhatsApp Web draws it.
/// </summary>
public sealed partial class ChatWallpaper : Canvas
{
    // The tiles are 540 by 960 pixels; WhatsApp Web shows them a little smaller.
    private const double TileWidth = 405;
    private const double TileHeight = 720;

    // WhatsApp Web's opacity for its default wallpaper.
    private const double LightOpacity = 0.1;
    private const double DarkOpacity = 0.06;

    private static BitmapImage? s_light;
    private static BitmapImage? s_dark;

    public ChatWallpaper()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Fill();
        ActualThemeChanged += (_, _) => Restyle();
    }

    private BitmapImage Source => ActualTheme == ElementTheme.Dark
        ? s_dark ??= Load("doodle-dark.webp")
        : s_light ??= Load("doodle-light.webp");

    private static BitmapImage Load(string name) =>
        new(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsApp", name)));

    private void Fill()
    {
        int columns = (int)Math.Ceiling(ActualWidth / TileWidth);
        int rows = (int)Math.Ceiling(ActualHeight / TileHeight);
        int needed = Math.Max(0, columns * rows);
        while (Children.Count < needed)
        {
            Children.Add(new Image { Width = TileWidth, Height = TileHeight, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill });
        }
        while (Children.Count > needed)
        {
            Children.RemoveAt(Children.Count - 1);
        }
        for (int i = 0; i < needed; i++)
        {
            SetLeft(Children[i], i % columns * TileWidth);
            SetTop(Children[i], i / columns * TileHeight);
        }
        // A canvas draws its children past its edges; keep the tiles inside.
        Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new(0, 0, ActualWidth, ActualHeight) };
        Restyle();
    }

    private void Restyle()
    {
        BitmapImage source = Source;
        double opacity = ActualTheme == ElementTheme.Dark ? DarkOpacity : LightOpacity;
        foreach (UIElement child in Children)
        {
            if (child is Image image)
            {
                image.Source = source;
                image.Opacity = opacity;
            }
        }
    }
}
