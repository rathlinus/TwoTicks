using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinWhatsApp.App.Controls;

public sealed partial class EmojiPicker
{
    /// <summary>A cell of the grid was made for an emoji, or goes on to show another one.</summary>
    private void OnCellDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Grid host && args.NewValue is string text)
        {
            Fill(host, text);
        }
    }
}
