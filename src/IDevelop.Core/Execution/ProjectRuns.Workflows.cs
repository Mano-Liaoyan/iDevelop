using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private readonly Dictionary<(WorkflowId Workflow, RunId Run), WorkflowRunCoordinator> _coordinators = [];

    /// <summary>Serializes <see cref="OpenRun"/>. Taken before <see cref="_gate"/>, never while holding it.</summary>
    private readonly Lock _opening = new();

    /// <summary>How long a coordinator waits before it retries a step refused for a busy lock or journal. Tests shorten it.</summary>
    internal TimeSpan CoordinatorRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many client roots one run starts or runs at once in this window, <see cref="WorkflowRunCoordinator.ClientRootLimit"/>.
    /// Tests lower it to see the order of starts.
    /// </summary>
    internal int ClientRoots { get; set; } = WorkflowRunCoordinator.ClientRootLimit;

    /// <summary>
    /// How long a run's own scheduled work in this window waits for the repository's mutation lock. A run's tasks prepare,
    /// claim, and publish at the same time, and each retries the lock rather than being refused as busy. A person's command
    /// keeps <see cref="GitRepository.MutationPatience"/>.
    /// </summary>
    internal TimeSpan MutationPatience { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The coordinator of an approved run in this window. It takes the run's control, which fences any execution a lost
    /// owner left unresolved. While another window controls the run, it only reads it; opening the run again once that
    /// control is free returns a new coordinator that controls it. Opens of one run in a window are serialized, so they
    /// share one coordinator. Opening launches nothing: <see cref="WorkflowRunCoordinator.Resume"/> authorizes scheduling.
    /// </summary>
    internal RunOpen OpenRun(WorkflowId workflow, RunId run)
    {
        lock (_opening)
        {
            WorkflowRunCoordinator? cached;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_leaving is not null, this);
                cached = _coordinators.GetValueOrDefault((workflow, run));
            }
            if (cached is { Controlled: true }) return new RunOpen.Opened(cached);
            var store = TurnStore;
            CoordinatorPermit? permit;
            switch (store.TakeControl(workflow, run))
            {
                case ControlTake.Owned owned:
                    permit = owned.Permit;
                    break;
                case ControlTake.Busy when cached is not null:
                    return new RunOpen.Opened(cached);
                case ControlTake.Busy:
                    permit = null;
                    break;
                case ControlTake.Rejected rejected:
                    return new RunOpen.Rejected(rejected.Reason);
                default:
                    throw new InvalidOperationException();
            }
            WorkflowRunCoordinator coordinator;
            try { coordinator = new(this, store, (patience, halted) => TurnMaterializer(store, patience, halted), new(ProjectFolders.OnDisk(_projectFolder), workflow, run), permit); }
            catch (InvalidOperationException)
            {
                permit?.Dispose();
                return new RunOpen.Rejected(new(RunProblem.StorageUnavailable));
            }
            bool leaving;
            lock (_gate)
            {
                leaving = _leaving is not null;
                if (!leaving) _coordinators[(workflow, run)] = coordinator;
            }
            if (leaving)
            {
                _ = coordinator.DisposeAsync();
                throw new ObjectDisposedException(GetType().FullName);
            }
            // The read-only coordinator this one replaces only read the run.
            _ = cached?.DisposeAsync();
            return new RunOpen.Opened(coordinator);
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
