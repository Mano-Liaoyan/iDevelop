# A review after a Merge node reviews the merge

A Merge node counts as a review's subject, as an editing agent does, so Implement → Merge, Docs → Merge, then Merge → Review reviews the merged work. The owner decided this on 2026-10-20 in issue [#163](https://github.com/Mano-Liaoyan/iDevelop/issues/163), over a review that can only pass or fail.

## How it works

In A → Merge, B → Merge, then Merge → Review → Approval:

- **One subject.** A review takes one subject, so a dependency from A into the same review is refused as a second subject.
- **What the reviewer reads.** The Merge node's instruction says it merges the work of the nodes before it. Its report and its commit are its result, and the change the reviewer reads runs from the merge of A's and B's starting points to the Merge node's commit.
- **A reject goes to the Merge node.** The review's feedback edge leads to its direct predecessor, the Merge node, whose agent continues in its own session and worktree ([ADR 0041](0041-feedback-goes-to-the-direct-predecessor.md)).

Source: issue [#163](https://github.com/Mano-Liaoyan/iDevelop/issues/163), the owner's decisions of 2026-10-20, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
