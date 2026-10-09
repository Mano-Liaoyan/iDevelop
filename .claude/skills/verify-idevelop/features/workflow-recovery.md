# Recover a workflow run's tasks

When a workflow run cannot go on by itself, the task that holds it shows what the person can do in the inspector. A task whose checkout changed after its turn ended reads `Blocked`. Its `Recovery` section lists the paths, the client's process, how its turn ended, its captures, and its cleanup, with `Preserve` and then `Restore`. A task whose turn's end was never recorded, as after a crash or a kill, reads `Uncertain` and offers `Close as stopped` with a reason. A task built from a result that has since been replaced reads `Inputs changed`, and its `Updated inputs` section offers `Review updated inputs` and `Approve rebase`. A review whose fix round a closed or killed app interrupted reads `Fix interrupted` and offers `Continue fix` and `Retry fix`. The card's attention glyph of each of these tasks selects it.

## Sub-features

- `recover-evidence` shows `RecoveryAttempt`, every block in `RecoveryBlocks` with all the paths and refs it names, `RecoveryClient`, `RecoveryTurnEnd`, `RecoveryCaptures` with `RecoveryCapturePaths`, and `RecoveryCleanup` for a blocked or uncertain task.
- `recover-refs` says in `RecoveryRefRepair` where each shared ref a block names pointed and points, and that the person puts it back before Restore can clear the block. `RestoreRest` counts the blocks Restore only checks again apart from the ones it clears.
- `recover-unaccepted` says in `RecoveryNoSuccess` that no restore can give an attempt a result when its turn-end capture was not accepted.
- `recover-owner` shows on a task held by another task's block which task holds the checkout, and `RecoveryShowOwner` selects that task.
- `recover-restore` retains the checkout with `PreserveCheckout`, previews `RestoreTarget`, each `RestoreMove`, `RestoreRest`, and `RestoreRetained`, and `RestoreCheckout` puts it back so the run goes on.
- `recover-hand` shows `RecoveryHandRepair` on macOS, where Restore cannot move files or remove `index.lock`, and shows Restore's refusal in `RecoveryNotice`.
- `recover-close` closes an uncertain turn with `RecoveryReason` and `CloseAsStopped`, and warns in `RecoveryStillRuns` while its process still runs. A turn whose client exited but whose settlement failed offers no closure: `RecoveryUnsettled` says what retries it.
- `recover-stopping` keeps a stopped run `Stopping` while a turn is uncertain, says so in `RunActivity`, and settles it once the turn is closed.
- `recover-rebase` previews a stale task's rebase with `ReviewUpdatedInputs`, shows `RebaseUpdated`, `RebaseChanges`, `RebaseReport`, `RebaseCandidate`, and `RebaseCandidatePaths`, and records it with `ApproveRebase`. A stale report shows `RebaseUnavailable` instead.
- `recover-fix` offers a run's interrupted review fix round through `ContinueFix` and `RetryFix`, and starts nothing before the person chooses.

## How to get to it (user POV)

- Choose the attention glyph on a card that reads `Blocked`, `Uncertain`, `Inputs changed`, or `Fix interrupted`, or choose the task in the sidebar.
- In the inspector's `Recovery` section, choose `Preserve`, then `Restore`, or type a reason and choose `Close as stopped`.
- In the inspector's `Updated inputs` section, choose `Review updated inputs`, then `Approve rebase` or `Review again`.
- In a review's `Run` section, choose `Continue fix` or `Retry fix`.

## Driving it with real-window.psm1

Preconditions:

- The preconditions of [Run a whole workflow](./workflow-run.md) hold, with a Git repository and a commit, and every task runs through the fake Codex.
- An Approval node sits between the first and the second task, so the run waits while the first task's checkout is changed by hand.

- **Restore.** Start a run and wait until the Approval node's `CardStatus` reads `Waiting for approval`. Change a file the first task wrote in its checkout under `$s.Project\.worktrees\`, then approve. `Wait-Until { (Find-ById $s.Window 'RunStatus').Current.Name -eq 'Needs attention' } 60` returns `$true`, and the first card reads `Blocked`. Choose the first task. `RecoveryPaths` names the file. Run `Invoke-Element (Find-ById $s.Window 'PreserveCheckout')`, then `Wait-Until { Find-ById $s.Window 'RestoreCheckout' 1 } 30`. Run `Save-Evidence $s 'restore-preview'` and `Invoke-Element (Find-ById $s.Window 'RestoreCheckout')`. The run reads `Completed`, the file holds what the task wrote, and the journal has a `restored` line.
- **Close as stopped.** Start a run whose first turn waits for `$s.Gate`, wait for `Running`, and stop the app's process with `Stop-Process -Id $s.Process.Id -Force`. Run `$s = Start-IDevelop -Reopen` to open the same project again. The first card reads `Uncertain` and `RunStatus` reads `Paused`. Choose the first task: `RecoveryClient` names the client's process. Stop that process, type a reason into `RecoveryReason`, and run `Invoke-Element (Find-ById $s.Window 'CloseAsStopped')`. The card reads `Closed as stopped`, and `Stop Workflow` settles the run as `Stopped`.
- **Rebase.** The Linux run used a writer, a review that asks for one change, a consumer of the review, and a second consumer of the writer. Once the fix is accepted, the second consumer reads `Inputs changed`. Choose it and run `Invoke-Element (Find-ById $s.Window 'ReviewUpdatedInputs')`. `RebaseCandidate` reads `Its change replays cleanly. ...`. Run `Invoke-Element (Find-ById $s.Window 'ApproveRebase')`, and the run reads `Completed`.

## Gotchas

- The fake Codex of a Windows session answers every turn alike, so a review that asks for a change, and the stale consumer it makes, need the per-task scripts that the headless tests `WorkflowRunReviewTests` use.
- Restore refuses on macOS whenever a file has to move, so `recover-hand` is the only path there.
- Closing the app through its window asks to stop the run, so only a killed process leaves a turn `Uncertain` or a fix round interrupted.
- Windows has not run this recipe yet. The headless tests `WorkflowRunRecoveryTests` and `WorkflowRunReviewTests` and a Linux window under Xvfb cover it so far.
