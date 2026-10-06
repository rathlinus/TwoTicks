using Microsoft.UI.Xaml.Markup;
using WinWhatsApp.Core;

namespace WinWhatsApp.App;

/// <summary>Text in the app's language for XAML, as in <c>Text="{app:L Key=main.searchPlaceholder}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class L : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}
