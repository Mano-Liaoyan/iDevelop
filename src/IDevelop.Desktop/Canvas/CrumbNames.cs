using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The breadcrumb's names: the project's part, then the workflow's name. When both do not fit, the project's part gives up its width
/// first and leaves when it can no longer read, so the workflow's name is cut only when it alone is wider than the room.
/// </summary>
public sealed class CrumbNames : Panel
{
    /// <summary>The least width of the project's part, with its separator, at which a cut project name still reads.</summary>
    private const double LeastProjectWidth = 88;

    private double _projectWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [var project, var workflow])
        {
            return default;
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        project.Measure(infinite);
        workflow.Measure(infinite);
        var (projectNatural, workflowNatural) = (project.DesiredSize.Width, workflow.DesiredSize.Width);
        var room = availableSize.Width;
        var projectRoom = room - workflowNatural;
        _projectWidth = projectRoom >= projectNatural ? projectNatural : projectRoom >= LeastProjectWidth ? projectRoom : 0;
        project.Measure(new Size(_projectWidth, double.PositiveInfinity));
        workflow.Measure(new Size(Math.Max(0, Math.Min(workflowNatural, room - _projectWidth)), double.PositiveInfinity));
        return new Size(_projectWidth + workflow.DesiredSize.Width, Math.Max(project.DesiredSize.Height, workflow.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is [var project, var workflow])
        {
            project.Arrange(new Rect(0, 0, _projectWidth, finalSize.Height));
            workflow.Arrange(new Rect(_projectWidth, 0, workflow.DesiredSize.Width, finalSize.Height));
        }

        return finalSize;
    }
}
