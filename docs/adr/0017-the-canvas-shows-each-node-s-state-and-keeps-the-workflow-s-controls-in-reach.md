# The canvas shows each node's state and keeps the workflow's controls in reach

Each card shows its node's state from the engine, and the controls that act on the workflow stay in reach on the canvas and over a node's conversation. Run Workflow sits beside Generate and needs no selected node. A floating bar at the bottom center, PlanWeave's run bar, shows what the nodes do now, with Stop All, and Merge into my branch while it is offered. While a node's conversation covers the canvas, a strip under the conversation's header repeats the bar's status and its buttons. The engine has no run of its own to show, so every control acts on nodes ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

- **No new visual language.** Each engine state, Idle, Running, Input required, Completed, Failed, or Stale, maps onto an existing card state and pill tone, and the card names it. Input required names why: a question, an approval, a conflict to resolve, an interrupted turn, or a review that reached its round limit.
- **A node's own controls.** A card, its menu, and the inspector offer the node's Run while the engine's `CanRun` allows it, and otherwise say why. They offer Stop while it runs, Retry once it has ended, and the person's answer while it waits.
- **The bar counts what goes on.** The bar's pill counts the nodes that run and the nodes that wait for the person. Stop All stops every running node. Merge into my branch appears only while every node is completed and nothing is stale ([ADR 0033](0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)).
- **The strip over the conversation** reuses the conversation's banner style and the bar's pill. It hides in the dock layout, where the canvas's own bar is in view.
- **Switching stops nothing.** A project keeps its workflows' nodes running while it is open. Project and workflow rows show the running dot while a node's client works, and a waiting dot while a node waits for the person.

## Considered options

- Floating the bar above the conversation. It would cover the conversation's composer.

Source: pull request [#80](https://github.com/Mano-Liaoyan/iDevelop/pull/80), its Decisions 2, 3, 8, and 10, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
