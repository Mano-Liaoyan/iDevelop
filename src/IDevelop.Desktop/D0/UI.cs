using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.D0;

// Throwaway D0 prototype helpers. Never merge.
internal static class UI
{
    public static object Res(string key)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var v) ? v! : throw new KeyNotFoundException(key);
    }

    public static IBrush Br(string key) => key.StartsWith('#') ? Brush.Parse(key) : (IBrush)Res(key);

    public static IBrush Canvas() => new SolidColorBrush((Color)Res("CanvasGrid.BackgroundColor"));

    public static CornerRadius Rad(string key) => (CornerRadius)Res(key);

    public static FontFamily Mono { get; } = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    public static Control Icon(string? key, double size = 16, string brush = "TextMutedBrush")
    {
        if (key is null)
        {
            // The neutral fallback for a field with no fitting glyph in the manifest: an empty rounded frame.
            return new Border
            {
                Width = size - 4, Height = size - 4, Margin = new Thickness(2), CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1.2), BorderBrush = Br("Border100Brush"), VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new PathIcon
        {
            Theme = (ControlTheme)Res("Glyph"), Data = (Geometry)Res(key), Width = size, Height = size,
            Foreground = Br(brush), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock T(string text, double size = 13, string brush = "TextBrush", FontWeight weight = FontWeight.Normal, bool trim = true)
        => new()
        {
            Text = text, FontSize = size, Foreground = Br(brush), FontWeight = weight, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };

    public static TextBlock Wrap(string text, double size = 13, string brush = "TextBrush")
        => new() { Text = text, FontSize = size, Foreground = Br(brush), TextWrapping = TextWrapping.Wrap };

    public static KindTile Tile(NodeKind kind, double size, bool library = false) =>
        new() { Kind = kind, TileSize = size, IsLibrary = library, VerticalAlignment = VerticalAlignment.Center };

    public static Button Glyph(string icon, double size = 24, string brush = "TextMutedBrush", double iconSize = 14)
        => new()
        {
            Width = size, Height = size, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Content = Icon(icon, iconSize, brush),
        };

    public static Button Btn(string text, string? icon = null, string cls = "action", double height = 28)
    {
        var b = new Button { Height = height, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        b.Classes.Add(cls);
        var brush = cls == "primary" ? "OnAccentBrush" : "TextBrush";
        b.Content = icon is null ? T(text, 13, brush) : Row(6, Icon(icon, 14, brush), T(text, 13, brush));
        return b;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        s.Children.AddRange(children);
        return s;
    }

    public static StackPanel Col(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        s.Children.AddRange(children);
        return s;
    }

    public static Control Picker(string text, bool borderless = false)
    {
        if (borderless)
        {
            return Row(4, T(text, 13, "TextBrush"), Icon("IconChevronDown", 12, "TextFaintBrush"));
        }

        var c = new ComboBox { FontSize = 13, MinHeight = 28, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = text };
        c.ItemsSource = new[] { text };
        c.SelectedIndex = 0;
        return c;
    }

    public static TextBox Box(string text, bool multiline = false, string? watermark = null)
    {
        var b = new TextBox
        {
            Text = text, FontSize = 13, MinHeight = multiline ? 64 : 28, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            Watermark = watermark,
        };
        return b;
    }

    public static Border Chip(string text, string? icon = null, string bg = "SurfaceMutedBrush", string fg = "TextBrush")
        => new()
        {
            Height = 22, Padding = new Thickness(7, 0), CornerRadius = Rad("RadiusSm"), Background = Br(bg), VerticalAlignment = VerticalAlignment.Center,
            Child = icon is null ? T(text, 12, fg) : Row(4, Icon(icon, 12, fg), T(text, 12, fg)),
        };

    public static Border Rule(double top = 0, double bottom = 0) =>
        new() { Height = 1, Background = Br("Border80Brush"), Margin = new Thickness(0, top, 0, bottom) };

    public static T2 At<T2>(this T2 c, double x, double y) where T2 : Control
    {
        Avalonia.Controls.Canvas.SetLeft(c, x);
        Avalonia.Controls.Canvas.SetTop(c, y);
        return c;
    }

    public static T2 M<T2>(this T2 c, double l, double t, double r, double b) where T2 : Control
    {
        c.Margin = new Thickness(l, t, r, b);
        return c;
    }

    public static T2 HA<T2>(this T2 c, HorizontalAlignment h) where T2 : Control
    {
        c.HorizontalAlignment = h;
        return c;
    }

    // A tiny Markdown-looking inline builder: `code`, **bold**, [link](x). Only for synthetic prototype text.
    public static TextBlock Md(string text, double size = 13, string brush = "TextBrush")
    {
        var tb = new TextBlock { FontSize = size, Foreground = Br(brush), TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5 };
        var inlines = new InlineCollection();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                inlines.Add(new Run(" " + text[(i + 1)..end] + " ") { FontFamily = Mono, FontSize = size - 1, Background = Br("SurfaceMutedBrush") });
                i = end + 1;
            }
            else if (text.AsSpan(i).StartsWith("**"))
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                inlines.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeight.SemiBold });
                i = end + 2;
            }
            else if (text[i] == '[')
            {
                var mid = text.IndexOf("](", i, StringComparison.Ordinal);
                var end = text.IndexOf(')', mid);
                inlines.Add(new Run(text[(i + 1)..mid]) { Foreground = Br("AccentTextBrush"), TextDecorations = TextDecorations.Underline });
                i = end + 1;
            }
            else
            {
                var next = text.IndexOfAny(['`', '*', '['], i + 1);
                if (next < 0)
                {
                    next = text.Length;
                }

                inlines.Add(new Run(text[i..next]));
                i = next;
            }
        }

        tb.Inlines = inlines;
        return tb;
    }
}
