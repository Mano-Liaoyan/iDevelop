# A Claude Code turn ends when its client exits, after its background work

A Claude Code turn ends when its client process exits, also when Claude Code reports a `result` while work it started in the background still runs. Claude Code can put a subagent in the background without being asked. It then sends a first `result`, such as "I'll wait for it", while the subagent runs. After stdin closes it answers the subagent in a turn of its own, with a second `result`, and exits. Claude Code stops waiting for background work after 10 minutes of idle waiting. Before this decision, iDevelop's 60-second deadline after success could kill the process before the second `result`. The turn then failed with "The client reported success but did not shut down." and lost the answer. Issue [#113](https://github.com/Mano-Liaoyan/iDevelop/issues/113) found it in the #93 real-client probe. The owner chose option A there on 2026-10-12.

## How it works

- **A turn is still one client process ([ADR 0026](0026-a-continued-session-reads-as-one-conversation.md)).** The later `result` is the turn's reply, because the newest verdict decides. Nothing is handed on, in a run or alone, while a subagent may still run.
- **The 60 seconds stand still while background work counts.** A backgrounded task counts from `task_started`, or a `task_updated` that backgrounds it, or its place in `background_tasks_changed`, until the `result` that follows its `task_notification` or its end. A task that is ambient or that a tool call awaits does not count. Once nothing counts, the 60 seconds start again in full. Stop and Cancel keep their own 5-second deadline.
- **The card says what the turn waits for.** While a running turn waits for a subagent, its card says "Waiting for a background subagent", and "Waiting for background work" for other background tasks, such as a background command. The attempt record and the run's task view both carry the list.
- **A subagent's messages are its own.** A message with a `parent_tool_use_id` shows under the subagent's type, such as "general-purpose", from the Agent call or its `task_started`. It never streams as the agent's text, reports no model for the turn, and is never the turn's reply.

## Considered options

- End the turn at the first `result`, keep listening, and add the late answer. It breaks one process per turn, and a run would hand on a result while the subagent may still run.
- Run subagents in the foreground with `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1`. It also turns off background commands, and confirming it needs a real-client probe.
- Keep the clock and make it longer. A long subagent still loses its answer, and a client that hangs waits longer.

The risk of this decision: a long subagent holds the task's lock and the run's client slot, and a person's message waits for the next turn. Stop is the way out, and Claude Code's own 10-minute cap bounds the wait.

Source: issue [#113](https://github.com/Mano-Liaoyan/iDevelop/issues/113), its design comment and the owner's decision of 2026-10-12.
