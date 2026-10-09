# A node's Run starts a run that advances only when every predecessor is complete

A node's Run starts a workflow run of that node, with the same preflight, approval, Stop, and crash recovery as Run Workflow. When a node of the run finishes, each node directly after it starts once all of that node's dependency predecessors are complete, and it waits until then. Nothing starts merely because it comes after another node. The owner decided this on 2026-10-09 in issue [#90](https://github.com/Mano-Liaoyan/iDevelop/issues/90), in their words "当它结束的时候，并且下游的前序所有节点都完成的时候，才自动跑下一个": the next node runs automatically only when the node ends and every predecessor of that next node is complete.

## How it works

- **A node's Run is a run.** The inspector's Run, the card menu's Run, and its shortcut open the preflight titled `Run "Parse input"`, which lists, checks, and offers only that node and the tasks after it. The approval records the node in the journal's `approved` event as `node`. The run starts that node in its own checkout, and Stop Workflow, Resume, and reconciliation after a crash work as in Run Workflow ([ADR 0004](0004-one-window-controls-a-run-and-only-its-resume-schedules.md)). The preflight offers an earlier report only for the node itself, so a planner's own Run can include its plan, and the tasks it planned then start. Generate Workflow's planner still runs on its own in the project folder.
- **The run advances only through complete predecessors.** A task may start when the person ran it, or when a dependency leads to it from such a task, and only once each of its dependency predecessors has a current, non-stale result in the run. Successors that become ready together start together ([ADR 0018](0018-every-ready-task-of-a-run-starts-at-once.md)). The journal refuses to reserve a task, or to ask an Approval node for an answer, outside these tasks. A waiting task names the predecessors it waits for itself, in Run Workflow too.
- **A Run during an active run joins it.** The Run of a node that the active run has not taken in records a `requested` event in that run's journal, once per confirmation, under the run's approval and with its approved definition. A repeat records nothing more, Stop ends further requests, and a reopened run starts the node only after Resume. Example `A → C ← B`: the person runs A, then runs B while A runs, and C starts once the last of the two finishes.
- **A run completes when nothing more can start.** Every task is then done, or nothing in the run can start it any more: nobody ran it, or a predecessor of it can no longer start. The journal accepts `Completed` only then. A failed, blocked, or stale task still keeps the run open, and the run bar says `Every task it ran has a current result.` when the run never ran some tasks.
- **A node whose predecessors are not complete starts nothing.** Its Run says why before any click and in the notice after it: `Runs after "A". Run "A" first.`, or `Runs after "A" and "B". Run them first.` for several. Core refuses the same case: the preflight shows it as a gap, and a request names the predecessor with `MissingDependencyResult`.
- **Run Workflow carries nothing.** It runs every root and every task after them, as before, and its journal records no node.

## Considered options

- Run the node and everything downstream of it. The owner rejected it: nothing starts merely because it is downstream.
- Keep a run open while a successor waits for a predecessor nobody ran, so a later Run of that predecessor joins it. The coordinator chose to complete the run once nothing more can start; the owner's rule for results of earlier runs covers the later Run instead.
- A second preflight for a Run during an active run. The run's approval already covers its approved snapshot and base.
- Keep the standalone Run on a node. The owner decided that a node's Run works like Run Workflow, with a preflight and its own checkout.

## Consequences

- Until results carry from one run to the next, "complete" means complete in the active run. After the person runs A of `A → C ← B` alone, the run completes, and a later Run of B starts a new run in which C does not start, because A's result belongs to the earlier run. The second pull request of #90 carries earlier current results into a new run, under the owner's rule that a result counts while it is still current and its changes apply cleanly on today's code, and keeps each card's state between runs.
- A node's Run no longer edits the project folder; its code lands in the run's checkouts.
- An older build refuses every node run's journal, not only one with a `requested` event: its journal reader allows no unknown member, so the `node` of the `approved` event already fails it closed. It also ignores an approval intent that names a node. Older journals read as before.

Source: issue [#90](https://github.com/Mano-Liaoyan/iDevelop/issues/90), decided by the owner and the coordinator on 2026-10-09, and pull request [#102](https://github.com/Mano-Liaoyan/iDevelop/pull/102), its Decisions 1 to 8.
