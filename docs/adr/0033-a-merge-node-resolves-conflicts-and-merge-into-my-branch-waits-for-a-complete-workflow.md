# A Merge node resolves conflicts, and Merge into my branch waits for a complete workflow

A conflict between a node's predecessors goes to a connected Merge node, a blueprint whose agent resolves it. Without one, the node waits for the person to resolve the conflict in its worktree and mark it resolved. The workflow's work reaches the person's branch only through Merge into my branch, which is offered once every node is completed and nothing is stale. The owner decided on 2026-10-19 in issues [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137) and [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148) that "an merge agent should take charge on solving conflict", as "A Merge node" that the person places and configures like any node, and that without one the person resolves the conflict themselves, with a way to open the conflicting worktree, fix the files, and mark it resolved. Map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) made Merge a blueprint and the merge into the person's branch the person's action ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

In Implement → Merge, Docs → Merge, then Merge → Review, on `main`:

- **A Merge node is a blueprint.** It is a prompt template and an output format that `CliAgent` runs, with its own client, model, effort, instructions, color, and glyph. Its color stays pink.
- **A conflict goes to the Merge node it connects into.** When Implement's and Docs' commits conflict, the Merge node's worktree starts at Git's merge with the conflict markers, and its agent's prompt names the work that merges and the conflicting files. When the agent ends its turn, iDevelop commits its files on both commits as parents, and Review starts from that merge.
- **Without a Merge node, the person resolves it.** In Implement → Review, Docs → Review, a conflict makes Review input-required with the conflicting paths. Its card and inspector offer Open worktree, which opens Review's worktree, at Git's merge with its markers, in the system's file manager, and Mark resolved, which commits the person's files there on the conflict's parents. Mark resolved is refused while a conflicting file still holds the markers, and names the files. A conflicting file that holds no markers, such as a binary file, a file one side deleted, or a renamed one, blocks nothing, and the conflict's detail and a Merge node's prompt name it so the person or the agent checks it.
- **Merge into my branch.** Once every node is completed and nothing is stale, the person may merge every end node's commit, the commits of the nodes with no successors, into the checked-out branch. When the end nodes conflict with each other, it refuses and tells the person to add a Merge node after them. When the branch moved since the nodes started and the merge conflicts, it refuses and names the conflicting paths. The engine never merges into the person's branch by itself.

## Considered options

- Merging the finished work into the branch by itself. The person's branch changes only when the person asks.
- Leaving the work on the nodes' branches for the person.
- Opening a pull request with the work.

Source: issues [#137](https://github.com/Mano-Liaoyan/iDevelop/issues/137) and [#148](https://github.com/Mano-Liaoyan/iDevelop/issues/148), pull request [#156](https://github.com/Mano-Liaoyan/iDevelop/pull/156), the owner's decisions of 2026-10-19, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
