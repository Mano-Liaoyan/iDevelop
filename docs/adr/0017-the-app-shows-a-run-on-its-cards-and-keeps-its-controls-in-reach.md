# The app shows a run on its task cards and keeps the run's controls in reach

Run Workflow sits beside Generate and needs no selected node. While a workflow's run is active, a floating run bar at the bottom center, PlanWeave's run bar, holds the run's status, its done count, what goes on now, Resume while the run is paused, and Stop Workflow. The task cards show the run's view of each task through the canvas's existing node states. While a task's conversation covers the canvas, a strip under the conversation's header repeats the run's status, done count, and activity, with Resume and Stop Workflow, so they stay in reach. The strip is the coordinator's decision.

## How it works

- **No new visual language.** Each `TaskState` maps onto an existing `NodeState` and pill tone, with its own subtitle. Pending, Ready, and Unsupported read as Idle. Stale and Uncertain read as Interrupted. Blocked, SentBack, and a Refused start that needs no setup read as Failed. A settled run shows the tasks it never started as "Not started", as it does an attempt that Stop closed before its claim. A review without a subject, the only Unsupported task, reads "Nothing to review".
- **A run-owned task stands aside.** While the active run owns a task, the task's own Run, Cancel, and inline composer hide, and the card says which run owns it. Its conversation opens through the run, with `ProjectRuns.OpenConversation(coordinator, task)`. After the run settles, the cards keep the run's view until the task runs on its own again or the person dismisses the run.
- **The strip over the conversation** reuses the conversation's banner style and the run bar's pill. It hides in the dock layout, where the canvas's own run bar is in view. After a takeover by this window, Resume works from it without closing the conversation.
- **Switching stops nothing.** A project keeps its workflows' runs while it is open. Project and workflow rows show the running dot while a run's client works, and a waiting dot while a task waits for the person.

## Considered options

- Floating the run bar above the conversation. It would cover the conversation's composer.

Source: pull request [#80](https://github.com/Mano-Liaoyan/iDevelop/pull/80) (E3g.1), its Decisions 2, 3, 8, and 10, the last of them the coordinator's, and its retained-project activity, and pull request [#81](https://github.com/Mano-Liaoyan/iDevelop/pull/81) (E3g.2), its Decision 8.
