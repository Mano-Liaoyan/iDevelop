# Stop confirms unstarted work, and a run never settles Failed

Stop Workflow is the confirmation that E1's `Recovered(NotStarted)` closure needs for an attempt that never started, and a run settles only `Completed` or `Stopped`. A run that can go no further stays approved and shows "Needs attention", because [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422) offers Retry or Stop Workflow there and adds no Finish action.

## How a stop ends each kind of work

- **Attempts that never started.** A reserved attempt that was never claimed closes as `Recovered(NotStarted)`, with the stop command's operation as its confirmation and the reason "The workflow was stopped before this task started." Once the run is stopping, a start that is refused or blocked sets no hold, so the stop still closes its reserved attempt.
- **Work that already won finishes its way.** A claim that won before the stop settles as `Cancelled`. A turn that is settling successfully may still publish, as design v5 allows. A waiting attempt closes as `Cancelled` without sending queued text, and an open gate request shows "Closed" and refuses its answers. An uncertain claim keeps the run "Stopping" until a person recovers it (E3g.2).
- **Closing the project is not a stop.** It lets the interrupted turns' dispositions run, bounded by the leave timeout, so their attempts close `Interrupted`. Reopening relaunches none of them.
- **The app asks before it closes a run's project.** When the window controls an active run, closing the run's project or the window asks "Stop it and leave?" first. Stop and leave records Stop Workflow before the project closes, so a reopen settles the run as `Stopped`, and Keep running closes nothing. A run that another window controls is not asked about.
- **No Failed outcome.** `RunOutcome.Failed` stays unused. Only Stop settles a run that can go no further, as `Stopped`.

## Consequences

- No ticket yet owns manual Retry or cancelling a single node, so today Stop Workflow is the only way out of "Needs attention".
- The app offers no way to close a project and leave its run for later. A run waits `Paused` for Resume only after iDevelop ended without asking, as after a crash.

Source: pull request [#71](https://github.com/Mano-Liaoyan/iDevelop/pull/71) (E3b), its Decisions 5, 6, 7, and 11, pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74) (E3e), its Decision 9 on a stopped run's gates, and pull request [#80](https://github.com/Mano-Liaoyan/iDevelop/pull/80) (E3g.1), its Decision 7.
