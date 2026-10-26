# A planner chooses each new task's agent

A planner, whether Generate Workflow placed it or the person placed a Plan or an Architect, proposes a client, a model, a reasoning level, and a one-line reason for each task it adds, chosen from the clients that are ready on the machine. A blueprint's own agent still comes first, as the [node model record](../handoffs/2026-10-04-node-model.md#planning-produces-graph-edits-the-person-approves) says, so the person's choices come with it. The owner asked for it on 2026-10-09 in issue [#91](https://github.com/Mano-Liaoyan/iDevelop/issues/91), after every task that Generate Workflow created got the same client and model, because the product direction asks Generate to draft "agents, models, and reasoning settings".

## How it works

- **The prompt lists what can run.** The planner reads each ready client, the models it can run now, and each model's levels, from the client catalog. The guidance on fitting a choice to a task names no model, so only the list follows the machine. A client still being checked when the planner starts is left out, and the review says so.
- **A blueprint's saved agent wins.** A task whose blueprint has an agent of its own takes that agent, and its review row says "Set by the blueprint". The planner chooses only for a task whose blueprint has none, and the prompt tells it to leave its choice out for a type with its own agent. The person can still change any task's agent in the review, and their choice wins over both. The owner decided this order on 2026-10-09: the person's choice, the blueprint's agent, the planner's choice, then the planner's own agent while the box is ticked.
- **A choice is checked, never trusted.** The review checks each choice against the clients as they are now, and again when they refresh.
- **A choice that cannot run falls back, visibly.** The task's ghost card and review row say why, and the task takes the agent it would have without a choice.
- **The person decides before anything joins the workflow.** Each ghost card and review row shows the task's agent and the planner's reason, the person can change any of them, and Accept writes exactly the agent each row shows.
- **"Fall back to the planner's agent"**, the box the owner confirmed on 2026-10-06, applies only to a new task that has no usable choice of its own.

## Considered options

- Letting the planner's choice override a blueprint's saved agent, which the first version of pull request #98 did. A blueprint's agent is the person's own choice, which the node model keeps.

## Consequences

- A fill keeps the agent of the task the person drew. A fill whose task has no agent takes the planner's choice, else the planner's agent while the box is ticked (issue [#99](https://github.com/Mano-Liaoyan/iDevelop/issues/99)). An Approval ignores a choice, since it takes no agent.
- A planner is design time. Its proposal edits the workflow document, which the person can edit at any time ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

Source: pull request [#98](https://github.com/Mano-Liaoyan/iDevelop/pull/98), its Decisions 1 to 6.
