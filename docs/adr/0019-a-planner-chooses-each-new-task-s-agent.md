# A planner chooses each new task's agent

A planner that the app starts, whether Generate Workflow placed it or the person placed a Plan or an Architect, now proposes a client, a model, a reasoning level, and a one-line reason for each task it adds, chosen from the clients that are ready on the machine. This supersedes the [node model record](../handoffs/2026-10-04-node-model.md#planning-produces-graph-edits-the-person-approves) at its line 120, which says a planner "never chooses agents", and at its line 218, which kept "Planners write slot and type handles and never choose agents" from design A. The owner asked for it on 2026-10-09 in issue [#91](https://github.com/Mano-Liaoyan/iDevelop/issues/91), after every task that Generate Workflow created got the same client and model, because the product direction asks Generate to draft "agents, models, and reasoning settings".

## How it works

- **The prompt lists what can run.** The planner reads each ready client, the models it can run now, and each model's levels, from the client catalog. The guidance on fitting a choice to a task names no model, so only the list follows the machine. A client still being checked when the planner starts is left out, and the review says so.
- **A choice is checked, never trusted.** The review checks each choice against the clients as they are now, and again when they refresh. Nothing about the offers is recorded in the attempt.
- **A choice that cannot run falls back, visibly.** The task's ghost card and review row say why, and the task takes the agent it would have without a choice.
- **The person decides before anything runs.** Each ghost card and review row shows the task's agent and the planner's reason, the person can change any of them, and Accept writes exactly the agent each row shows.
- **"Fall back to the planner's agent"**, the box the owner confirmed on 2026-10-06 as "New tasks use the planner's agent", now applies only to a new task that has no usable choice of its own.

## Considered options

- Letting a run's planner choose agents too. Its prompt must render the same from its attempt alone, so it would need the offers recorded in the attempt. A run's planner keeps the old contract until a ticket asks for that.

## Consequences

- A fill still keeps the agent of the task the person drew, and an Approval ignores a choice, since it takes no agent.
- A proposal without agents, from an older build or a run's planner, reads and records as before.

Source: pull request [#98](https://github.com/Mano-Liaoyan/iDevelop/pull/98), its Decisions 1 to 6.
