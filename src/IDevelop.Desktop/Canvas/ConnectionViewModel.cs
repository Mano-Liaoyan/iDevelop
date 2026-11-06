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
    private IReadOnlyList<Point> _route = [];

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

    /// <summary>The kind of the node the connection leaves, whose hue the wire takes.</summary>
    public NodeKind SourceKind => From.Kind;

    public WireEmphasis Emphasis
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnStrokeChanged();
            }
        }
    }

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
    public double Stroke => (WireFlows.Stroke(Flow) + (Emphasis == WireEmphasis.Highlighted ? 1 : 0)) * ZoomTiers.WireScale(_canvas.Tier);

    /// <summary>The stroke under the pointer and while the wire is selected.</summary>
    public double HotStroke => Math.Max(Stroke, 3 * ZoomTiers.WireScale(_canvas.Tier));

    /// <summary>What the wire says it carries, as its tooltip.</summary>
    public string Tip => WireFlows.Tip(Flow, From.Title, To.Title);

    /// <summary>The corners the wire turns at, from the end of its source stub to the start of its target stub.</summary>
    public IReadOnlyList<Point> Route => _route;

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
        // Every card stands where it shows and at the size it shows, so a wire goes around an expanded card (ADR 0024). The
        // wire's own two cards stand where their ports are, which Nodify keeps current while a card is dragged.
        Rect[] cards =
        [
            .. _canvas.Nodes.Where(node => node != From && node != To).Select(node => node.DisplayRect),
            new(From.Output.Anchor - new Vector(From.CardSize.Width - WorkflowCanvasViewModel.InputPortCenter.X, WorkflowCanvasViewModel.InputPortCenter.Y), From.CardSize),
            new(To.Input.Anchor - (Vector)WorkflowCanvasViewModel.InputPortCenter, To.CardSize),
        ];
        var route = WireRouting.Route(From.Output.Anchor, To.Input.Anchor, cards);
        if (!route.SequenceEqual(_ownRoute))
        {
            _ownRoute = route;
            _canvas.SpreadWires();
        }
    }

    /// <summary>The route that this wire finds alone, before it takes a lane beside other wires.</summary>
    internal WireLanes.Wire Lane => new(_ownRoute, From, To, From.Location.Y + From.Location.X / 100000);

    /// <summary>Takes the route with the wire's lane.</summary>
    internal void ShowRoute(ImmutableArray<Point> route)
    {
        if (!route.SequenceEqual(_route))
        {
            _route = route;
            OnPropertyChanged(nameof(Route));
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
            Reroute();
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskNodeViewModel.Kind))
        {
            OnPropertyChanged(nameof(SourceKind));
        }
        else if (e.PropertyName == nameof(TaskNodeViewModel.Title))
        {
            OnPropertyChanged(nameof(Tip));
        }
    }
}
