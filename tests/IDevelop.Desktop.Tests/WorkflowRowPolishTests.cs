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
}
