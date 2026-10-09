using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using Nodify;

namespace IDevelop.Desktop.Proto;

// THROWAWAY N1 PROTOTYPE. Never merge. The view state an option keeps for expansion, and what the probe measures.
internal static class N1View
{
    /// <summary>Shows the marked places where GitHub issues, pull requests, and comments will appear.</summary>
    public static bool Placeholders { get; set; }

    public static void SetExpanded(TaskNodeViewModel node, bool expanded) => node.IsExpanded = expanded;

    public static void ExpandAll(WorkflowCanvasViewModel canvas, bool expanded)
    {
        foreach (var node in canvas.Nodes)
        {
            node.IsExpanded = expanded;
        }
    }

    public static void SetPeek(WorkflowCanvasViewModel canvas, TaskNodeViewModel? node)
    {
        foreach (var each in canvas.Nodes)
        {
            each.IsPeeking = ReferenceEquals(each, node);
        }
    }

    public static void Refresh(TaskNodeViewModel node) => node.RefreshDetail();

    /// <summary>The card's visible box in canvas units: its container less the port gutters.</summary>
    public static Rect Bounds(NodifyEditor editor, TaskNodeViewModel node)
    {
        var container = editor.GetVisualDescendants().OfType<ItemContainer>().FirstOrDefault(c => ReferenceEquals(c.DataContext, node));
        if (container is null)
        {
            return default;
        }

        var size = container.Bounds.Size;
        return new Rect(container.Location.X + 10, container.Location.Y, Math.Max(0, size.Width - 20), size.Height);
    }

    /// <summary>Surfaces an option draws outside the cards, such as a flyout, named "surface:&lt;title&gt;".</summary>
    public static IEnumerable<(string Title, Rect Box)> Surfaces(Window window) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Tag is string tag && tag.StartsWith("surface:", StringComparison.Ordinal) && control.IsEffectivelyVisible)
            .Select(control => ((string)control.Tag!, SurfaceBox(control)));

    private static Rect SurfaceBox(Control control) =>
        control.DataContext is MarginNote note ? new Rect(note.Location, control.Bounds.Size)
        : control.FindAncestorOfType<ItemContainer>() is { } container
            ? new Rect(container.Location + (control.TranslatePoint(default, container) ?? default), control.Bounds.Size)
            : control.FindAncestorOfType<DecoratorContainer>() is { } decorator
                ? new Rect(decorator.Location + (control.TranslatePoint(default, decorator) ?? default), control.Bounds.Size)
                : control.Bounds;
}

/// <summary>Reports a card's laid-out size to its node, so the option's layout and the wires follow a card that grows.</summary>
public static class N1Size
{
    public static readonly AttachedProperty<bool> TrackProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Track", typeof(N1Size));

    static N1Size()
    {
        TrackNoteProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
            {
                control.SizeChanged += (_, args) =>
                {
                    if (control.DataContext is MarginNote note)
                    {
                        N1Notes.Measured(note.Node, args.NewSize);
                    }
                };
            }
        });
        TrackProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
            {
                control.SizeChanged += (_, args) =>
                {
                    if (control.DataContext is TaskNodeViewModel node)
                    {
                        node.MeasuredSize = args.NewSize;
                    }
                };
            }
        });
    }

    /// <summary>Reports a margin note's size, so the lane stacks notes without overlap.</summary>
    public static readonly AttachedProperty<bool> TrackNoteProperty = AvaloniaProperty.RegisterAttached<Control, bool>("TrackNote", typeof(N1Size));

    public static bool GetTrackNote(Control control) => control.GetValue(TrackNoteProperty);

    public static void SetTrackNote(Control control, bool value) => control.SetValue(TrackNoteProperty, value);

    public static bool GetTrack(Control control) => control.GetValue(TrackProperty);

    public static void SetTrack(Control control, bool value) => control.SetValue(TrackProperty, value);
}
