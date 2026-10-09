# A block holds a task only through its current attempt or result

A block holds a task, its consumers, and the run's completion only through the task's current attempt or current result, and a block on an attempt that a later attempt replaced holds nothing. This narrows [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422), which says "Known unresolved blocks prevent completion." An unresolved block on a replaced attempt does not prevent completion, while every block that holds a task does, because a held task is not done.

## Where each check looks

- **Drift lives on the publishing attempt.** A drift block is an unresolved `DirtyWorktree` or `UncertainOwnership` block that records a change to a checkout after its result was accepted or its baseline recorded. A task's drift is recorded and looked for on its publishing attempt: the attempt whose executed result is the task's current one, or, for a rebased result ([ADR 0009](0009-a-stale-result-is-rebased-as-one-merge-tree-commit.md)), the attempt whose checkout that result keeps. The recheck before a consumer's first claim, and the journal's guard on that claim, look only there.
- **The run projection.** A block holds a task when it names no attempt, the task's newest attempt, or the attempt of its current executed result. Unlike the publishing attempt, this rule counts no attempt for a rebased result, so a block on the attempt a rebased result keeps holds the task while that attempt is the newest. An attempt reserved after the current result was accepted, such as a retry or a fix, decides the task's state instead of the older result.
- **Completion.** The coordinator settles a run `Completed` once every task projects as done under that rule and every attempt is closed with no unresolved claim. The journal's own check asks that every task have a current, non-stale result, that every attempt be closed with no unresolved claim, and that every publication or rebase plan have its result or a block naming it. It does not ask that blocks be resolved.

## Rechecks name an operation that no command runs

A recheck records its block under an operation derived from the command's own (`recheck`), which no command runs as its own operation. A replay of `Prepare` or `PrepareTurn` resolves its own operation's blocks without rechecking files, so it can never clear drift. Only Restore and a recovery baseline, which verify the checkout, clear it. An attempt's own earlier drift block holds its continuing claim, Mark done, and a review conclusion before any live recheck, as a producer's block holds its consumers.

## Cancel always closes, and Mark done is held

Cancel never stops at its baseline check. It records the drift it finds and closes the attempt, even when the check cannot run. Mark done and a review conclusion publish or conclude, so drift holds them. When their check fails for another reason, it records a block under the closure's own operation, and the attempt's next successful baseline check resolves it, whether a closure under any operation or a later turn's claim, because that check verifies what the failed one could not. Otherwise drift follows Publish's rule: only an operation's own operation-scoped or repository-scoped fault is exempt, so another operation's fault holds Mark done as it would hold publication.

## What a person can clear

E3g.2's Recovery section shows the evidence of each block that holds a task, and offers only what the coordinator accepts. The projection fills the facts it decides on from the journal, such as whether a turn's root exit is recorded and whether a result carries code of its own. Its commands run E3a.3's steps and E1's recovery under the task's lease, in the controlling window only, and add no rules of their own.

- **Preserve and restore clears drift and capture blocks only.** It is offered for `DirtyWorktree` and `UncertainOwnership` blocks. Preserve retains the checkout as it is now with two matching observations, a preview names each move and the blocks it clears, and Restore makes the moves bound to that preview. A checkout that keeps changing keeps its block and moves nothing. Any other block, such as a conflicting join, shows its evidence and offers nothing, and Stop Workflow ends the run.
- **A shared ref or the stash is put back by hand.** iDevelop never moves shared refs or the stash, and offers no repair of them. A block on them shows where each ref pointed when the turn was prepared and when the block was found, and the person puts it back outside iDevelop. Restore then only checks those refs again, and the block resolves once they are back as recorded. Design v5 asks for a shared-ref or stash repair with its own displayed scope and confirmation, so this is a deviation, which keeps its rule never to silently adopt or erase a changed stash.
- **A rejected capture gets no result from a restore.** As design v5 says, a mismatched or missing turn-end capture cannot become successful through restoration. When a turn's capture diverged or failed, Restore only puts the checkout back, and only a Retry could give the task a result. The panel says that the run goes on only when the journal shows no block left on the task.
- **On macOS, files are changed by hand.** Without file identities, Restore cannot move files or remove `index.lock`. The section says so before any preview, and the person changes them by hand and preserves again.

## Considered options

- Every unresolved block of every attempt holds the task and the run. A block left on an attempt that a retry replaced would then hold the task and its consumers, and keep the run from completing, after the retry had already produced a newer result.

## Consequences

- A closure check's fault is recognized only through its 64th retry. A later one counts as drift.

Source: pull request [#69](https://github.com/Mano-Liaoyan/iDevelop/pull/69) (E3a.5b), its Decisions 1 to 4, pull request [#71](https://github.com/Mano-Liaoyan/iDevelop/pull/71) (E3b), its Decision 8 and its projection change for newer attempts, and pull request [#81](https://github.com/Mano-Liaoyan/iDevelop/pull/81) (E3g.2), its Decisions 1, 2, 4, 9, 10, and 11, its deviation, and its review fix P1.
