# Every ready node starts at once

The engine starts every node that is ready at once, each with its own client process. A node is ready when all its predecessors are completed and none of their results is stale. When a node completes, its result goes along every outgoing edge, and each node after it that is now ready starts. The owner decided on 2026-10-09 in issue [#89](https://github.com/Mano-Liaoyan/iDevelop/issues/89), after a fan-out ran its tasks one after another, that everything ready starts, because the [product direction](../product-direction.md) asks to run independent work concurrently. Map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) keeps it ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

In Plan → Implement → Review, plus Plan → Docs → Review:

- **Run starts the node and what follows.** A node's Run starts it, and once it completes, Implement and Docs are ready and start together. Review starts once both have completed. Run Workflow starts every node that is not completed or stale, each as soon as it is ready, in dependency order.
- **A node takes one turn at a time.** A node that waits for the person holds no client process.
- **The bar counts what runs.** The bar names the busy nodes by what they do, and a card that waits for several nodes says how many, with its inspector naming them.

## Considered options

- A concurrency setting per workflow or per project. The owner asked for everything ready to start.

## Consequences

- Several clients of one provider may run at once under one account and meet its rate limits.

Source: issue [#89](https://github.com/Mano-Liaoyan/iDevelop/issues/89), decided by the owner on 2026-10-09, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
