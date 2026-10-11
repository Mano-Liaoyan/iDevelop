# A run's planner and its amendment choose agents, and so does a fill

A planner that runs inside a workflow run now proposes a client, a model, a reasoning level, and a one-line reason for each task it adds, the same way Generate Workflow's planner does under [ADR 0019](0019-a-planner-chooses-each-new-task-s-agent.md). An amendment gives each task the agent the person's review shows. A planner also chooses an agent for each task it fills that has no agent of its own. This supersedes two consequences of ADR 0019: "A fill still keeps the agent of the task the person drew" and the considered option that kept a run's planner on the old contract "until a ticket asks for that". The owner asked for it on 2026-10-09 in issue [#99](https://github.com/Mano-Liaoyan/iDevelop/issues/99), because a planner inside a run and an amendment still gave every new task the planner's agent, and decided on 2026-10-12 how a fill falls back.

## How it works

- **A run's start records what it offered.** When the start of a planner's attempt is planned, the run records the clients that are ready on the machine, with the models they can run now and each model's levels, on the start's plan in the run's journal. The planner's prompt lists them, so every preparation, claim, and recovery renders the same prompt from the attempt alone, whatever the clients offer by then. Only a planner's initial start or retry records them, since a continuation or a review fix sends a prompt of its own. A start while no client offers a model records nothing and reads the contract without agents, as every start did before. The offers are no part of the plan's command, so a repeat of the same operation returns the plan it recorded.
- **An amendment takes the review's choices.** The person's acceptance carries the planner's choices that the review found this machine can run and the person's own choices, as Generate's review passes them. Each task takes the person's choice, else its own agent, else the planner's choice, else the planner's agent while the box is ticked. The amendment records both lists in its choice, so the same acceptance again converges and other choices are another acceptance. An acceptance without agents records and repeats exactly as before.
- **A fill takes the planner's choice when its task has no agent.** The prompt asks for an agent on a fill and says which slots keep an agent of their own. A fill keeps the agent its task has, the person's own or its blueprint's default from when it was placed, and its review row says "Set on the task". Otherwise it takes the person's choice in the review, else the planner's usable choice.
- **The box applies to a fill too.** A fill whose task has no agent, and for which the planner chose none this machine runs, takes the planner's own agent while "Fall back to the planner's agent" is ticked, as a new task does. The owner decided this on 2026-10-12.
- **The review checks, the journal records.** As in Generate, the review checks each choice against the clients as they are now. The run's store has no client statuses, so it records what the review passed and does not check it again.

## Considered options

- Letting a fill keep its task as it is when the planner gave no usable choice, even with the box ticked. The owner chose the same fallback as for a new task.
- Recording the offers on the attempt's log only. The prompt of a run's planner renders from the run's journal at each preparation and claim, so the journal has to hold them.

## Consequences

- No view accepts a run planner's proposal yet. Issue [#84](https://github.com/Mano-Liaoyan/iDevelop/issues/84) builds it, and the owner noted there that it must show the picked agents and let the person change them.
- A run's journal written before this decision has no offers on its plans, and its planners keep the contract they read.

Source: pull request [#115](https://github.com/Mano-Liaoyan/iDevelop/pull/115), its Decisions, and the owner's answers on issue #99 on 2026-10-12.
