# A Retry merges new inputs into its earlier change, and an out-of-date task runs again by itself

A done task offers Retry, and a task whose result went out of date runs again by itself once every task before it is done. An editing node's Retry continues in its worktree with its earlier change, and the new work of the tasks before it merges in before its agent starts. The owner decided this on 2026-10-12 and 2026-10-19 in issue [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83), in these words:

- **What a Retry of a code-writing task starts from:** "Same, merge new inputs". The node continues in its own worktree with its earlier change, and its predecessors' new commits are merged in before its agent starts. A conflict in that merge is resolved "Like any conflict".
- **Which tasks offer Retry:** also a finished (Done) task, with a warning that the tasks after it go out of date. If the redo fails, the task shows Failed, because the newest attempt decides.
- **The finished tasks after a retried task:** they run again by themselves once all their predecessors are complete, and the Approval node asks again. This follows [ADR 0020](0020-a-node-s-run-starts-a-run-that-advances-only-when-every-predecessor-is-complete.md) and overrides design v5's "no automatic stale reruns". An editing successor that runs again by itself works "Same as a Retry".

## How it works

In Architect → Implement → Review, with Architect's design in `docs/design.md` and Implement's commit of `src/Parser.cs` on top of it:

- **A done task offers Retry.** The person retries Architect from its card, its card menu, or the inspector, and the confirmation says that the tasks after it go out of date and run again once it has a new result. Architect continues in its worktree, and its new design supersedes the old one, so Implement's and Review's results go out of date.
- **An out-of-date task runs again by itself.** Implement runs again as a Retry of its newest attempt once Architect is done, under a confirmation every window derives from that attempt. Review waits for Implement, and runs again once Implement has its new result. An Approval node asks again. A rerun that fails shows Failed, and the person retries it.
- **The new work merges into the earlier change.** Implement's Retry takes Architect's new commit and its own current result's commit, and `git merge-tree` merges them. Its branch moves to that merge, and the files its worktree holds stay, so its agent starts with `src/Parser.cs` and the new `docs/design.md`. While the work of the tasks before it is the work its result was built from, a Retry starts at its result's commit. Without a result, it starts at the new work, with its failed attempt's files.
- **A conflict goes where any conflict goes.** The Merge node's agent resolves it, or without one the person uses Open worktree and Mark resolved on Implement's card. Implement starts once the merge is committed ([ADR 0033](0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).
- **A read-only task retries on today's inputs.** Review's worktree starts at the merge of its predecessors' current commits ([ADR 0032](0032-each-editing-node-works-in-its-own-worktree-kept-simple.md)).
- **An unstarted attempt prepares again.** When Implement's attempt is reserved and waits for a client slot while Architect hands on a newer result, that attempt closes as not started, `Its inputs changed before it started.`, and Implement starts again with the new inputs, as a Retry does.

Source: issues [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83) and [#125](https://github.com/Mano-Liaoyan/iDevelop/issues/125), and the owner's decisions of 2026-10-12 and 2026-10-19.
