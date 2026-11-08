using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

public sealed class WorkflowRowPolishTests
{
    [AvaloniaFact]
    public void A_status_line_breaks_only_between_groups()
    {
        var text = new TextBlock { Text = "1 failed · 2 waiting · 2 interrupted · 1 stale", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        text.Measure(new Size(90, double.PositiveInfinity));

        var lines = text.TextLayout.TextLines.Select(line => text.Text![line.FirstTextSourceIndex..Math.Min(line.FirstTextSourceIndex + line.Length, text.Text.Length)].Trim()).ToList();

        Assert.Equal(["1 failed ·", "2 waiting ·", "2 interrupted ·", "1 stale"], lines);
    }

    [AvaloniaFact]
    public void A_socket_follows_the_wire_when_the_zoom_tier_changes_its_stroke()
    {
        var wire = new RoutedConnection
        {
            Source = new Point(40, 40),
            Target = new Point(180, 40),
            SourceOnEdge = true,
            SourceOffset = new Size(10, 0),
            TargetOffset = new Size(10, 0),
            Stroke = Brushes.Black,
            Fill = Brushes.Black,
            OutlineBrush = Brushes.Transparent,
            ArrowSize = new Size(0, 0),
            StrokeThickness = 6,
        };
        var window = new Window { Width = 220, Height = 80, Background = Brushes.White, Content = new Panel { Children = { wire } } };
        window.Show();
        try
        {
            // The tallest column of dark pixels at the wire's source end is the socket's height.
            var thick = SocketHeight(window);
            wire.StrokeThickness = 2;
            var thin = SocketHeight(window);

            Assert.True(thick >= 22, $"The socket of a 6 px wire is {thick} px high.");
            Assert.InRange(thin, 6, 10);
        }
        finally
        {
            window.Close();
        }
    }

    private static int SocketHeight(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var pixels = frame.Lock();
        return Enumerable.Range(0, 80).Max(x => Enumerable.Range(0, pixels.Size.Height).Count(y => Marshal.ReadByte(pixels.Address + y * pixels.RowBytes + x * 4, 1) < 128));
    }
}
