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
    private WireEmphasis _emphasis;
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
        get => _emphasis;
        internal set => SetProperty(ref _emphasis, value);
    }

    /// <summary>The corners the wire turns at, from the end of its source stub to the start of its target stub.</summary>
    public IReadOnlyList<Point> Route => _route;

    public ICommand SetKindCommand => _setKind;

    public ICommand DeleteCommand { get; }

    internal void Update(ConnectionKind kind)
    {
        if (SetProperty(ref _kind, kind, nameof(Kind)))
        {
            _setKind.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Finds the wire's path again, as any card may have moved into it or out of it.</summary>
    internal void Reroute()
    {
        // The wire's own two cards stand where their ports are, which Nodify keeps current while a card is dragged.
        Rect[] cards =
        [
            .. _canvas.Nodes.Where(node => node != From && node != To).Select(node => WireRouting.Card(node.Location)),
            WireRouting.Card(From.Output.Anchor - (Vector)WorkflowCanvasViewModel.OutputPortCenter),
            WireRouting.Card(To.Input.Anchor - (Vector)WorkflowCanvasViewModel.InputPortCenter),
        ];
        var route = WireRouting.Route(From.Output.Anchor, To.Input.Anchor, cards);
        if (!route.SequenceEqual(_route))
        {
            _route = route;
            OnPropertyChanged(nameof(Route));
        }
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
    }
}
