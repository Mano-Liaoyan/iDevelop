# A run approval includes a planner's report in its own journal entry

Run Workflow can take the report that a read-only root planner produced on its own before the run, and the run then never runs that planner. The inclusion is checked inside the approval's transaction and recorded in the `Approved` event itself, so no crash can separate approving the run from including its reports. This is how E3d.2 builds [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s `IncludePlannerReport`: it is `ReportReuse.ValidatePlanner` plus `RunStore.Approve` with the confirmation's inclusions, and no command runs after the approval.

## How it works

- **The person opts in per report.** The preflight lists each read-only root planner's newest standalone attempt, and the other reusable reports of E3d.1, as unchecked boxes. A report that cannot be included says why and cannot be checked. The confirmation names each inclusion by task, source attempt, and turn.
- **The intent records the inclusions.** The approval intent of [ADR 0006](0006-run-approval-records-an-intent-before-the-journal.md) records them before any side effect, and an intent matches a confirmation only with the same inclusions. A pending intent with other inclusions is replaced.
- **The confirmation finishes a waiting Chat planner.** After the intent and before the approval's journal entry, a planner that waits for the person is marked done at the turn the preview showed. A reply after the preview makes the preview stale, and the confirmation then finishes nothing. The intent covers a crash between Mark done and the approval.
- **What can be included.** The attempt must be a fresh standalone attempt of the approved definition and settings, on a planner with no input. Its final turn must hold a readable proposal, and a proposal in an earlier turn does not count. It must not be a Continue of an earlier attempt, and it must end in strict success, with no terminal handoff or unsent queued text. The approved workflow must already hold the part of the proposal the person accepted, so that accepting that part again changes nothing. Anything else is `ReuseUnverifiable`.
- **What counts as accepted.** The proposed nodes the approved workflow holds, plus the fills whose title and fields are in place. The person may edit an accepted node afterwards, but an edited fill counts as not accepted. Proposal types resolve through the approved revision's blueprints, then the built-ins, so an unaccepted node of a library blueprint the workflow does not use makes the proposal unreadable.
- **What an included result carries.** Its origin is `Included`, with the source log's checkpoint, the turn, the definition, a digest of the proposal and its accepted part, and the confirmation. Its inputs are empty. It imports no client session, and an initial start of the task is `StartConflict`. An ordinary reusable report keeps E1's validator and the `Reused` origin, and counts as its single turn.
- **A failed inclusion approves nothing.** The approval returns `InclusionRefused`, naming the problem, with the refreshed preview. Running the planner in the run takes a confirmation without the inclusion, which is design v5's explicit choice to run it again.

## Considered options

- A separate `IncludePlannerReport` command after the approval, as design v5 named it. A crash between the two would leave an approved run without the inclusion the person confirmed, and the run would need a second recovery path.
- Approving the run without a failed inclusion. The planner would then run again in the run without the person choosing that.

Source: pull request [#78](https://github.com/Mano-Liaoyan/iDevelop/pull/78) (E3d.2), its Decisions 1 to 7, and pull request [#80](https://github.com/Mano-Liaoyan/iDevelop/pull/80) (E3g.1), its Decision 9.
