using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace WinWhatsApp.App.Controls;

/// <summary>
/// A voice message's waveform, as WhatsApp draws it: thin bars, coloured up to
/// where it has played, and a dot at that point.
/// </summary>
/// <remarks>
/// WhatsApp sends 64 loudness values from 0 to 100 with every voice message.
/// Without them, as for audio files, the bars are all the same low height.
/// Made in code, as <see cref="Ticks"/> is, because every voice message has one.
/// </remarks>
public sealed partial class Waveform : UserControl
{
    private const double BarWidth = 3;
    private const double Gap = 2;
    private const double MinBar = 4;
    private const double DotSize = 13;

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(byte[]), typeof(Waveform), new PropertyMetadata(null, (d, _) => ((Waveform)d).Rebuild()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Waveform), new PropertyMetadata(0.0, (d, _) => ((Waveform)d).Paint()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(string), typeof(Waveform), new PropertyMetadata("ReadTicksBrush", (d, _) => ((Waveform)d).Paint()));

    // Transparent rather than empty, so a click between the bars still seeks.
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Ellipse _dot = new() { Width = DotSize, Height = DotSize };
    private readonly List<Rectangle> _bars = [];

    public Waveform()
    {
        IsTabStop = false;
        Height = 28;
        Content = _canvas;
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width != e.PreviousSize.Width)
            {
                Rebuild();
            }
        };
        ActualThemeChanged += (_, _) => Paint();
    }

    public byte[]? Samples
    {
        get => (byte[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>How far it has played, from 0 to 100.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>The theme brush for the dot and the played bars.</summary>
    public string Accent
    {
        get => (string)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private void Rebuild()
    {
        double width = ActualWidth - DotSize / 2;
        double height = Height;
        _canvas.Children.Clear();
        _bars.Clear();
        if (width <= 0)
        {
            return;
        }

        int count = Math.Max(1, (int)((width + Gap) / (BarWidth + Gap)));
        byte[]? samples = Samples is { Length: > 0 } s ? s : null;
        // Loudness relative to the loudest part, so a quiet recording still has a shape.
        double loudest = samples is null ? 1 : Math.Max(1.0, samples.Max());
        for (int i = 0; i < count; i++)
        {
            double level = 0;
            if (samples is not null)
            {
                // Each bar takes the loudest of the values that fall on it.
                int from = i * samples.Length / count;
                int to = Math.Max(from + 1, (i + 1) * samples.Length / count);
                for (int j = from; j < to && j < samples.Length; j++)
                {
                    level = Math.Max(level, samples[j] / loudest);
                }
            }
            double barHeight = Math.Max(MinBar, level * height);
            var bar = new Rectangle { Width = BarWidth, Height = barHeight, RadiusX = BarWidth / 2, RadiusY = BarWidth / 2 };
            Canvas.SetLeft(bar, DotSize / 2 + i * (BarWidth + Gap));
            Canvas.SetTop(bar, (height - barHeight) / 2);
            _bars.Add(bar);
            _canvas.Children.Add(bar);
        }
        Canvas.SetTop(_dot, (height - DotSize) / 2);
        _canvas.Children.Add(_dot);
        Paint();
    }

    private void Paint()
    {
        if (_bars.Count == 0)
        {
            return;
        }
        Brush? played = Ticks.ThemeBrush(Accent, ActualTheme);
        Brush? rest = Ticks.ThemeBrush("WaveformBrush", ActualTheme);
        double position = Math.Clamp(Progress / 100, 0, 1) * (ActualWidth - DotSize);
        foreach (Rectangle bar in _bars)
        {
            bar.Fill = Canvas.GetLeft(bar) + BarWidth / 2 <= position + DotSize / 2 && Progress > 0 ? played : rest;
        }
        _dot.Fill = played;
        Canvas.SetLeft(_dot, position);
    }
}
