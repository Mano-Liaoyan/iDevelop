# Ask again after a send back, and a Retry note to the agent

After a send back, Ask again on the Approval node opens a new request on the same inputs, and a Retry takes an optional note to the agent. The owner decided this on 2026-10-12 in issue [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83), in these words:

- **Undoing a Send back:** after a confirmation, "Ask again" on the Approval node opens a new request on the same inputs. The sent-back request stays in history.
- **The Send back reason:** Retry gets an optional note to the agent. When the task feeds a sent-back Approval node, the note is prefilled with the reason.

## How it works

In Plan → Implement → Review → Approval, the person sends Approval's request back with `Handle empty input.`:

- **Ask again asks first.** The Approval section of the inspector offers Ask again on the sent-back request, and its confirmation says that a new request opens on the same inputs and that the sent-back request and its reason stay in the run's history. The journal records `gateAskedAgain` for that request, and the node takes request 2 on the same input bindings, with ids of its own. A repeat of Ask again returns the recorded entry. Only the node's newest request, sent back and still on current inputs, takes Ask again.
- **Send back is final for its request.** Request 1 stays sent back with its reason ([ADR 0008](0008-a-gate-request-is-answered-once.md)). Ask again and new inputs upstream are the two ways a sent-back node takes a new request, and Send back never picks a task to rerun.
- **A Retry carries a note.** The Retry confirmation has a box for a note to the agent. For Implement, which feeds the sent-back Approval node through its dependency connections, the box opens with `Handle empty input.`. The person can change or clear it. A note ends the agent's first prompt under the heading `A note from the person with this retry`, and the attempt's journal entry records it. A task that runs again by itself after a Retry, such as Review, gets no note.

Source: issues [#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83) and [#126](https://github.com/Mano-Liaoyan/iDevelop/issues/126), and the owner's decisions of 2026-10-12, rechecked on 2026-10-19.
