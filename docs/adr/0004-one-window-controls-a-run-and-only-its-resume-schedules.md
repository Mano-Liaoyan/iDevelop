# One window controls a run, and only its Resume schedules

One window controls a run: opening the run takes its coordinator permit, and every other window gets a read-only coordinator whose commands return `Unavailable`. Nothing is scheduled until that window's Resume, which lives in memory and is not journaled, so after a crash or restart the run stays `Paused` until the person resumes it. This applies [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s rule that reopening a run restores its questions and gates but launches nothing until Resume run, to a fresh run as well.

## How it works

- **Opening takes control.** `ProjectRuns.OpenRun` takes the run's permit, which fences whatever a lost owner left unresolved. While another window controls the run, it returns a read-only coordinator, whose commands return `Unavailable` with "This run is controlled by another iDevelop window." A read-only coordinator never takes control by itself. Once the controlling window closes, the next `OpenRun` in another window returns a new coordinator that controls the run.
- **Resume is per window.** Opening never schedules. Resume does, for a fresh run and after a reopen alike, in the window that controls the run. Run Workflow's start command approves, opens, and resumes in one step.
- **Answers need no Resume.** An Approval node's answer records a decision and launches nothing, so a paused run accepts it, and the dependents start once the person resumes. A new gate request is scheduling and waits for Resume.
- **Dispatch is deterministic.** While the run's one client slot is free, a resting attempt's next turn goes first, in task order ([ADR 0010](0010-a-reply-is-saved-at-once-and-runs-when-the-slot-is-free.md)). Otherwise the first ready task in task ID order starts. A reserved attempt that was never claimed is ready like a task without one, and it resumes with its own cause and operation.
- **Only busy refusals are tried again by themselves.** A step refused because a task lock, the journal, storage, or the run is busy, or one whose command faulted, runs again after one second. Every other refusal waits for Resume, which clears the window's holds that the journal does not record.

## Considered options

- A journaled Resume. A run would start work again after a crash or restart without the person, and design v5 says reopening launches nothing.

## Consequences

- A read-only coordinator stays read-only after control frees, so the run UI (E3g.1) has to open the run again to take control.

Source: pull request [#71](https://github.com/Mano-Liaoyan/iDevelop/pull/71) (E3b), its Decisions 1 to 4, pull request [#73](https://github.com/Mano-Liaoyan/iDevelop/pull/73) (E3d.1), its Decision 8, pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74) (E3e), its Decision 6, and pull request [#76](https://github.com/Mano-Liaoyan/iDevelop/pull/76) (E3c.1), its Decision 3.
