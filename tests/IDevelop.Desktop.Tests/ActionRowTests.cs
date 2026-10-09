using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Tests;

/// <summary>The proposal's buttons follow one rule at every width: one line when they fit, else the first above the rest.</summary>
public sealed class ActionRowTests
{
    [AvaloniaFact]
    public void Buttons_that_fit_share_one_line_at_their_own_widths()
    {
        var (row, buttons) = Row(150, 70, 80);

        Arrange(row, 400);

        Assert.True(row.IsOneLine);
        Assert.Equal([new Rect(0, 0, 150, 28), new Rect(158, 0, 70, 28), new Rect(236, 0, 80, 28)], buttons.Select(button => button.Bounds));
    }

    [AvaloniaFact]
    public void Buttons_that_do_not_fit_put_the_first_across_the_width_and_share_the_line_under_it_equally()
    {
        var (row, buttons) = Row(150, 70, 80);

        Arrange(row, 244);

        Assert.False(row.IsOneLine);
        Assert.Equal([new Rect(0, 0, 244, 28), new Rect(0, 36, 118, 28), new Rect(126, 36, 118, 28)], buttons.Select(button => button.Bounds));
        Assert.Equal(64, row.Bounds.Height);
    }

    [AvaloniaFact]
    public void A_hidden_button_takes_no_place()
    {
        var (row, buttons) = Row(150, 70, 80);
        buttons[0].IsVisible = false;

        Arrange(row, 200);

        Assert.True(row.IsOneLine);
        Assert.Equal([new Rect(0, 0, 70, 28), new Rect(78, 0, 80, 28)], buttons.Skip(1).Select(button => button.Bounds));
    }

    private static (ActionRow Row, Button[] Buttons) Row(params double[] widths)
    {
        Button[] buttons = [.. widths.Select(width => new Button { MinWidth = width, MaxHeight = 28, MinHeight = 28, Padding = default })];
        var row = new ActionRow();
        row.Children.AddRange(buttons);
        foreach (var (button, width) in buttons.Zip(widths))
        {
            // A fixed desired width, so the test reads the rule rather than a font's metrics.
            button.Content = new Border { Width = width - 2 };
            button.BorderThickness = new Thickness(1);
            button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }

        return (row, buttons);
    }

    private static void Arrange(ActionRow row, double width)
    {
        var window = new Window { Content = new StackPanel { Width = width, Children = { row } } };
        window.Show();
        window.UpdateLayout();
    }
}
