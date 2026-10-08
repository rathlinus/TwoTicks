using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TwoTicks.Core;

namespace TwoTicks.App.Controls;

internal readonly record struct IconPath(string Data, bool EvenOdd);

internal sealed record IconData(string ViewBox, IconPath[] Paths);

/// <summary>
/// WhatsApp Web's icons, which WaIcons.g.cs holds as SVG path data, scaled to
/// the size they are shown at.
/// </summary>
internal static partial class WaIcons
{
    private static readonly Dictionary<(string Name, double Size), IconPath[]> s_scaled = [];

    public static bool Exists(string name) => s_icons.ContainsKey(name);

    /// <summary>The paths of an icon, fitted into a square of the given size and centred in it.</summary>
    public static IconPath[] Scaled(string name, double size)
    {
        if (s_scaled.TryGetValue((name, size), out IconPath[]? cached))
        {
            return cached;
        }
        if (!s_icons.TryGetValue(name, out IconData? icon))
        {
            return [];
        }
        double[] box = icon.ViewBox.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        double side = Math.Max(box[2], box[3]);
        double scale = size / side;
        double left = box[0] - (side - box[2]) / 2;
        double top = box[1] - (side - box[3]) / 2;
        IconPath[] paths = icon.Paths.Select(p => p with { Data = SvgPath.Transform(p.Data, left, top, scale) }).ToArray();
        s_scaled[(name, size)] = paths;
        return paths;
    }

    public static Geometry Geometry(IconPath path) =>
        (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), (path.EvenOdd ? "F0 " : "F1 ") + path.Data);

    /// <summary>An icon for a menu entry or another place that takes an IconElement.</summary>
    public static PathIcon PathIcon(string name, double size = 16)
    {
        IconPath[] paths = Scaled(name, size);
        bool evenOdd = paths.Any(p => p.EvenOdd);
        string data = string.Join(" ", paths.Select(p => p.Data));
        return new PathIcon { Data = Geometry(new IconPath(data, evenOdd)) };
    }
}
