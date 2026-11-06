using System.Collections.Immutable;
using System.Windows.Input;
using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Inspector;

/// <summary>How many of the workflow's nodes are in one state.</summary>
public sealed record StateCount(NodeState State, string Label, int Count)
{
    /// <summary>The state's glyph spins.</summary>
    public bool IsRunning => State == NodeState.Running;

    /// <summary>Selects the first node in the state, in the list's order.</summary>
    public required ICommand SelectCommand { get; init; }

    public string Tip => $"Select the first of the {Label.ToLowerInvariant()} nodes";
}

/// <summary>The workflow inspector's header and overview while nothing is selected: the workflow, its node count, its states, and its kinds.</summary>
public sealed class WorkflowOverviewViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private ImmutableArray<StateCount> _states = [];

    internal WorkflowOverviewViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        canvas.Nodes.CollectionChanged += (_, _) => Count();
        canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowCanvasViewModel.Name))
            {
                OnPropertyChanged(nameof(Name));
            }
            else if (e.PropertyName == nameof(WorkflowCanvasViewModel.Counts))
            {
                CountStates();
            }
        };
        Count();
    }

    public string Name => _canvas.Name;

    public string TaskCount => _canvas.Nodes.Count == 1 ? "1 node" : $"{_canvas.Nodes.Count} nodes";

    /// <summary>Each state that holds a node, the states that want the person's eye first.</summary>
    public IReadOnlyList<StateCount> States => _states;

    public bool HasStates => !_states.IsEmpty;

    /// <summary>The libraries, which the workflow inspector lists under the overview.</summary>
    public BlueprintsViewModel Blueprints => _canvas.Blueprints;

    private void SelectFirst(NodeState state)
    {
        if (_canvas.Outline.FirstOrDefault(node => node.State == state) is { } node)
        {
            _canvas.Inspect(node);
        }
    }

    private void Count()
    {
        OnPropertyChanged(nameof(TaskCount));
        CountStates();
    }

    private void CountStates()
    {
        var counts = _canvas.Counts;
        (NodeState State, string Label, int Count)[] all =
        [
            (NodeState.InputRequired, "Waiting", counts.Waiting), (NodeState.Failed, "Failed", counts.Failed), (NodeState.Running, "Running", counts.Running),
            (NodeState.Stale, "Stale", counts.Stale), (NodeState.Completed, "Completed", counts.Completed),
            (NodeState.Idle, "Idle", counts.Total - counts.Waiting - counts.Failed - counts.Running - counts.Stale - counts.Completed),
        ];
        ImmutableArray<StateCount> states =
        [
            .. all.Where(entry => entry.Count > 0).Select(entry => new StateCount(entry.State, entry.Label, entry.Count)
            {
                SelectCommand = new RelayCommand(() => SelectFirst(entry.State)),
            }),
        ];
        if (!states.Select(entry => (entry.State, entry.Count)).SequenceEqual(_states.Select(entry => (entry.State, entry.Count))))
        {
            _states = states;
            OnPropertyChanged(nameof(States));
            OnPropertyChanged(nameof(HasStates));
        }
    }
}
