# A Merge node resolves a run's conflicts, and a finished run merges into its branch

A run's finished work merges by itself into the branch the run started from, and a merge agent resolves the conflicts the run meets. The owner decided this on 2026-10-19 in issue [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137), in these words:

> merge automatically and an merge agent should take charge on solving conflict

The same day they chose these options in issue [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148):

- **Merge agent:** "A Merge node". A built-in Merge node that the person places and configures like any node, with its own client, model, effort and instructions.
- **Conflicts between predecessors:** "Merge agent". The same merge agent resolves them, and the successor starts once the merge is committed.
- **Uncommitted changes at the end-of-run merge:** "Wait and ask". The final merge waits, and the run's card asks the person to commit or stash. Their edits are never touched.
- **No Merge node:** "Ask me". The run waits, and the card asks the person to resolve the conflict, or to add a Merge node and run it.
- **Several Merge nodes:** "The connected one". A conflict goes to the Merge node that the conflicting branches connect into. The end-of-run merge uses the Merge node that has no successors.

## How it works

In Plan → Implement → Review → Approval, plus Plan → Docs → Review, with a Merge node given Claude Code, on `main`:

- **A Merge node is a node.** It has its own kind, color, and glyph, its client, model, effort, and instructions. Its agent runs only in a run, on a conflict that goes to it, so a Merge node's own Run and a message to it outside a run are refused with that reason.
- **A conflict between predecessors goes to the Merge node's agent.** When Implement's and Docs' commits conflict, iDevelop commits Git's merge with its conflict markers, and the Merge node's agent starts in its own worktree at that commit. Its prompt names the work that merges and the conflicting files. When the agent ends its turn, iDevelop commits its files on both commits as parents, and Review starts from that merge. A merge of the same two commits serves every node that needs it.
- **A Merge node between nodes merges them.** In Implement → Merge and Docs → Merge, then Merge → Review, the Merge node's own step merges Implement's and Docs' code and hands it on with their reports and artifacts, as an approval that needs no person does. Its agent runs only when that merge conflicts. A Merge node with no dependency predecessor never starts on its own.
- **The finished run merges into its branch.** Once every node the run can start is done, the Approval node included, the run merges its work into the branch HEAD named at its approval. The work is the code of the nodes' current results that no other result's code holds. When `main` has not moved, it fast-forwards. When it moved, a clean merge is a merge commit, and a conflict goes to the Merge node with no successors. The folder that has `main` checked out follows the merge, and the run then completes. A run approved on a detached HEAD has no branch, and nothing merges.
- **Uncommitted changes make the final merge wait.** While the folder that has `main` checked out has a change to a tracked file outside `.idp`, nothing moves, and the run's bar asks the person to commit or stash. The run looks again after its retry delay.
- **Without a Merge node, the person decides.** A conflict between predecessors blocks the successor with the conflicting paths, and its card asks the person to add a Merge node and run it. A conflict with the branch makes the run wait, and the run's bar asks the person to merge the run's work into `main` themselves or to add a Merge node and run it. The Run of a Merge node that the waiting run lacks adds it to the run, with the connections into it from the run's nodes, as the person's amendment, and the conflict goes to it. Once `main` holds the run's work, however it got there, the run completes.
- **A merge that is not finished waits for the person.** An attempt whose agent failed, or left a conflicting file with Git's markers, commits nothing. The Merge node fails, and its Retry starts again at the commit with the markers.
- **Choosing among Merge nodes.** A conflict between a Merge node's own predecessors goes to that node. Any other conflict between predecessors goes to the Merge node that every conflicting branch connects into, and the final merge to the Merge node with no successors, the first in task order when several fit. A workflow with one Merge node gives it every conflict. With several and none that fits, the conflict waits for the person.

## Considered options

- Leaving the run's work on a run branch for the person.
- Opening a pull request with the run's work.

Source: issues [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137) and [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148), and the owner's decisions of 2026-10-19.
