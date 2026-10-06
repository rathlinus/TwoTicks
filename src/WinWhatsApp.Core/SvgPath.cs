using System.Globalization;
using System.Text;

namespace WinWhatsApp.Core;

/// <summary>
/// Moves and scales SVG path data, so that an icon drawn in its view box comes
/// out at the size it is shown at.
/// </summary>
public static class SvgPath
{
    private static readonly Dictionary<char, int> s_arguments = new()
    {
        ['m'] = 2, ['l'] = 2, ['h'] = 1, ['v'] = 1, ['c'] = 6, ['s'] = 4, ['q'] = 4, ['t'] = 2, ['a'] = 7, ['z'] = 0,
    };

    /// <summary>
    /// Maps the view box onto a square of the given size: x' = (x - left) * scale.
    /// The path data must have its values separated, as build.py writes it.
    /// </summary>
    public static string Transform(string data, double left, double top, double scale)
    {
        var output = new StringBuilder(data.Length);
        string[] tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        char command = ' ';
        int i = 0;
        while (i < tokens.Length)
        {
            string token = tokens[i];
            if (token.Length == 1 && char.IsLetter(token[0]))
            {
                command = token[0];
                output.Append(command).Append(' ');
                i++;
                if (char.ToLowerInvariant(command) == 'z')
                {
                    continue;
                }
            }

            int count = s_arguments[char.ToLowerInvariant(command)];
            bool relative = char.IsLower(command);
            for (int k = 0; k < count && i < tokens.Length; k++, i++)
            {
                double value = double.Parse(tokens[i], CultureInfo.InvariantCulture);
                value = Map(char.ToLowerInvariant(command), k, value, relative, left, top, scale);
                output.Append(Format(value, char.ToLowerInvariant(command) == 'a' && k is 2 or 3 or 4)).Append(' ');
            }
            // Further pairs after a move are lines.
            if (command == 'M')
            {
                command = 'L';
            }
            else if (command == 'm')
            {
                command = 'l';
            }
        }
        return output.ToString().TrimEnd();
    }

    private static double Map(char command, int k, double value, bool relative, double left, double top, double scale)
    {
        switch (command)
        {
            case 'a':
                // Radii scale; the rotation and the two flags stay; the end point moves.
                return k switch
                {
                    0 or 1 => value * scale,
                    2 or 3 or 4 => value,
                    5 => relative ? value * scale : (value - left) * scale,
                    _ => relative ? value * scale : (value - top) * scale,
                };
            case 'h':
                return relative ? value * scale : (value - left) * scale;
            case 'v':
                return relative ? value * scale : (value - top) * scale;
            default:
                // Coordinates come in x, y pairs.
                if (relative)
                {
                    return value * scale;
                }
                return k % 2 == 0 ? (value - left) * scale : (value - top) * scale;
        }
    }

    private static string Format(double value, bool keepAsIs) =>
        keepAsIs ? value.ToString(CultureInfo.InvariantCulture) : Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
}
