# A review after a Merge node reviews the merge

A Merge node counts as a review's subject, as an editing agent does, so Implement → Merge, Docs → Merge, then Merge → Review reviews the merged work. Before, a review whose only change-making predecessor was a Merge node had no subject, never started, and left the run in Needs attention with nothing to act on. The owner decided this on 2026-10-20 in issue [#163](https://github.com/Mano-Liaoyan/iDevelop/issues/163), over a review that can only pass or fail. Its fix rounds go to the implementers behind the merge ([ADR 0041](0041-a-review-after-a-merge-node-sends-each-finding-to-the-implementer-whose-change-touched-its-file.md)). The same issue makes the run bar's Needs attention select the first task in the outline that keeps the run from going on.

## How it works

In A → Merge, B → Merge, then Merge → Review → Approval:

- **One subject.** A review still takes one subject, so a dependency from A into the same review is refused as a second subject.
- **What the reviewer reads.** The Merge node's ticket says it merges the work of the tasks before it, with its instructions. Its report is the one its merge handed on, with A's and B's reports, and its change runs from the base A and B started from to their merge.

Source: issue [#163](https://github.com/Mano-Liaoyan/iDevelop/issues/163) and the owner's decisions of 2026-10-20.
