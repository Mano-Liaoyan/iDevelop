using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private readonly Dictionary<(WorkflowId Workflow, RunId Run), WorkflowRunCoordinator> _coordinators = [];
    private readonly Lock _opening = new();

    /// <summary>How long a coordinator waits before it retries a step refused for a busy lock or journal. Tests shorten it.</summary>
    internal TimeSpan CoordinatorRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The coordinator of an approved run in this window, opened once per run. It takes the run's control, which fences
    /// any execution a lost owner left unresolved. While another window controls the run, it only reads it. Opening
    /// launches nothing: <see cref="WorkflowRunCoordinator.Resume"/> authorizes scheduling.
    /// </summary>
    internal RunOpen OpenRun(WorkflowId workflow, RunId run)
    {
        // Two opens in one window would race for the run's control, and the loser's read-only coordinator could be kept.
        lock (_opening) return OpenRunCore(workflow, run);
    }

    private RunOpen OpenRunCore(WorkflowId workflow, RunId run)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            if (_coordinators.TryGetValue((workflow, run), out var open)) return new RunOpen.Opened(open);
        }
        var store = TurnStore;
        CoordinatorPermit? permit;
        switch (store.TakeControl(workflow, run))
        {
            case ControlTake.Owned owned:
                permit = owned.Permit;
                break;
            case ControlTake.Busy:
                permit = null;
                break;
            case ControlTake.Rejected rejected:
                return new RunOpen.Rejected(rejected.Reason);
            default:
                throw new InvalidOperationException();
        }
        WorkflowRunCoordinator coordinator;
        try { coordinator = new(this, store, () => TurnMaterializer(store), new(ProjectFolders.OnDisk(_projectFolder), workflow, run), permit); }
        catch (InvalidOperationException)
        {
            permit?.Dispose();
            return new RunOpen.Rejected(new(RunProblem.StorageUnavailable));
        }
        lock (_gate)
        {
            if (_leaving is null && !_coordinators.ContainsKey((workflow, run)))
            {
                _coordinators.Add((workflow, run), coordinator);
                return new RunOpen.Opened(coordinator);
            }
        }
        _ = coordinator.DisposeAsync();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            return new RunOpen.Opened(_coordinators[(workflow, run)]);
        }
    }

    /// <summary>Halts each coordinator before the project's turns leave, and lets each give up its permit after they left.</summary>
    private Task LeaveCoordinators(Task leaving)
    {
        WorkflowRunCoordinator[] coordinators = [.. _coordinators.Values];
        if (coordinators.Length == 0) return leaving;
        foreach (var coordinator in coordinators) coordinator.Halt();
        return After(leaving, coordinators);

        static async Task After(Task leaving, WorkflowRunCoordinator[] coordinators)
        {
            try { await leaving.ConfigureAwait(false); }
            finally { await Task.WhenAll(coordinators.Select(coordinator => coordinator.DisposeAsync())).ConfigureAwait(false); }
        }
    }
}
