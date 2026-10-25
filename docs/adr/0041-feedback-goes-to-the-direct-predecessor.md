# Feedback goes to the direct predecessor

A review's reject and an approval's send back go along a feedback edge to the node's direct predecessor, or to each of its predecessors when it has several. Each continues in its own session and worktree with the findings or the note. The engine routes results along edges and nothing else, so a feedback edge leads to the same nodes as the edges into the reviewer or approver ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)). Map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) settled this on 2026-10-25.

## How it works

In Implement engine → Merge, Implement UI → Merge, then Merge → Review → Approve, the reviewer rejects with finding 1 at `src/Calculator.App/Views.fs:80`:

- **The Merge node gets the findings.** Review's direct predecessor is the Merge node, so its agent continues in its own session and worktree with finding 1, and Review reviews its new result.
- **The nodes behind it are not reached.** Implement engine and Implement UI keep their results, so the Merge node's result stays the one based on them.
- **The round limit holds.** Each reject counts as a round of Review ([ADR 0040](0040-after-three-review-rounds-the-reviewer-waits-for-the-person.md)).

## Considered options

- Sending each finding to the implementer behind a Merge node whose change touched the finding's file, which the owner asked for on 2026-10-25. It needs a routing rule that reads the changes, and it can return later as such a rule without changing the engine.

Source: map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179), settled with the owner on 2026-10-25.
