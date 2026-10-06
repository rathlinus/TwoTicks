using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinWhatsApp.App.Controls;

/// <summary>A name or a picture that opens something when clicked; it shows the hand cursor, as links do.</summary>
public sealed partial class PlainButton : Button
{
    public PlainButton()
    {
        Style = (Style)Application.Current.Resources["PlainButtonStyle"];
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
