# One window controls a run, and only its Resume schedules

Design v5 gives one window control of a run through the coordinator permit, and says that reopening a run launches nothing until Resume run authorizes scheduling. E3b settled how a window gets control and how Resume works.

- **Opening takes control.** `ProjectRuns.OpenRun` takes the run's permit, which fences whatever a lost owner left unresolved. While another window controls the run, it returns a read-only coordinator, whose commands return `Unavailable` with "This run is controlled by another iDevelop window." A read-only coordinator never takes control by itself. Once the controlling window closes, the next `OpenRun` in another window returns a new coordinator that controls the run.
- **Resume is per window and in memory.** Opening never schedules. Resume does, for a fresh run and after a reopen alike, and it is not journaled. A crash or restart leaves the run `Paused` until the person resumes it in the window that now controls it. Run Workflow's start command approves, opens, and resumes in one step.
- **Answers need no Resume.** An Approval node's answer records a decision and launches nothing, so a paused run accepts it, and the dependents start once the person resumes. A new gate request is scheduling and waits for Resume.
- **Dispatch is deterministic.** Ready tasks start in task ID order, one client root per run. A reserved attempt that was never claimed is ready like a task without one, and it resumes with its own cause and operation.
- **Only busy refusals retry by themselves.** A step refused because a task lock, the journal, storage, or the run is busy, or one whose command faulted, retries after one second. Every other refusal waits for Resume, which clears the window's holds that the journal does not record.

## Considered options

- A journaled Resume. A run would start work again after a crash or restart without the person, which design v5's "Reopening launches nothing" rules out.

## Consequences

- A read-only coordinator stays read-only after control frees, so the run UI (E3g.1) has to open the run again to take control.

Source: pull request [#71](https://github.com/Mano-Liaoyan/iDevelop/pull/71) (E3b), its Decisions 1 to 4, pull request [#73](https://github.com/Mano-Liaoyan/iDevelop/pull/73) (E3d.1), its Decision 8, and pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74) (E3e), its Decision 6.
