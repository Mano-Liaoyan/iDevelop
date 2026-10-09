# A reply is saved at once and runs when the run's slot is free

In a run, a message to a resting attempt is written to the attempt's log at once, which is its durable acknowledgement, and the attempt's next turn starts when the run's one client slot is free. These continuations take the slot before any ready task, in task order, because a person waits on them. A standalone reply starts its turn in the same step, but a run's reply may have to wait for another task's client.

## How it works

- **What continues by itself.** Only a turn that ended with queued text goes on by itself. A waiting attempt goes on once a message was written after its turn's closure checkpoint. That position tells a reply apart from text queued before a question was deferred, so no event or schema changed. The next turn's prompt is the queued texts joined by a blank line, earlier queued text first.
- **Refuse rather than hold.** While the task's turn starts or settles, or its next turn is prepared and not yet claimed, Send is refused with a reason, such as "The task's next turn is starting. Send your message again in a moment." The message is not held in memory, and the Desktop keeps the draft. So the queue never changes between a next turn's prompt and its claim.
- **A running turn** takes the message and queues it, as in a standalone run. Stop and send ends the turn first.
- **Mark done and Cancel go through the coordinator**, like Send, so a second window gets `Unavailable` and a command for another run is refused.

## Consequences

- A conversation opened in a second, read-only window keeps answering `Unavailable` after the controlling window closes, until it is opened again on the coordinator that now controls the run. The app does that by itself: once its window takes control ([ADR 0004](0004-one-window-controls-a-run-and-only-its-resume-schedules.md)), the open conversation reopens on the new coordinator with its draft.

Source: pull request [#76](https://github.com/Mano-Liaoyan/iDevelop/pull/76) (E3c.1), its Decisions 1 to 5 and its open question on read-only windows, and pull request [#80](https://github.com/Mano-Liaoyan/iDevelop/pull/80) (E3g.1), its follow-up from #76.
