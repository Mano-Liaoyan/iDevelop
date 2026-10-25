# A PersonAgent approves or sends back

An Approval node is a `PersonAgent`: once its predecessors are completed, it is input-required until the person approves or sends back, and it runs no client. Approve completes the node with a result that forwards what the person saw. Send back is a feedback edge to its predecessors ([ADR 0035](0035-send-back-is-a-feedback-edge.md)). The owner settled the two node kinds in code, `CliAgent` and `PersonAgent`, on 2026-10-25 ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

- **Its basis is its inputs.** An Approval node has no model, so its basis is the latest results of its predecessors and its instruction. When one of them changes, its result goes stale, as any node's does, and it waits for the person again.
- **An approval forwards what the person saw.** Its report is each input's report under its node's title, in the order of its edges and unshortened. The commits its predecessors' results carry reach the nodes after it as if they came from its predecessors directly.
- **The answer is one entry.** Approve appends one result to the node's log, so no reader of results needs a second source of truth.

## Considered options

- A separate decision entry beside an ordinary result. A crash between the two would leave a decision without its result.

Source: pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74), its Decisions 1 and 2, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
