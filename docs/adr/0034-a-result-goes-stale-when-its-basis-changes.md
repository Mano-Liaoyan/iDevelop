# A result goes stale when its basis changes, and an upstream rerun runs the nodes after it again

A node's result is stale when the basis it recorded, its input result ids, its instruction text, and its model, differs from the node's current basis. Staleness is a pure function of the node's log and the workflow, so nothing is cascaded or stored. When an upstream node runs again, the nodes after it run again by themselves in a fresh session once they are ready. When the person edits a node, it waits for the person's Run. The owner decided on 2026-10-12 and 2026-10-19 in issue [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83) that a done task offers Retry, with a warning that the tasks after it go stale, and that those tasks run again by themselves once all their predecessors are complete. Map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) settled the rest ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

In Architect → Implement → Review:

- **A completed node offers Retry.** The person retries Architect from its card, its card menu, or the inspector, and the confirmation says that the nodes after it go stale and run again once it has a new result. Architect starts a fresh session in a clean worktree ([ADR 0032](0032-each-editing-node-works-in-its-own-worktree.md)).
- **The nodes after it run again by themselves.** Once Architect completes, Implement's recorded input no longer names Architect's latest result, so Implement's result is stale and Implement is ready. It runs again in a fresh session that is given only the latest upstream results. Review follows once Implement completes. An Approval node waits for the person again.
- **An upstream rerun stops a running node.** When Architect runs again while Implement runs, the engine stops Implement's turn and marks it stale. Once Architect completes, Implement runs again by itself in a fresh session.
- **A person's edit stops a running node and waits.** When the person changes Implement's instruction, its model, or its edges while it runs, the engine stops it and marks it stale. Implement then waits for the person's Run, and the nodes after it wait with it.
- **A stale result hands on nothing.** A node whose predecessor's result is stale is not ready, and Merge into my branch is not offered while any result is stale ([ADR 0033](0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)).

## Considered options

- Storing a stale flag and cascading it to the nodes after a change. The flag would need its own consistency rules, while a comparison of bases always gives the same answer.

Source: issue [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83), the owner's decisions of 2026-10-12 and 2026-10-19, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
