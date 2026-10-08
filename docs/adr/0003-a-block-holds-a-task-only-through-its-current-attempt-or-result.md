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

## Considered options

- Every unresolved block of every attempt holds the task and the run. A block left on an attempt that a retry replaced would then hold the task and its consumers, and keep the run from completing, after the retry had already produced a newer result.

## Consequences

- A closure check's fault is recognized only through its 64th retry. A later one counts as drift.

Source: pull request [#69](https://github.com/Mano-Liaoyan/iDevelop/pull/69) (E3a.5b), its Decisions 1 to 4, and pull request [#71](https://github.com/Mano-Liaoyan/iDevelop/pull/71) (E3b), its Decision 8 and its projection change for newer attempts.
