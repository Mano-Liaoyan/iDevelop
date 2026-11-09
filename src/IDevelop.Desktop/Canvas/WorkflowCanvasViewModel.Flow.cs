namespace IDevelop.Desktop.Canvas;

/// <summary>The Map's two views: Graph, the editable blueprint view, and Flow, a derived read-only layout.</summary>
public enum MapMode { Graph, Flow }

/// <summary>Which of the Map's two views shows, and what the person chose. The choice is view state on this machine (brief S2).</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>Flow's bands, cards, and wires.</summary>
    public FlowViewModel Flow { get; }

    public MapMode Mode
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsFlow));
                OnPropertyChanged(nameof(IsGraph));
                OnPropertyChanged(nameof(KeyHints));
                Flow.OnStagesChanged();
            }
        }
    }

    /// <summary>The view the person chose for this workflow, or null when no choice was made. The window saves it.</summary>
    public MapMode? ModeChoice
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>The Flow button of the footer.</summary>
    public bool IsFlow
    {
        get => Mode == MapMode.Flow;
        set
        {
            if (value)
            {
                Choose(MapMode.Flow);
            }
        }
    }

    /// <summary>The Graph button of the footer.</summary>
    public bool IsGraph
    {
        get => Mode == MapMode.Graph;
        set
        {
            if (value)
            {
                Choose(MapMode.Graph);
            }
        }
    }

    /// <summary>G: switches to the other view, as the person's choice.</summary>
    internal void ToggleMode() => Choose(IsFlow ? MapMode.Graph : MapMode.Flow);

    internal void Choose(MapMode mode)
    {
        ModeChoice = mode;
        Mode = mode;
    }

    /// <summary>Takes the choice that the last session left for this workflow.</summary>
    internal void RestoreMode(MapMode? saved)
    {
        if (saved is { } mode)
        {
            ModeChoice = mode;
            Mode = mode;
        }
    }

    /// <summary>The first view of a workflow that has no saved choice: Graph, unless a Graph fit at this Map's size would hide agent names. It is evaluated once.</summary>
    internal void ApplyDefaultMode(bool graphHidesNames)
    {
        if (ModeChoice is null)
        {
            Mode = graphHidesNames ? MapMode.Flow : MapMode.Graph;
        }
    }

    /// <summary>Whether Flow has moved its surface to the run front once.</summary>
    internal bool FlowOpened { get; set; }
}
