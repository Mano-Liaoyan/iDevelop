using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>How a connection stands out while nodes are selected: it touches one of them, or it recedes behind them.</summary>
public enum WireEmphasis { Normal, Highlighted, Dimmed }

public sealed class ConnectionViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly RelayCommand<ConnectionKind> _setKind;
    private ConnectionKind _kind;
    private ImmutableArray<Point> _ownRoute = [];
    private ImmutableArray<Point> _route = [];
    private IReadOnlyList<WirePiece> _curve = [];
    private (double Exit, double Entry) _slots;

    internal ConnectionViewModel(WorkflowCanvasViewModel canvas, ConnectionKey key, TaskNodeViewModel from, TaskNodeViewModel to, ConnectionKind kind)
    {
        _canvas = canvas;
        Key = key;
        From = from;
        To = to;
        _kind = kind;
        _setKind = new RelayCommand<ConnectionKind>(
            newKind => canvas.Edit(new WorkflowEdit.SetConnectionKind(Key, newKind)),
            newKind => newKind != Kind);
        DeleteCommand = new RelayCommand(() => canvas.Edit(new WorkflowEdit.Delete([], [Key])));
        from.PropertyChanged += OnSourceChanged;
        from.Output.PropertyChanged += OnEndMoved;
        to.Input.PropertyChanged += OnEndMoved;
        Reroute();
    }

    public ConnectionKey Key { get; }

    public TaskNodeViewModel From { get; }

    public TaskNodeViewModel To { get; }

    public ConnectionKind Kind => _kind;

    public WireEmphasis Emphasis
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnStrokeChanged();
                OnPropertyChanged(nameof(IsHighlighted));
                OnPropertyChanged(nameof(IsDimmed));
            }
        }
    }

    /// <summary>The wire touches a selected node.</summary>
    public bool IsHighlighted => Emphasis == WireEmphasis.Highlighted;

    /// <summary>Other nodes are selected, so the wire recedes behind their wires.</summary>
    public bool IsDimmed => Emphasis == WireEmphasis.Dimmed;

    /// <summary>What the wire carries now.</summary>
    public WireFlow Flow
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnStrokeChanged();
                OnPropertyChanged(nameof(Tip));
            }
        }
    } = WireFlow.Idle;

    /// <summary>The wire's stroke in canvas units. It shows a selection by weight, and it follows the zoom tier.</summary>
    public double Stroke => (WireFlows.Stroke(Flow) + (IsHighlighted ? 1 : 0)) * ZoomTiers.WireScale(_canvas.Tier);

    /// <summary>The stroke under the pointer and while the wire is selected.</summary>
    public double HotStroke => Math.Max(Stroke, 3 * ZoomTiers.WireScale(_canvas.Tier));

    /// <summary>What the wire says it carries, as its tooltip.</summary>
    public string Tip => From.HandsOn is { Length: > 0 } result ? $"{WireFlows.Tip(Flow, From.Title, To.Title)}\n{CardText.Line(result, 200)}" : WireFlows.Tip(Flow, From.Title, To.Title);

    /// <summary>Where the wire leaves its source card: its own point on the output side, under or over the port when the port has several wires.</summary>
    public Point SourcePoint => From.Output.Anchor + new Vector(0, _slots.Exit);

    /// <summary>Where the wire enters its target card: its own point on the input side.</summary>
    public Point TargetPoint => To.Input.Anchor + new Vector(0, _slots.Entry);

    /// <summary>The wire leaves its card at its own point, away from the port's socket, so it starts at the card's edge.</summary>
    public bool LeavesOwnPoint => _slots.Exit != 0;

    /// <summary>The wire enters its card at its own point, away from the port's socket, so it ends at the card's edge.</summary>
    public bool EntersOwnPoint => _slots.Entry != 0;

    /// <summary>The corners of the wire's plan, from the end of its source stub to the start of its target stub.</summary>
    public IReadOnlyList<Point> Route => _route;

    /// <summary>The curve the canvas draws along the plan.</summary>
    public IReadOnlyList<WirePiece> Curve => _curve;

    public ICommand SetKindCommand => _setKind;

    public ICommand DeleteCommand { get; }

    internal void Update(ConnectionKind kind)
    {
        if (SetProperty(ref _kind, kind, nameof(Kind)))
        {
            _setKind.NotifyCanExecuteChanged();
            ShowFlow();
            From.OnLinksChanged();
            To.OnLinksChanged();
        }
    }

    /// <summary>Finds the wire's path again, as any card may have moved into it or out of it.</summary>
    internal void Reroute()
    {
        // Every card stands where it shows and at the size it draws, so a wire goes around an expanded card (ADR 0024) and a
        // zoomed out card's last line. The
        // wire's own two cards stand where their ports are, which Nodify keeps current while a card is dragged.
        Rect[] cards =
        [
            .. _canvas.Nodes.Where(node => node != From && node != To).Select(node => node.DrawnRect),
            new(From.Output.Anchor - new Vector(From.CardSize.Width - WorkflowCanvasViewModel.InputPortCenter.X, WorkflowCanvasViewModel.InputPortCenter.Y), From.DrawnSize),
            new(To.Input.Anchor - (Vector)WorkflowCanvasViewModel.InputPortCenter, To.DrawnSize),
        ];
        var slots = _canvas.Slots(this);
        if (slots != _slots)
        {
            _slots = slots;
            OnPropertyChanged(nameof(SourcePoint));
            OnPropertyChanged(nameof(TargetPoint));
            OnPropertyChanged(nameof(LeavesOwnPoint));
            OnPropertyChanged(nameof(EntersOwnPoint));
        }

        var route = WireRouting.Route(SourcePoint, TargetPoint, cards, _canvas.CardReach);
        if (!route.SequenceEqual(_ownRoute))
        {
            _ownRoute = route;
            _canvas.SpreadWires();
        }
    }

    /// <summary>The route that this wire finds alone, before it takes a lane beside other wires.</summary>
    internal WireLanes.Wire Lane => new(_ownRoute, From.Location.Y + From.Location.X / 100000, To.Location.Y + To.Location.X / 100000);

    /// <summary>Takes the route with the wire's lane, and the curve along it.</summary>
    internal void ShowRoute(ImmutableArray<Point> route, IReadOnlyList<Rect> cards)
    {
        var curve = WireCurve.Of(SourcePoint, route, TargetPoint, cards);
        if (!route.SequenceEqual(_route))
        {
            _route = route;
            OnPropertyChanged(nameof(Route));
        }

        if (!curve.SequenceEqual(_curve))
        {
            _curve = curve;
            OnPropertyChanged(nameof(Curve));
        }
    }

    /// <summary>Reads what the wire carries from the two nodes' states and results.</summary>
    internal void ShowFlow() => Flow = WireFlows.Of(_kind, From.State, To.State, From.Id, From.LatestResult?.Id, To.LatestResult?.Basis);

    internal void OnStrokeChanged()
    {
        OnPropertyChanged(nameof(Stroke));
        OnPropertyChanged(nameof(HotStroke));
    }

    /// <summary>Called once the connection leaves the canvas, so its nodes no longer hold it.</summary>
    internal void Detach()
    {
        From.PropertyChanged -= OnSourceChanged;
        From.Output.PropertyChanged -= OnEndMoved;
        To.Input.PropertyChanged -= OnEndMoved;
    }

    private void OnEndMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PortViewModel.Anchor))
        {
            OnPropertyChanged(nameof(SourcePoint));
            OnPropertyChanged(nameof(TargetPoint));
            Reroute();
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.Title))
        {
            OnPropertyChanged(nameof(Tip));
        }
    }
}
