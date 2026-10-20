# A review after a Merge node reviews the merge, and its fixes go to the Merge node's agent

A Merge node counts as a review's subject, as an editing agent does, so Implement → Merge, Docs → Merge, then Merge → Review reviews the merged work, and the review's Changes Needed sends each fix round to the Merge node's agent. Before, a review whose only change-making predecessor was a Merge node had no subject, never started, and left the run in Needs attention with nothing to act on. The owner decided this on 2026-10-20 in issue [#163](https://github.com/Mano-Liaoyan/iDevelop/issues/163), choosing "Merge node's agent" over looking through the merge to the editors before it, and over a review that can only pass or fail. The same issue makes the run bar's Needs attention select the first task in the outline that keeps the run from going on.

## How it works

In A → Merge, B → Merge, then Merge → Review → Approval:

- **One subject.** A review still takes one subject, so a dependency from A into the same review is refused as a second subject.
- **What the reviewer reads.** The Merge node's ticket says it merges the work of the tasks before it, with its instructions. Its report is the one its merge handed on, with A's and B's reports, and its change runs from the base A and B started from to their merge.
- **The fix round.** The Merge node's agent starts in a fresh session, since its merge ran no agent, in the Merge node's own worktree on its task branch at the merge it handed on. It edits and commits as an editing agent does, and that commit becomes the Merge node's new result. Later rounds resume that session. The Merge node's own step still runs its agent only on a conflict ([ADR 0033](0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).

## Consequences

A Merge node whose agent already resolved a conflict between its own predecessors keeps that attempt's detached worktree, which takes no task branch. Its fix round is refused, its card shows the refusal, and the run needs attention, until a later change lets that worktree take the task branch.

Source: issue [#163](https://github.com/Mano-Liaoyan/iDevelop/issues/163) and the owner's decisions of 2026-10-20.
