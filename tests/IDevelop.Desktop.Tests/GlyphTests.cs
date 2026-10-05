using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace IDevelop.Desktop.Tests;

public sealed class GlyphTests
{
    // Ink is how far each pixel moved from the white background toward the black glyph, from 0 to 255, by row and column.
    private static int[,] Ink(Geometry data)
    {
        var icon = new PathIcon
        {
            Theme = (ControlTheme)Application.Current!.FindResource("Glyph")!,
            Data = data,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var window = new Window { Width = 64, Height = 64, Background = Brushes.White, Content = icon };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            using var pixels = frame.Lock();
            var ink = new int[16, 16];
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    // Every pixel is a gray between black and white, so its green byte sits at the same offset in RGBA and BGRA.
                    ink[y, x] = 255 - Marshal.ReadByte(pixels.Address + y * pixels.RowBytes + x * 4 + 1);
                }
            }

            return ink;
        }
        finally
        {
            window.Close();
        }
    }

    private static int[,] Ink(string key) => Ink((Geometry)Application.Current!.FindResource(key)!);

    [AvaloniaFact]
    public void The_run_glyph_keeps_the_play_triangle_where_its_16_unit_frame_draws_it()
    {
        var ink = Ink("IconRun");

        // The triangle's left edge is at x = 4 of the frame. The default PathIcon theme stretches it to start near x = 1.
        Assert.Equal((0, 255, 255, 0), (ink[8, 2], ink[8, 4], ink[8, 8], ink[2, 14]));
    }

    [AvaloniaFact]
    public void The_implement_glyph_draws_both_brackets_with_gaps_around_the_slash()
    {
        var ink = Ink("IconKindImplement");

        Assert.Equal((255, 0, 0, 255), (ink[8, 1], ink[8, 4], ink[8, 10], ink[8, 14]));
    }

    [AvaloniaFact]
    public void Every_fluent_icon_lies_inside_the_16_unit_frame_and_draws_ink()
    {
        var icons = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://IDevelop.Desktop/Theme/FluentIcons.axaml"));

        Assert.NotEmpty(icons.Keys);
        Assert.All(icons.Keys.Cast<string>(), key =>
        {
            var data = (Geometry)icons[key]!;
            Assert.True(new Rect(0, 0, 16, 16).Contains(data.Bounds), $"{key} spans {data.Bounds}.");
            Assert.True(Ink(data).Cast<int>().Max() >= 128, $"{key} drew no solid pixel.");
        });
    }
}
