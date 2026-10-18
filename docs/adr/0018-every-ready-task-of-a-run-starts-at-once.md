# Every ready task of a run starts at once

A workflow run starts every task that is ready at once, each with its own client root. This supersedes [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s rule "Bring-up uses one starting or running client root per run", the run's one client slot that [ADR 0004](0004-one-window-controls-a-run-and-only-its-resume-schedules.md), [ADR 0010](0010-a-reply-is-saved-at-once-and-runs-when-the-slot-is-free.md), and [ADR 0013](0013-an-interrupted-fix-waits-for-the-person-s-choice.md) described, and open issue 8 of the [workflow execution design](../handoffs/2026-10-07-workflow-execution-design.md#open-issues), which asked for a probe the user authorizes before more than one root ran. The owner decided this on 2026-10-09 in issue [#89](https://github.com/Mano-Liaoyan/iDevelop/issues/89), after a fan-out ran its tasks one after another, because the [product direction](../product-direction.md) asks to run independent work concurrently.

## How it works

- **One decision starts everything that can start.** Each of the coordinator's decisions starts every resting attempt's next turn that has text for its agent, then every ready task, then every review's next reviewer turn or fix round, each in task order. So a reply to a waiting task, a fix round the person chose, and a review's next turn start beside the tasks that already run.
- **A large safety bound, and no setting.** `WorkflowRunCoordinator.ClientRootLimit`, 32 client roots per run, bounds what one run starts or runs at once. A workflow a person draws stays far below it. Only at the bound does the order of starts matter: the first ones in that order start, and each root exit frees a slot for the next. Design v5's "Add no single-value selector" still holds.
- **A slot belongs to one task.** A task's starting and running turn holds one slot, its root exit frees it before cleanup, and waiting, gates, settlement, and cleanup hold none. A task takes one turn at a time, and a review's subject takes one fix round at a time. Stop Workflow cancels every running turn. Reopening reconciles every unclosed claim and launches none of them again.
- **The run bar counts what runs.** The run's pill counts only the tasks whose clients run, as in "3 running", and reads "Running" for one. While none runs, it reads "Starting" or "Finishing" when a task's client starts or finishes. The activity line names the busy tasks by what they do, as in Running "A" and "B" · Starting "C", and wraps only between titles, on the run bar and on the conversation's run strip. A card that waits for several tasks reads "Waits for 3 tasks", and its inspector names them. Each card shows its own task's state.

## Considered options

- A concurrency setting per run or per project. Design v5 rules out a single-value selector, and the owner asked for everything ready to start.
- No bound at all. A wide fan-out or a planner's amendment could start dozens of clients at once.

## Consequences

- Several clients of one provider may run at once under one account and meet its rate limits. No real client has run this way yet, and such a check needs the owner's approval.

Source: issue [#89](https://github.com/Mano-Liaoyan/iDevelop/issues/89), decided by the owner on 2026-10-09, and pull request [#96](https://github.com/Mano-Liaoyan/iDevelop/pull/96), its Decisions 1 to 5.
