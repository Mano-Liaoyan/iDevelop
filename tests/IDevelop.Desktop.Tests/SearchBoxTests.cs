using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Tests;

public sealed class SearchBoxTests
{
    [AvaloniaFact]
    public void Every_search_box_in_a_window_shows_the_search_glyph_inside_itself()
    {
        TextBox[] boxes = [new() { Classes = { "search" } }, new() { Classes = { "search" } }];
        var panel = new StackPanel();
        panel.Children.AddRange(boxes);
        new Window { Content = panel }.Show();

        Assert.All(boxes, box =>
        {
            var glyph = Assert.IsType<PathIcon>(box.InnerLeftContent);
            Assert.Same(Application.Current!.FindResource("IconSearch"), glyph.Data);
            Assert.Contains(box, glyph.GetVisualAncestors());
        });
    }
}
