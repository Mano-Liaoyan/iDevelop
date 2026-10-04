using System.Collections.Immutable;
using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// PlanWeave's floating run bar, for the run this window owns. It shows the attempt, not a card, so it stays reachable
/// when the running task is deleted or off screen.
/// </summary>
public sealed class ActiveRunViewModel : ObservableObject
{
    private readonly ProjectRuns _runs;
    private readonly ClientDirectory _clients;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RelayCommand _cancel;
    private AttemptRecord? _run;
    private int _others;

    internal ActiveRunViewModel(ProjectRuns runs, ClientDirectory clients)
    {
        _runs = runs;
        _clients = clients;
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(Elapsed));
        _cancel = new RelayCommand(() => _runs.Cancel(_run!.Task), () => _run is { Stopping: false });
    }

    public bool IsVisible => _run is not null;

    public string? TaskTitle => _run is null ? null : _others == 0 ? _run.TaskTitle : $"{_run.TaskTitle} and {_others} more";

    public string? AgentLabel => _run is { } run ? RunText.AgentLabel(run.Requested, _clients.Current[run.Requested.Client]) : null;

    public string? Elapsed => _run is { } run ? RunText.Elapsed(DateTimeOffset.UtcNow - run.RequestedAt) : null;

    public string? LastActivity => _run switch
    {
        null => null,
        { Stopping: true } => "Stopping…",
        { Activity: [.., var line] } => line.Text,
        _ => $"Waiting for {Clients.Name(_run.Requested.Client)}…",
    };

    public ICommand CancelCommand => _cancel;

    /// <summary>Called on the UI thread with the window's running attempts, oldest first. The bar shows the newest one,
    /// and its Cancel stops that one. Each card cancels its own task.</summary>
    internal void Show(ImmutableArray<AttemptRecord> active)
    {
        _run = active.IsEmpty ? null : active[^1];
        _others = Math.Max(0, active.Length - 1);
        _clock.IsEnabled = _run is not null;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(TaskTitle));
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(LastActivity));
        _cancel.NotifyCanExecuteChanged();
    }
}
