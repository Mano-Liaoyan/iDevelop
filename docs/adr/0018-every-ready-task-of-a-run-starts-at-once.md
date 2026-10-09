# Every ready task of a run starts at once

A workflow run starts every task that is ready at once, each with its own client root. This supersedes [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s rule "Bring-up uses one starting or running client root per run", the run's one client slot that [ADR 0004](0004-one-window-controls-a-run-and-only-its-resume-schedules.md), [ADR 0010](0010-a-reply-is-saved-at-once-and-runs-when-the-slot-is-free.md), and [ADR 0013](0013-an-interrupted-fix-waits-for-the-person-s-choice.md) described, and open issue 8 of the [workflow execution design](../handoffs/2026-10-07-workflow-execution-design.md#open-issues), which asked for a probe the user authorizes before more than one root ran. The owner decided this on 2026-10-09 in issue [#89](https://github.com/Mano-Liaoyan/iDevelop/issues/89), after a fan-out ran its tasks one after another, because the [product direction](../product-direction.md) asks to run independent work concurrently.

## How it works

- **One decision starts everything that can start.** Each of the coordinator's decisions starts every resting attempt's next turn that has text for its agent, then every ready task, then every review's next reviewer turn or fix round, each in task order. So a reply to a waiting task, a fix round the person chose, and a review's next turn start beside the tasks that already run.
- **A large safety bound, and no setting.** `WorkflowRunCoordinator.ClientRootLimit`, 32 client roots per run, bounds what one run starts or runs at once. A workflow a person draws stays far below it. Only at the bound does the order of starts matter: the first ones in that order start, and each root exit frees a slot for the next. Design v5's "Add no single-value selector" still holds.
- **A slot belongs to one task.** A task's starting and running turn holds one slot, its root exit frees it before cleanup and capture, and waiting, gates, settlement, publication, and cleanup hold none. A task takes one turn at a time, and a review's subject takes one fix round at a time. Stop Workflow cancels every running turn. Reopening reconciles every unclosed claim and launches none of them again.
- **Steps wait their turn for the repository's lock.** Preparations, claims, publications, rebases, and joins still take the repository's mutation lock one at a time where they share Git state. A run's steps wait up to 30 seconds for it, instead of being refused as busy after one second, so tasks that start or finish together take it in turn. The lock order stays coordinator permit, task lease, repository mutation lock, then journal write lock, and every wait is bounded, so the waits cannot deadlock. Other callers keep the one-second wait.
- **The run bar counts what runs.** While several tasks start, run, or finish, the run's pill reads "3 running", and its activity line names them by what they do, as in Running "A" and "B" · Starting "C". Each card shows its own task's state.

## Considered options

- A concurrency setting per run or per project. Design v5 rules out a single-value selector, and the owner asked for everything ready to start.
- No bound at all. A wide fan-out or a planner's amendment could start dozens of clients at once.
- The busy refusal and retry of the repository lock as before. Tasks that start or finish together contend for the lock at the same moment, and each refusal showed a task as unable to start until the retry a second later.

## Consequences

- Several clients now run at the same time in sibling checkouts. The design's open issue 3 widens: a client that writes outside its own checkout can change a sibling's checkout while that sibling runs, and captures cannot tell who wrote a stable change.
- Several clients of one provider may run at once under one account and meet its rate limits. No real client has run this way yet, and such a check needs the owner's approval.
- A step that waits for the repository lock can hold its start for up to 30 seconds before it is refused and tried again.

Source: issue [#89](https://github.com/Mano-Liaoyan/iDevelop/issues/89), decided by the owner on 2026-10-09.
