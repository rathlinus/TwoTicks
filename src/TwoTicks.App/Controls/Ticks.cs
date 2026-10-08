using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwoTicks.Core;

namespace TwoTicks.App.Controls;

/// <summary>
/// The marks after one's own message, WhatsApp's: a clock while sending, one
/// tick when sent, two when delivered, two blue ones when read.
/// </summary>
/// <remarks>
/// Made in code, with a single icon, because every message row has one: a
/// control with XAML of its own costs far more to make.
/// </remarks>
public sealed partial class Ticks : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(int), typeof(Ticks), new PropertyMetadata(int.MinValue, (d, _) => ((Ticks)d).Refresh()));

    private readonly WaIcon _icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public Ticks()
    {
        IsTabStop = false;
        Width = 16;
        Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        Content = _icon;
        ActualThemeChanged += (_, _) => Refresh();
    }

    public int Status
    {
        get => (int)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private void Refresh()
    {
        int status = Status;
        (string? kind, double size, string brush) = status switch
        {
            MessageStatus.Pending => ("Pending", 12, "MetaTextBrush"),
            MessageStatus.Sent => ("Sent", 16, "MetaTextBrush"),
            // The double tick of "read", in grey: WhatsApp Web's own "delivered" icon has only one.
            MessageStatus.Delivered => ("Read", 16, "MetaTextBrush"),
            MessageStatus.Failed => ("Failed", 14, "FailedBrush"),
            >= MessageStatus.Read => ("Read", 16, "ReadTicksBrush"),
            _ => (null, 16, "MetaTextBrush"),
        };
        _icon.Kind = kind;
        _icon.Size = size;
        _icon.Foreground = ThemeBrush(brush, ActualTheme);
    }

    private static readonly Dictionary<(string, ElementTheme), Brush?> s_brushes = [];

    /// <summary>A brush of the app's light or dark colours, as ThemeResource would pick it.</summary>
    internal static Brush? ThemeBrush(string key, ElementTheme theme)
    {
        if (!s_brushes.TryGetValue((key, theme), out Brush? brush))
        {
            string name = theme == ElementTheme.Dark ? "Dark" : "Light";
            brush = Application.Current.Resources.ThemeDictionaries.TryGetValue(name, out object? dictionary)
                && ((ResourceDictionary)dictionary).TryGetValue(key, out object? value) ? value as Brush : null;
            s_brushes[(key, theme)] = brush;
        }
        return brush;
    }
}
