using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Desktop.Theme;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>One wire as Flow draws it: a polyline in the surface's coordinates with what it carries and how it stands out.</summary>
public sealed record FlowWire(ImmutableArray<Point> Points, ConnectionKind Kind, WireFlow Flow, WireEmphasis Emphasis);

/// <summary>A stage's band in Flow: its header's place, its words, and its fold.</summary>
public sealed class FlowBandViewModel(WorkflowStage stage) : ObservableObject
{
    public WorkflowStage Stage { get; } = stage;

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    /// <summary>"6 of 8 done".</summary>
    public string Summary => $"{Stage.Counts.Completed} of {Stage.Counts.Total} done";

    public bool IsFolded => Stage.IsFolded;

    public NodeState? Rollup => Stage.Counts.Rollup;

    public WaitReason RollupWhy => NodeStates.CommonReason(Stage.Members);

    public bool HasRollup => Rollup is not null;

    internal void Place(Rect header)
    {
        (X, Y, Width) = (header.X, header.Y, header.Width);
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
    }

    internal void ShowCounts()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsFolded));
        OnPropertyChanged(nameof(Rollup));
        OnPropertyChanged(nameof(RollupWhy));
        OnPropertyChanged(nameof(HasRollup));
    }
}

/// <summary>One agent's card or chip in Flow: its place, its sockets, and whether it recedes while another node has the wires.</summary>
public sealed class FlowCardViewModel(TaskNodeViewModel node) : ObservableObject
{
    public TaskNodeViewModel Node { get; } = node;

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    public bool IsChip
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(SocketTop));
                OnPropertyChanged(nameof(SocketBottom));
            }
        }
    }

    public bool IsDimmed
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>What the input socket shows: nothing while no wire joins it, else the wire's color.</summary>
    internal WireFlow? Input
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsInputConnected));
                OnPropertyChanged(nameof(IsInputFeeding));
                OnPropertyChanged(nameof(IsInputStopped));
            }
        }
    }

    /// <summary>What the output socket shows.</summary>
    internal WireFlow? Output
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsOutputConnected));
                OnPropertyChanged(nameof(IsOutputFeeding));
                OnPropertyChanged(nameof(IsOutputStopped));
            }
        }
    }

    public bool IsInputConnected => Input is not null;

    public bool IsInputFeeding => Input == WireFlow.Feeding;

    public bool IsInputStopped => Input == WireFlow.Stopped;

    public bool IsOutputConnected => Output is not null;

    public bool IsOutputFeeding => Output == WireFlow.Feeding;

    public bool IsOutputStopped => Output == WireFlow.Stopped;

    /// <summary>The input socket's margin: half the socket stands past the card's top edge.</summary>
    public Thickness SocketTop => new(0, IsChip ? -3 : -4, 0, 0);

    /// <summary>The output socket's margin: half the socket stands past the card's bottom edge.</summary>
    public Thickness SocketBottom => new(0, 0, 0, IsChip ? -3 : -4);

    /// <summary>The input socket's center, in the surface's coordinates.</summary>
    public Point InputPoint => new(X + Width / 2, Y);

    /// <summary>The output socket's center, in the surface's coordinates.</summary>
    public Point OutputPoint => new(X + Width / 2, Y + Height);

    internal Rect Bounds => new(X, Y, Width, Height);

    internal void Place(Rect rect, bool chip)
    {
        (X, Y, Width, Height) = (rect.X, rect.Y, rect.Width, rect.Height);
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        IsChip = chip;
    }
}

/// <summary>
/// Flow, the Map's derived view: agents in rows by graph depth. It reads the workflow's stages and never writes a position (brief S2).
/// The band folds are the stages' own, which the Index reads too.
/// </summary>
public sealed class FlowViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly Dictionary<TaskNodeViewModel, FlowCardViewModel> _cards = [];
    private readonly Dictionary<WorkflowStage, FlowBandViewModel> _bands = [];
    private double _width;
    private FlowDensity? _densityChoice;
    private TaskNodeViewModel? _hovered;
    private bool _holding;

    internal FlowViewModel(WorkflowCanvasViewModel canvas) => _canvas = canvas;

    /// <summary>The canvas whose workflow this Flow shows.</summary>
    internal WorkflowCanvasViewModel Canvas => _canvas;

    public ObservableCollection<FlowBandViewModel> Bands { get; } = [];

    /// <summary>The cards of the open bands, in stage order, so a screen reader and Tab read them as the eye does.</summary>
    public ObservableCollection<FlowCardViewModel> Cards { get; } = [];

    public FlowDensity Density => _densityChoice ?? FlowLayout.DefaultDensity(_canvas.Nodes.Count);

    public bool IsChip => Density == FlowDensity.Chip;

    /// <summary>The footer's minus button makes the cards smaller, and it stops at Chip.</summary>
    public bool CanStepDown => !IsChip;

    /// <summary>The footer's plus button makes the cards larger, and it stops at Card.</summary>
    public bool CanStepUp => IsChip;

    public double SurfaceHeight
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public IReadOnlyList<FlowWire> Wires
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>The wires into running agents, drawn in their own moving layer, so only that layer redraws each frame.</summary>
    public IReadOnlyList<FlowWire> MovingWires
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasMovingWires));
            }
        }
    } = [];

    public bool HasMovingWires => MovingWires.Count > 0;

    internal FlowPlan? Plan { get; private set; }

    internal void Measure(double width)
    {
        if (width > 0 && Math.Abs(width - _width) >= 0.5)
        {
            _width = width;
            Arrange();
        }
    }

    /// <summary>Makes the cards one step smaller or larger, as the person's own choice for this workflow.</summary>
    internal void StepDensity(int step) => ChooseDensity(step < 0 ? FlowDensity.Chip : FlowDensity.Card);

    internal void ChooseDensity(FlowDensity density)
    {
        if (density != Density || _densityChoice is null)
        {
            _densityChoice = density;
            ShowDensity();
        }
    }

    private void ShowDensity()
    {
        OnPropertyChanged(nameof(Density));
        OnPropertyChanged(nameof(IsChip));
        OnPropertyChanged(nameof(CanStepDown));
        OnPropertyChanged(nameof(CanStepUp));
        Arrange();
    }

    /// <summary>F: the density that shows the most stages from the run front.</summary>
    internal void Fit(double viewHeight)
    {
        if (_width > 0 && _canvas.Stages.Count > 0)
        {
            ChooseDensity(FlowLayout.DensityToFit(_width, viewHeight, StagesNow(), RunFront()));
        }
    }

    /// <summary>The first stage that is not all Done, else the first stage.</summary>
    internal int RunFront()
    {
        for (var index = 0; index < _canvas.Stages.Count; index++)
        {
            if (_canvas.Stages[index].Counts.Completed < _canvas.Stages[index].Counts.Total)
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>The stages' folds and states changed, or the workflow's shape did.</summary>
    internal void OnStagesChanged()
    {
        foreach (var gone in _bands.Keys.Except(_canvas.Stages).ToList())
        {
            _bands.Remove(gone);
        }

        foreach (var stage in _canvas.Stages.Where(stage => !_bands.ContainsKey(stage)))
        {
            _bands.Add(stage, new FlowBandViewModel(stage));
            stage.PropertyChanged += OnStageChanged;
        }

        OnPropertyChanged(nameof(Density));
        OnPropertyChanged(nameof(IsChip));
        OnPropertyChanged(nameof(CanStepDown));
        OnPropertyChanged(nameof(CanStepUp));
        Arrange();
    }

    private void OnStageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_holding && e.PropertyName == nameof(WorkflowStage.IsFolded) && sender is WorkflowStage stage && _bands.ContainsKey(stage))
        {
            Arrange();
        }
    }

    /// <summary>Runs <paramref name="update"/>, whose stages change one by one, and arranges only once afterward.</summary>
    internal void Hold(Action update)
    {
        _holding = true;
        try
        {
            update();
        }
        finally
        {
            _holding = false;
        }

        OnStagesChanged();
    }

    /// <summary>The selection or the pointer changed, so the wires and the dimmed cards show the new node.</summary>
    internal void ShowActive() => ShowWires();

    internal void Hover(TaskNodeViewModel? node)
    {
        if (node != _hovered)
        {
            _hovered = node;
            ShowWires();
        }
    }

    /// <summary>The card of <paramref name="node"/>, or null while its stage folds.</summary>
    internal FlowCardViewModel? CardOf(TaskNodeViewModel node) => _cards.GetValueOrDefault(node) is { } card && Cards.Contains(card) ? card : null;

    /// <summary>The stage of <paramref name="node"/>, or null.</summary>
    internal WorkflowStage? StageOf(TaskNodeViewModel node) => _canvas.Stages.FirstOrDefault(stage => stage.Holds(node));

    internal FlowBandViewModel? BandOf(WorkflowStage stage) => _bands.GetValueOrDefault(stage);

    /// <summary>The node a key moves the selection to from the selected node, or the first card when nothing is selected.</summary>
    internal TaskNodeViewModel? Neighbor(TaskNodeViewModel? node, FlowMove move)
    {
        if (Plan is not { } plan)
        {
            return null;
        }

        if (node is null || StageOf(node) is not { } stage || !stage.Members.Contains(node) || CardOf(node) is null)
        {
            return Cards.FirstOrDefault()?.Node;
        }

        var at = _canvas.Stages.IndexOf(stage);
        return FlowLayout.Neighbor(plan, at, stage.Members.ToList().IndexOf(node), move) is { } next
            ? _canvas.Stages[next.Stage].Members[next.Index]
            : null;
    }

    private FlowStage[] StagesNow() => [.. _canvas.Stages.Select(stage => new FlowStage(stage.Members.Count, stage.IsFolded))];

    private void Arrange()
    {
        var stages = _canvas.Stages;
        WorkflowCanvasViewModel.Arrange(Bands, [.. stages.Select(stage => _bands[stage])], null);
        foreach (var band in Bands)
        {
            band.ShowCounts();
        }

        if (_width <= 0 || !_canvas.IsFlow)
        {
            return;
        }

        var plan = FlowLayout.Place(_width, Density, StagesNow());
        Plan = plan;
        var shown = new List<FlowCardViewModel>();
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var band = plan.Bands[index];
            _bands[stage].Place(band.Header);
            for (var at = 0; at < band.Cards.Length; at++)
            {
                var node = stage.Members[at];
                if (!_cards.TryGetValue(node, out var card))
                {
                    card = new FlowCardViewModel(node);
                    _cards.Add(node, card);
                }

                card.Place(band.Cards[at], Density == FlowDensity.Chip);
                shown.Add(card);
            }
        }

        WorkflowCanvasViewModel.Arrange(Cards, shown, null);
        foreach (var gone in _cards.Keys.Where(node => !_canvas.Nodes.Contains(node)).ToList())
        {
            _cards.Remove(gone);
        }

        SurfaceHeight = plan.Height;
        ShowWires();
    }

    // Only wires of an active node are drawn when a stage wraps, and the other cards dim then. A folded stage is a wire end.
    private void ShowWires()
    {
        if (Plan is not { } plan)
        {
            Wires = [];
            MovingWires = [];
            return;
        }

        var active = _canvas.SelectedNodes.Select(node => node.Id).ToHashSet();
        if (_hovered is { } hovered)
        {
            active.Add(hovered.Id);
        }

        var rects = Cards.Select(card => card.Bounds).ToList();
        var inputs = new Dictionary<FlowCardViewModel, WireFlow>();
        var outputs = new Dictionary<FlowCardViewModel, WireFlow>();
        var wires = new List<FlowWire>();
        var seen = new HashSet<(Point, Point)>();
        // Each stage-to-stage gap has its own lanes, one for each output, so outputs that share a gap keep apart.
        var lanes = new Dictionary<(int Stage, double Source), int>();
        foreach (var connection in _canvas.Connections)
        {
            var from = CardOf(connection.From);
            var to = CardOf(connection.To);
            if (from is not null)
            {
                outputs[from] = Stronger(outputs.GetValueOrDefault(from, WireFlow.Idle), connection.Flow);
            }

            if (to is not null)
            {
                inputs[to] = Stronger(inputs.GetValueOrDefault(to, WireFlow.Idle), connection.Flow);
            }

            var drawn = !plan.Grid.Wraps || active.Contains(connection.Key.From) || active.Contains(connection.Key.To);
            if (!drawn)
            {
                continue;
            }

            var source = from?.OutputPoint ?? BandEnd(connection.From, bottom: true);
            var target = to?.InputPoint ?? BandEnd(connection.To, bottom: false);
            if (source is not { } start || target is not { } end || !seen.Add((start, end)))
            {
                continue;
            }

            var stage = StageOf(connection.To) is { } targetStage ? _canvas.Stages.IndexOf(targetStage) : 0;
            var lane = lanes.TryGetValue((stage, start.X), out var known) ? known : lanes[(stage, start.X)] = lanes.Keys.Count(key => key.Stage == stage);
            var emphasis = active.Count == 0 ? WireEmphasis.Normal
                : active.Contains(connection.Key.From) || active.Contains(connection.Key.To) ? WireEmphasis.Highlighted
                : WireEmphasis.Dimmed;
            wires.Add(new FlowWire(FlowLayout.WireRoute(start, end, rects, lane % 3 * 3), connection.Kind, connection.Flow, emphasis));
        }

        foreach (var card in Cards)
        {
            card.Input = inputs.TryGetValue(card, out var input) ? input : null;
            card.Output = outputs.TryGetValue(card, out var output) ? output : null;
            card.IsDimmed = plan.Grid.Wraps && active.Count > 0 && !active.Contains(card.Node.Id)
                && !_canvas.Connections.Any(wire => (wire.From == card.Node && active.Contains(wire.Key.To)) || (wire.To == card.Node && active.Contains(wire.Key.From)));
        }

        // Stronger wires draw last, so a highlighted or carrying wire stays whole over the ones it shares a lane with. A wire into a
        // running agent moves, in its own layer.
        Wires = [.. wires.Where(wire => wire.Flow != WireFlow.Feeding).OrderBy(wire => wire.Emphasis == WireEmphasis.Dimmed ? 0 : wire.Emphasis == WireEmphasis.Normal ? 1 : 2).ThenBy(wire => wire.Flow == WireFlow.Stopped ? 1 : 0)];
        MovingWires = [.. wires.Where(wire => wire.Flow == WireFlow.Feeding)];
    }

    private Point? BandEnd(TaskNodeViewModel node, bool bottom)
    {
        if (StageOf(node) is not { } stage || !_bands.TryGetValue(stage, out var band))
        {
            return null;
        }

        return new Point(band.X + band.Width / 2, band.Y + (bottom ? FlowLayout.HeaderHeight : 0));
    }

    private static WireFlow Stronger(WireFlow current, WireFlow next) => WireFlows.Stronger(current, next);
}
