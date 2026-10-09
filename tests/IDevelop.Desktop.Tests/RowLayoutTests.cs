using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The rows that place the window's floating and wrapping controls decide their layout while they measure and only place it
/// while they arrange, so one layout pass settles them, at a width where their controls share a line and at one where
/// they move.
/// </summary>
public sealed class RowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(700)]
    [InlineData(300)]
    public void A_spill_row_settles_in_one_layout_pass(double width)
    {
        var text = new CountingText { Text = "Your reply continues this attempt, and the agent reads it after its turn.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var row = new SpillRow { MinLeadWidth = 200, Children = { text, new Button { Content = "Mark done" }, new Button { Content = "Cancel" }, new Button { Content = "Send" } } };
        var window = Show(row, width);

        Assert.True(row.IsMeasureValid && row.IsArrangeValid, "The row is still invalid after its layout pass.");
        Assert.Equal(width < 400, row.IsSpilled);
        Assert.InRange(text.Measures, 1, 4);

        text.Measures = 0;
        window.Width = width < 400 ? 700 : 300;
        Settle();
        Assert.True(row.IsMeasureValid && row.IsArrangeValid, "The row is still invalid after the window changed width.");
        Assert.InRange(text.Measures, 1, 4);
    }

    [AvaloniaFact]
    public void The_canvas_top_bar_settles_in_one_layout_pass_with_its_pill_in_the_row_and_under_the_buttons()
    {
        var breadcrumb = new CountingText { Text = "reports-service › Release notes" };
        var pill = new Border { Width = 140, Height = 24 };
        var buttons = new Border { Width = 260, Height = 32 };
        CanvasTopBar.SetSlot(breadcrumb, ChromeSlot.Leading);
        CanvasTopBar.SetSlot(pill, ChromeSlot.Center);
        CanvasTopBar.SetSlot(buttons, ChromeSlot.Trailing);
        var bar = new CanvasTopBar { Children = { breadcrumb, pill, buttons } };
        var window = Show(bar, 1000);

        Assert.True(bar.IsMeasureValid && bar.IsArrangeValid && bar.IsInRow);
        Assert.InRange(breadcrumb.Measures, 1, 2);

        breadcrumb.Measures = 0;
        window.Width = 500;
        Settle();
        Assert.True(bar.IsMeasureValid && bar.IsArrangeValid && !bar.IsInRow);
        Assert.InRange(breadcrumb.Measures, 1, 2);
    }

    private static Window Show(Control content, double width)
    {
        var window = new Window { Width = width, Height = 400, Content = new Border { Padding = new Thickness(0), Child = content, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top } };
        window.Show();
        Settle();
        return window;
    }

    // One layout pass: the dispatcher's jobs, which include the layout manager's pass, and nothing more.
    private static void Settle() => Dispatcher.UIThread.RunJobs();

    private sealed class CountingText : TextBlock
    {
        public int Measures { get; set; }

        protected override Type StyleKeyOverride => typeof(TextBlock);

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }
}
