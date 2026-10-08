using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace TwoTicks.App.Controls;

/// <summary>
/// Keeps the private code points that stand for emoji in shown text out of
/// the clipboard: copying or cutting a selection puts the emoji themselves
/// there. See <see cref="Emoji"/>.
/// </summary>
internal static class EmojiClipboard
{
    /// <summary>Watches for copy and cut anywhere under the root of a window.</summary>
    public static void Attach(UIElement root) => root.PreviewKeyDown += OnPreviewKeyDown;

    private static void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.C or VirtualKey.X) || !CommandKeyDown() || sender is not UIElement { XamlRoot: { } root })
        {
            return;
        }
        object? focused = FocusManager.GetFocusedElement(root);
        string selected = focused switch
        {
            TextBox box => box.SelectedText,
            TextBlock block => block.SelectedText,
            _ => "",
        };
        string plain = Emoji.ToPlain(selected);
        if (plain == selected)
        {
            // Nothing to change: the control copies it itself.
            return;
        }
        var package = new DataPackage();
        package.SetText(plain);
        Clipboard.SetContent(package);
        if (e.Key == VirtualKey.X && focused is TextBox { IsReadOnly: false } cut)
        {
            cut.SelectedText = "";
        }
        e.Handled = true;
    }

    /// <summary>Control, or Command on a Mac.</summary>
    public static bool CommandKeyDown() =>
        IsDown(VirtualKey.Control) || (OperatingSystem.IsMacOS() && (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)));

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
