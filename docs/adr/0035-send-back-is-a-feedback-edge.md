# Send back is a feedback edge

An Approval node's Send back takes a note and sends it along a feedback edge to the node's predecessors. Each continues in its own session and worktree with the note as its next message, and once they complete, the Approval node waits for the person again on their new results. The owner decided on 2026-10-12 in issue [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83) that a send back carries the person's reason to the agent. Map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) made it a feedback edge, the same route a review's reject takes ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

In Plan → Implement → Review → Approval, the person sends Approval back with `Handle empty input.`:

- **The note reaches the predecessor.** Review is Approval's predecessor, so Review's session continues with the note. Its reviewer reviews again and, when it rejects, sends its findings on to Implement ([ADR 0041](0041-feedback-goes-to-the-direct-predecessor.md)).
- **The approver runs again.** Once Review completes again, Approval is input-required on the new result, and the person approves or sends back again.
- **Send back records the note.** The note is part of the Approval node's history, beside the results it answered.

Source: issues [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83) and [#126](https://github.com/Mano-Liaoyan/iDevelop/issues/126), the owner's decisions of 2026-10-12, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
