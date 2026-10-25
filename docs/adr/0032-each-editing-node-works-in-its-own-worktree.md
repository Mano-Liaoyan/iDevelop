# Each editing node works in its own worktree

Each editing node's agent works in a Git worktree of its own, on its own branch, `idp/<workflow>/<node>`. A turn ends with one commit of that worktree. A node's worktree starts at the merge of its predecessors' commits, and a root node's at the HEAD of the project's branch. A Retry starts in a clean worktree. The owner decided on 2026-10-19 in issue [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137), in these words:

> Use a separate worktree for each editing node, kept simple: create it, commit at the end of the turn, merge predecessors before a successor starts, ask on a conflict.

On 2026-10-25 map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) named the branch after the workflow and the node and made a Retry start clean ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

In Plan → Implement → Review → Approval, plus Plan → Docs → Review:

- **A worktree per node.** Implement and Docs are editing nodes, so each gets its worktree at `.worktrees/<workflow>/<node>` in the project folder and its branch. Plan and Review are read-only, so their worktrees are detached at the merge of their predecessors and commit nothing.
- **One commit at the end of a turn.** When Implement's client exits, iDevelop captures its worktree once, as one commit on the branch's tip. The commit of the node's last turn is part of its result.
- **The merge of predecessors.** Review starts once Implement and Docs have completed. When they hand on different commits, `git merge-tree` merges them into one commit, and Review's worktree starts there, so it sees both nodes' edits. With one predecessor commit, the worktree starts at that commit. A conflict goes to a connected Merge node, or the node waits for the person ([ADR 0033](0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)).
- **A continued session keeps its worktree.** A person's message and a feedback edge continue the node's session in its worktree, with its files as they are.
- **A fresh session starts clean.** A Retry, and a rerun after an upstream change, reset the worktree to the merge of the predecessors' latest commits, without the files an earlier session left.

## The repository lock stays

Creating a worktree, committing, merging predecessors, and moving a branch change the repository, and every ready node starts at once ([ADR 0018](0018-every-ready-node-starts-at-once.md)). Git fails a ref update or a worktree registration that meets another writer's lock file. iDevelop's repository lock, `idevelop/mutation.lock` in the repository's common directory, makes those steps wait for each other instead (issue [#105](https://github.com/Mano-Liaoyan/iDevelop/issues/105)).

## Considered options

- Every node works in the project folder, and a file can be written by one editing node at a time, under a lock held for each write. The owner chose this first on 2026-10-19 and then replaced it with worktrees. Issue [#147](https://github.com/Mano-Liaoyan/iDevelop/issues/147), its design, closed as not planned.
- Every node works in the project folder. Nodes that run at once see each other's edits as they happen, and the last write to a file stays.
- A Retry that continues in its worktree with the earlier change. Its output would then depend on more than its basis.

Source: issues [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137) and [#140](https://github.com/Mano-Liaoyan/iDevelop/issues/140), the owner's decisions of 2026-10-19, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
