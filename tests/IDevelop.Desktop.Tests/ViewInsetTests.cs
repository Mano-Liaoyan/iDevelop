using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The breadcrumb and Generate float over the canvas's top edge, so a fitted or opening view keeps cards below them.</summary>
public sealed class ViewInsetTests : IDisposable
{
    private static readonly string[] Titles = ["Design", "Build", "Review", "Ship"];
    private static readonly TaskId Ship = new(Guid.Parse("019a9d2e-5d10-7a44-8c2e-1f3b5d7e9a44"));

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    /// <summary>The lowest edge of what floats over the canvas's top: the breadcrumb and the Generate button.</summary>
    private static double ChromeBottom(Shell shell) =>
        Math.Max(shell.Bounds(shell.Find<Border>("Breadcrumb")).Bottom, shell.Bounds(shell.Find<Button>("GenerateWorkflow")).Bottom);

    private static Rect CardInWindow(Shell shell, string title) => shell.CardRect(title).Translate((Vector)shell.Bounds(shell.Editor).TopLeft);

    [AvaloniaFact]
    public void Fit_to_view_keeps_every_card_of_a_tall_workflow_below_the_breadcrumb_and_inside_the_canvas()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 105, 90),
            TaskAt(TestTasks.Build, "Build", 105, 900),
            TaskAt(TestTasks.Review, "Review", 105, 1700),
            TaskAt(Ship, "Ship", 105, 2500)));

        shell.Click(shell.Find<Button>("FitToScreen"));

        var canvas = shell.Bounds(shell.Editor);
        Assert.True(shell.Editor.ViewportZoom < 0.5, $"The fit zoomed to {shell.Editor.ViewportZoom}, so the workflow was not tall.");
        foreach (var title in Titles)
        {
            var card = CardInWindow(shell, title);
            Assert.True(card.Top >= ChromeBottom(shell) + 12 && canvas.Contains(card), $"{title} at {card} is under the breadcrumb or outside {canvas}");
        }

        Assert.Equal(shell.Editor.ViewportLocation, shell.Find<Minimap>("Minimap").ViewportLocation);
    }

    [AvaloniaFact]
    public void A_workflow_whose_top_cards_would_sit_under_the_breadcrumb_opens_with_them_below_it()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 20), TaskAt(TestTasks.Build, "Build", 405, 300)));

        var design = CardInWindow(shell, "Design");
        var chrome = ChromeBottom(shell);
        Assert.True(design.Top >= chrome + 12 && design.Top <= chrome + 40, $"Design at {design} is not just below the breadcrumb, which ends at {chrome}");
        Assert.Equal((0.0, 1.0), (shell.Editor.ViewportLocation.X, shell.Editor.ViewportZoom));
        Assert.Equal(shell.Editor.ViewportLocation, shell.Find<Minimap>("Minimap").ViewportLocation);
    }
}
