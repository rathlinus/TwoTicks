using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinWhatsApp.App.Controls;

/// <summary>The strip between the chat list and the chat that resizes the list; it shows the resize cursor.</summary>
public sealed partial class SplitterHandle : Grid
{
    public SplitterHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
