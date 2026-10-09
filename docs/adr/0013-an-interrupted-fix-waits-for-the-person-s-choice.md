# An interrupted fix waits for the person's choice, and a confirmation is one choice

A review's fix round that closing iDevelop interrupted never starts again by itself, in a run or standalone. Once its attempt is closed, the review offers Continue fix and Retry fix. The person's choice reserves one replacement at once, which launches when a client slot of the run is free, which below the run's bound of client roots is at once ([ADR 0018](0018-every-ready-task-of-a-run-starts-at-once.md)). A confirmation is one choice: it returns its replacement while that replacement is open, and once the replacement has closed it is refused. This is how E3c.2 builds [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s explicit interrupted-fix choice, where duplicate commands create one replacement and Resume run selects neither. It supersedes the [node model record](../handoffs/2026-10-04-node-model.md)'s line 261, "Only a fix round goes on after a quit": a fix round no longer goes on by itself.

## How it works

- **When the choice appears.** The window that closes records the fix as `Interrupted`, and Resume reconciles a claim left open, so the choice appears once the fix is closed. A claim that stays uncertain is first recovered through E1's confirmed path. Continue fix needs the fix's client session: after `Recovered(NotStarted)`, or without a session, only Retry fix is offered, and the review says why Continue fix is unavailable. Reopening or resuming starts neither.
- **Continue fix** preserves the subject's checkout with two matching observations, records them as a recovery baseline that the claim checks again, and reserves `AttemptCause.Continue` in the fix's own session. A checkout that changes between the two observations blocks, with `UncertainOwnership` when its branch or HEAD moved and `DirtyWorktree` otherwise, and starts nothing.
- **Retry fix** salvages the unfinished work, resets the checkout, and reserves `AttemptCause.Retry` in a fresh session, whose prompt carries the ticket and the change so far.
- **Reserve at once, launch when a slot is free.** The choice runs under the subject's task lease, and each step's operation derives from the confirmation, so a repeat after a crash or a refusal converges on the same records. The durable reservation is the acknowledgement, as a saved reply is in [ADR 0010](0010-a-reply-is-saved-at-once-and-runs-when-the-slot-is-free.md). The launch starts beside the run's other clients, and only at the run's bound of client roots does it wait behind ready tasks, like all review work.
- **One choice per confirmation.** A repeat of a confirmation returns its replacement while that replacement is open, and is `ReplacementConflict` once it has closed. While a replacement is open, another confirmation or the other choice is `ReplacementConflict` too. A replacement that closing interrupts again becomes the fix the next choice applies to, under a new confirmation.
- **The round keeps its link.** A run's Continue fix or Retry fix keeps the interrupted round's review link with its guidance count, so the replacement's prompt is rebuilt the same. A standalone choice records a new link with the current guidance count, as a standalone round does.
- **Ending a review ends its fix.** Cancelling a review cancels its running fix and closes a resting one as cancelled. A reserved fix that never started closes as `Recovered(NotStarted)` with "The review ended before this fix round started." Its confirmation derives from the ended reviewer attempt, as Stop's operation confirms a run's unstarted work in [ADR 0005](0005-stop-confirms-unstarted-work-and-a-run-never-settles-failed.md).

## Considered options

- A confirmation that returns its replacement forever. When closing iDevelop interrupted the replacement too, the same confirmation kept returning a closed attempt, and the run could not go on.

## Consequences

- A standalone review shows Continue fix and Retry fix in its inspector. A run's review reads "Fix interrupted", counts as waiting, and offers the same buttons and text through the run, with one confirmation per round and choice. A review that a run owns no longer shows the standalone choice.
- A fix round that a crash or a kill lost is first closed as stopped ([ADR 0005](0005-stop-confirms-unstarted-work-and-a-run-never-settles-failed.md)), and then offers the choice, with Continue fix when the fix has a session.

Source: pull request [#79](https://github.com/Mano-Liaoyan/iDevelop/pull/79) (E3c.2), its Decisions 1, 2, 5, and 8, and its review round 1 (P1-1, P1-2, and P2-1), and pull request [#81](https://github.com/Mano-Liaoyan/iDevelop/pull/81) (E3g.2), its Decision 7.
