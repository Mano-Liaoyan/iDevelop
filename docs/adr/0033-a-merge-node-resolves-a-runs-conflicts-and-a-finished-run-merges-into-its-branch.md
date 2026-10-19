# A Merge node resolves a run's conflicts, and a finished run merges into its branch

A run's finished work merges by itself into the branch the run started from, and a merge agent resolves the conflicts the run meets. The owner decided this on 2026-10-19 in issue [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137), in these words:

> merge automatically and an merge agent should take charge on solving conflict

The same day they chose these options in issue [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148):

- **Merge agent:** "A Merge node". A built-in Merge node that the person places and configures like any node, with its own client, model, effort and instructions.
- **Conflicts between predecessors:** "Merge agent". The same merge agent resolves them, and the successor starts once the merge is committed.
- **Uncommitted changes at the end-of-run merge:** "Wait and ask". The final merge waits, and the run's card asks the person to commit or stash. Their edits are never touched.
- **No Merge node:** "Ask me". The run waits, and the card asks the person to resolve the conflict, or to add a Merge node and run it.
- **Several Merge nodes:** "The connected one". A conflict goes to the Merge node that the conflicting branches connect into. The end-of-run merge uses the Merge node that has no successors.

They answered the questions on pull request [#156](https://github.com/Mano-Liaoyan/iDevelop/pull/156) the same day:

- **A Merge node between nodes:** "Agent only on conflict".
- **"Ask me" for a conflict:** "Let me resolve it myself", with a way to open the conflicting worktree, fix the files, and mark it resolved.
- **Adding a Merge node and running it:** "Joins the waiting run".
- **A Merge node's failed attempt:** "Same worktree". Its Retry continues from the files the failed attempt left, as every other Retry does.
- **Ties between Merge nodes:** "First in task order".
- **The final merge's details:** "Accept all".
- **Planners proposing Merge nodes:** "Yes".
- **The Merge node's color:** "Keep pink".

## How it works

In Plan → Implement → Review → Approval, plus Plan → Docs → Review, with a Merge node given Claude Code, on `main`:

- **A Merge node is a node.** It has its own kind, color, and glyph, its client, model, effort, and instructions. Its agent runs only in a run, on a conflict that goes to it, so a Merge node's own Run and a message to it outside a run are refused with that reason.
- **A conflict between predecessors goes to the Merge node's agent.** When Implement's and Docs' commits conflict, iDevelop commits Git's merge with its conflict markers, and the Merge node's agent starts in its own worktree at that commit. Its prompt names the work that merges and the conflicting files. When the agent ends its turn, iDevelop commits its files on both commits as parents, and Review starts from that merge. A merge of the same two commits serves every node that needs it.
- **A Merge node between nodes merges them.** In Implement → Merge and Docs → Merge, then Merge → Review, the Merge node's own step merges Implement's and Docs' code and hands it on with their reports and artifacts, as an approval that needs no person does. Its agent runs only when that merge conflicts. A Merge node with no dependency predecessor never starts on its own.
- **The finished run merges into its branch.** Once every node the run can start is done, the Approval node included, the run merges its work into the branch HEAD named at its approval. The work is the code of the nodes' current results that no other result's code holds. When `main` has not moved, it fast-forwards. When it moved, a clean merge is a merge commit, and a conflict goes to the Merge node with no successors. The folder that has `main` checked out follows the merge, and the run then completes. A run approved on a detached HEAD has no branch, and nothing merges.
- **Uncommitted changes make the final merge wait.** While the folder that has `main` checked out has a change to a tracked file outside `.idp`, nothing moves, and the run's bar asks the person to commit or stash. A rebase or a bisect of `main` in any worktree makes it wait too, and the run's bar says the work merges once it finishes. The folder moves only while it still has `main` checked out at the commit the merge started from. The run looks again after its retry delay.
- **Without a Merge node, the person resolves the conflict.** A conflict between predecessors blocks the successor with the conflicting paths, and a conflict with the branch makes the run wait. The successor's card and inspector, or for the final merge the run's bar, offer Open worktree and Mark resolved. Open worktree adds the conflict's worktree at `.worktrees/<run>/conflict-<commit>`, detached at the commit with Git's markers, and opens it in the system's file manager. Mark resolved commits the person's files there on the conflict's two parents, and the run goes on exactly as after a Merge node's merge: Review starts from it, or the final merge uses it. Mark resolved is refused, with the files named, while a conflicting file still holds the markers, and before the worktree is open. A conflicting file that holds no markers, such as a binary file, a file one side deleted, or a renamed one, blocks nothing, and the conflict's detail, the notices, and a Merge node's prompt name it so the person or the agent checks it. The other way is to add a Merge node and run it: the Run of a Merge node that the waiting run lacks adds it to the run, with the connections into it from the run's nodes, as the person's amendment, and the conflict goes to it. Once `main` holds the run's work, however it got there, the run completes.
- **A merge that is not finished waits for the person.** An attempt whose agent failed, or left a conflicting file with Git's markers, commits nothing. The Merge node fails, and its Retry continues in the same worktree from the files the failed attempt left.
- **Choosing among Merge nodes.** A conflict between a Merge node's own predecessors goes to that node. Any other conflict between predecessors goes to the Merge node that every conflicting branch connects into, and the final merge to the Merge node with no successors, the first in task order when several fit. A workflow with one Merge node gives it every conflict. With several and none that fits, the conflict waits for the person. A Merge node the person runs then joins the run, and when the conflicting branches do not all connect into it either, the notice says so and names them.

## Considered options

- Leaving the run's work on a run branch for the person.
- Opening a pull request with the run's work.

Source: issues [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137) and [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148), and the owner's decisions of 2026-10-19.
