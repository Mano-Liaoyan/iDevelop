# A node is an agent, and a Git commit hand-off is not a requirement

On 2026-10-18 the owner restated the product's goal and said earlier records had misread it. In the owner's words, with spelling corrected:

> My goal is that node is an abstraction of an agent, it contains the model/effort it chooses, system prompt, instructions and skills equipped with it, conversation with human, input it can read from previous agent or human and output for the later agents and human. The overall goal is that we build a visual node/graph system that can execute the task in a multi-agent environment, since each model has its advantage in some domain, this system can be beneficial.

The owner also said that "a task's result is a Git commit that must be correct, survive crashes, and be safe to build on" was never a requirement. That guarantee came from records that agents wrote, chiefly the [target workflow](../product-direction.md#the-target-workflow) and the [D0 design validation](../handoffs/2026-10-06-d0-design-validation.md), which settled per-task worktrees, branches, and join commits. It was never the owner's request.

## What this decides

- **A node is an agent.** It holds what its agent is: the model and effort, the system prompt, the instructions, and the skills. It holds the agent's conversation with a person, the input the agent reads from earlier agents or a person, and the output it leaves for later agents and the person.
- **The purpose is multi-model work.** A workflow lets each model do the part of a task it is good at.
- **The Git commit hand-off is not a requirement.** No requirement asks for a per-task worktree and branch, a result captured as a commit, double capture, drift blocks, join commits, or rebasing a stale result. The code built them in D0, E2, and E3, and they stay until a separate decision removes or keeps each one.

## Consequences

- Records that state the Git hand-off as a requirement now say it is not one. ADRs that exist mainly for it stay as the record of what was built, and this ADR overrides them where they read as requirements.
- The decision to drop, keep, or simplify each part of the Git machinery belongs to the owner, issue by issue.
- How a node's input and output are carried, as text, files, or something else, is not decided here.

Source: the owner's message of 2026-10-18 in a Claude Code session.
