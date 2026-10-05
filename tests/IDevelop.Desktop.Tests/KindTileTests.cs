using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

public sealed class KindTileTests
{
    private const int Inset = 8;

    private static string[,] Render(ThemeVariant theme, params KindTile[] tiles)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Inset,
            Margin = new Thickness(Inset),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        foreach (var tile in tiles)
        {
            tile.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(tile);
        }

        var window = new Window { Width = 400, Height = 64, Background = Brushes.Black, RequestedThemeVariant = theme, Content = row };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            using var pixels = frame.Lock();
            var rgba = pixels.Format == PixelFormat.Rgba8888;
            Assert.True(rgba || pixels.Format == PixelFormat.Bgra8888, $"Unexpected format {pixels.Format}.");
            var hex = new string[pixels.Size.Height, pixels.Size.Width];
            for (var y = 0; y < pixels.Size.Height; y++)
            {
                for (var x = 0; x < pixels.Size.Width; x++)
                {
                    var pixel = pixels.Address + y * pixels.RowBytes + x * 4;
                    var (r, g, b) = (Marshal.ReadByte(pixel, rgba ? 0 : 2), Marshal.ReadByte(pixel, 1), Marshal.ReadByte(pixel, rgba ? 2 : 0));
                    hex[y, x] = $"#{r:X2}{g:X2}{b:X2}";
                }
            }

            return hex;
        }
        finally
        {
            window.Close();
        }
    }

    private static KindTile[] EveryKind(double size = 32) =>
        [.. Enum.GetValues<NodeKind>().Select(kind => new KindTile { Kind = kind, TileSize = size })];

    private static string At(string[,] hex, int tile, double size, int x, int y) => hex[Inset + y, Inset + tile * (int)(size + Inset) + x];

    // The smallest box around the pixels that are at least half white, in the tile's own pixels.
    private static string GlyphBox(string[,] hex, int tile)
    {
        var background = Sum(At(hex, tile, 32, 3, 16));
        int left = 32, top = 32, right = -1, bottom = -1;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                if ((Sum(At(hex, tile, 32, x, y)) - background) * 2 >= 3 * 255 - background)
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                }
            }
        }

        return $"({left},{top})-({right},{bottom})";
    }

    private static int Sum(string hex) => Convert.ToInt32(hex[1..3], 16) + Convert.ToInt32(hex[3..5], 16) + Convert.ToInt32(hex[5..7], 16);

    [AvaloniaFact]
    public void Each_kind_fills_its_tile_with_its_tile_hue_in_both_themes()
    {
        string[] Hues(ThemeVariant theme)
        {
            var hex = Render(theme, EveryKind());
            return [.. Enumerable.Range(0, 6).Select(tile => At(hex, tile, 32, 3, 16))];
        }

        Assert.Equal(["#564ADE", "#007EAE", "#B02FC2", "#008575", "#956D51", "#6C6C70"], Hues(ThemeVariant.Light));
        Assert.Equal(["#6D7CFF", "#1894C2", "#DB34F2", "#009B89", "#B78A66", "#8E8E93"], Hues(ThemeVariant.Dark));
    }

    [AvaloniaFact]
    public void Each_tile_draws_its_kinds_glyph_in_white_inside_the_middle_five_eighths()
    {
        string[] Glyphs(ThemeVariant theme)
        {
            var hex = Render(theme, EveryKind());
            return [.. Enumerable.Range(0, 6).Select(tile => GlyphBox(hex, tile))];
        }

        // A 16 unit glyph drawn at 20 px from 6 px in, so a glyph spanning the whole frame would cover pixels 6 to 25.
        string[] boxes = ["(7,7)-(24,24)", "(8,9)-(23,23)", "(11,7)-(20,24)", "(7,10)-(24,20)", "(8,9)-(24,24)", "(7,7)-(21,24)"];
        Assert.Equal(boxes, Glyphs(ThemeVariant.Light));
        Assert.Equal(boxes, Glyphs(ThemeVariant.Dark));
        Assert.Equal("#FFFFFF", At(Render(ThemeVariant.Dark, EveryKind()), 0, 32, 8, 16));
    }

    [AvaloniaFact]
    public void A_tiles_glyph_is_the_icon_its_kind_names()
    {
        var tiles = EveryKind();
        var row = new StackPanel();
        row.Children.AddRange(tiles);
        new Window { Content = row }.Show();

        Assert.All(tiles, tile => Assert.Same(
            Application.Current!.FindResource(NodeKinds.Info(tile.Kind).Icon),
            tile.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Classes.Contains("kindIcon")).Data));
    }

    [AvaloniaFact]
    public void A_library_tile_shows_the_badge_across_its_corner_from_24_px_and_an_18_px_tile_never_does()
    {
        string Badges(ThemeVariant theme)
        {
            var large = Render(theme, new KindTile(), new KindTile { IsLibrary = true });
            var medium = Render(theme, new KindTile { TileSize = 24 }, new KindTile { TileSize = 24, IsLibrary = true });
            return string.Join(" ", At(large, 0, 32, 29, 4), At(large, 1, 32, 29, 4), At(large, 1, 32, 31, -1),
                At(medium, 0, 24, 22, 4), At(medium, 1, 24, 22, 4));
        }

        Assert.Equal("#564ADE #FFFFFF #FFFFFF #564ADE #FFFFFF", Badges(ThemeVariant.Light));
        Assert.Equal("#6D7CFF #22201D #22201D #6D7CFF #22201D", Badges(ThemeVariant.Dark));

        var small = Render(ThemeVariant.Light, new KindTile { TileSize = 18 }, new KindTile { TileSize = 18, IsLibrary = true });
        var rows = Enumerable.Range(-Inset, 18 + 2 * Inset);
        Assert.Equal(
            rows.Select(y => string.Join(" ", Enumerable.Range(0, 18 + Inset).Select(x => At(small, 0, 18, x, y)))),
            rows.Select(y => string.Join(" ", Enumerable.Range(0, 18 + Inset).Select(x => At(small, 1, 18, x, y)))));
    }

    [AvaloniaFact]
    public void A_tile_is_named_for_its_kind()
    {
        var tile = new KindTile();
        var implement = AutomationProperties.GetName(tile);
        tile.Kind = NodeKind.ReadOnlyAgent;

        Assert.Equal(("Implement", "Read-only agent"), (implement, AutomationProperties.GetName(tile)));
    }
}
