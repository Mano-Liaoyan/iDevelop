using Avalonia;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>A workflow that comes back after a conversation closed shows its cards where its view says, at a fit for its size.</summary>
public sealed class ConversationViewportTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell TwoWorkflows()
    {
        var folder = _temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90), TaskAt(TestTasks.Build, "Build", 1400, 900));
        var other = WorkflowDocument.Create(folder, "Other");
        Assert.IsType<EditResult.Applied>(other.Apply(TaskAt(TestTasks.Review, "Review", 2000, 2000)));
        Assert.IsType<EditResult.Applied>(other.Apply(TaskAt(TaskId.New(), "Ship", 3000, 2600)));
        other.Save();
        return Shell.OpenFitted(folder);
    }

    private static Point ExpectedCard(Shell shell, TaskNodeViewModel node) => (Point)((node.DisplayRect.TopLeft - shell.Editor.ViewportLocation) * shell.Editor.ViewportZoom);

    [AvaloniaFact]
    public void A_workflow_that_comes_back_draws_its_cards_at_its_view_at_a_fit_for_the_canvas()
    {
        var shell = TwoWorkflows();
        var window = shell.Window.ViewModel;
        var first = window.Canvas!;
        var second = window.Projects.Single().Workflows.Single(canvas => canvas != first);
        var fit = first.ViewportZoom;
        Assert.NotEqual(1, fit);
        first.Nodes[0].OpenConversationCommand.Execute(null);
        shell.Render();
        window.Select(second);
        shell.Render();
        window.Select(first);
        shell.Render();

        Assert.Equal(fit, first.ViewportZoom, 6);
        foreach (var card in shell.Nodes())
        {
            var node = (TaskNodeViewModel)card.DataContext!;
            var (shown, expected) = (card.TranslatePoint(default, shell.Editor)!.Value, ExpectedCard(shell, node));
            Assert.True(Point.Distance(shown, expected) < 0.5, $"{node.Title} shows at {shown}, and its view puts it at {expected}.");
        }
    }

    [AvaloniaFact]
    public void A_workflow_the_person_moved_keeps_their_view_when_it_comes_back()
    {
        var shell = TwoWorkflows();
        var window = shell.Window.ViewModel;
        var first = window.Canvas!;
        var second = window.Projects.Single().Workflows.Single(canvas => canvas != first);
        shell.Editor.ViewportLocation += new Vector(40, 25);
        var moved = first.ViewportLocation;
        shell.Render();

        window.Select(second);
        shell.Render();
        window.Select(first);
        shell.Render();

        Assert.Equal(moved, first.ViewportLocation);
    }
}
