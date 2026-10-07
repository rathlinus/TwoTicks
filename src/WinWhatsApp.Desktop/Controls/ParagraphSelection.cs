using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// On Windows this selects a paragraph on a triple click, which a RichTextBlock
/// does not do by itself. The text here is a TextBlock, which has its own
/// selection; the property exists so the same XAML holds on both.
/// </summary>
public static class ParagraphSelection
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ParagraphSelection), new PropertyMetadata(false));

    public static bool GetIsEnabled(TextBlock block) => (bool)block.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBlock block, bool value) => block.SetValue(IsEnabledProperty, value);
}
