# Each editing node works in its own worktree, kept simple

In a run, each editing node's agent works in a Git worktree of its own, on a branch of its own. A turn ends with one commit of that worktree, and a node's worktree starts at the merge of its predecessors' commits. The owner decided this on 2026-10-19 in issue [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137), in these words:

> Use a separate worktree for each editing node, kept simple: create it, commit at the end of the turn, merge predecessors before a successor starts, ask on a conflict.

The same day they answered the follow-up questions:

- **Retry:** "Same worktree". A Retry of an editing node continues in the failed attempt's worktree with its files as they are.
- **Read-only nodes such as Review:** "Merged predecessors". Each works in a worktree at the merge of its predecessors, so Review sees Implement's edits, and it commits nothing.
- **A conflict between predecessors:** "Merge agent". A built-in Merge node resolves it, and the successor starts once the merge is committed. Issue [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148) builds the Merge node ([ADR 0033](0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).

## How it works

In Plan → Implement → Review → Approval, plus Plan → Docs → Review:

- **A worktree per node.** Each agent node of a run gets its worktree at `.worktrees/<run>/<node>` when its first attempt starts. Implement and Docs are editing nodes, so each also gets its branch, `idp/<run>/task/<node>`. Plan and Review are read-only, so their worktrees are detached and have no branch.
- **One commit at the end of a turn.** When Implement's client exits, iDevelop captures its worktree once, as one commit on the branch's tip. The commit of the attempt's last turn becomes the branch's tip and Implement's result. A read-only node's capture records its files and commits nothing.
- **The merge of predecessors.** Review starts once Implement and Docs have results. When they hand on different commits, `git merge-tree` merges them into one commit, and Review's worktree starts there, so it sees both nodes' edits. With one predecessor commit, the worktree starts at that commit.
- **A conflict goes to the Merge node.** If Implement's and Docs' commits conflict, a Merge node's agent resolves them, and Review starts once the merge is committed. Without a Merge node to take the conflict, Review is blocked with the conflicting paths ([ADR 0033](0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).
- **A Retry stays.** A Retry of Implement continues in Implement's worktree. A read-only node's later attempt resets its detached worktree to the merge it starts from, without the files an earlier attempt left, since a read-only node commits nothing.

## The repository lock stays

Creating a worktree, committing, merging predecessors, and moving a branch still change the repository, and a run starts every ready node at once ([ADR 0018](0018-every-ready-task-of-a-run-starts-at-once.md)). Git fails a ref update or a worktree registration that meets another writer's lock file. iDevelop's repository lock, `idevelop/mutation.lock` in the repository's common directory, makes those steps wait for each other instead, and a person's command still goes ahead of a run's scheduled steps (issue [#105](https://github.com/Mano-Liaoyan/iDevelop/issues/105)). Issue [#146](https://github.com/Mano-Liaoyan/iDevelop/issues/146) keeps it.

## Considered options

- Every node works in the project folder, and a file can be written by one editing node at a time, under a lock held for each write. The owner chose this first on 2026-10-19 and then replaced it with worktrees. Issue [#147](https://github.com/Mano-Liaoyan/iDevelop/issues/147), its design, closed as not planned.
- Every node works in the project folder, as a standalone run does. Nodes that run at once see each other's edits as they happen, and the last write to a file stays.
- Each node's change comes back to the project folder when it ends. That is a hand-off under another name.

Source: issues [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137), [#140](https://github.com/Mano-Liaoyan/iDevelop/issues/140) and [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148), and the owner's decisions of 2026-10-19.
