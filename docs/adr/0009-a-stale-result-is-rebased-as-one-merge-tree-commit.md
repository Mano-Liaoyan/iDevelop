# A stale result is rebased as one merge-tree commit

Review updated inputs replays a stale writer result's complete recorded change onto its current inputs as one squashed commit, built by a three-way `git merge-tree` in E2b's scratch Git directory, and like a join it needs Git 2.43. A person's approval moves the task's branch and checkout to that commit and records a rebased-origin result, with no attempt. This is how E3f builds [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s clean rebase candidate and person-approved rebased result.

## The candidate

- **One squashed commit.** `git merge-tree --write-tree` with a named merge base merges three trees. The base is the stale result's effective upstream base, the code of its inputs. Ours is the current verified input revision, or a join of the current sources built the way a consumer's own join is. Theirs is the stale result's commit. The candidate is one commit whose parent is the new base, with a frozen recipe (iDevelop's fixed identity and the latest committer time of its inputs), so the approval can rebuild the preview and compare identities, and a crash rebuilds the same commit. The task's original commits stay reachable through the old result's ref.
- **E2b's scratch Git directory is design v5's "isolated temporary checkout".** The merge runs there under the same pinned settings, with attributes from the run base, not in a temporary worktree.
- **Git 2.43, like a join**, because a rebase merge is the same `--attr-source` merge. On older Git the preview shows the candidate as unavailable ("Rebasing needs Git 2.43 or later. Installed: …"), and the approval refuses. When the code base did not change, as in a report-only upstream update, no merge runs, so any supported Git rebases.
- **Only the controlling window previews.** The preview builds merges and clears stale merge scratch folders, so it runs under the task's lease and the repository mutation lock, and a held lock returns `JournalBusy`. It records nothing. A conflict or an unavailable candidate is shown, never journaled.

## The approval

- **Three journaled moves.** The approval runs only while a fresh preview still has the approved identity. Under the repository mutation lock it journals a rebase plan, then three moves, each journaled as intent and observation and each preceded by a live check: the retention ref `refs/idp/<run-key>/rebase/<task-key>/<result-id>`, a compare-and-swap of the task branch from the stale commit, and `reset --hard` of the clean checkout. Repeating the approval, Resume, or Stop finishes a plan that a crash interrupted.
- **A rebased-origin result.** The rebase records a `ResultAccepted` whose origin is `Rebased(source, plan)`, with the current input bindings, the stale result as the result it supersedes, and the stale result's report and artifacts. No attempt is created, and the old result and its ref stay. The rebased result is the task's new source baseline, so a later retry or review fix compares its sources with the rebase's.
- **Acceptance does not require fresh inputs**, only the approved plan's inputs and every observed move, so an interrupted plan can always finish. If an input changed meanwhile, the rebased result is stale in turn, like a running consumer's.
- **A rebase replaces only the stale result's clean checkout.** When the checkout's last recorded state is a later attempt's, such as a failed retry, the preview and the approval refuse `StartConflict`. Only a result with code of its own rebases: a stale read-only report and a stale review result are retried, not rebased. One unfinished rebase per task is allowed.

## Considered options

- A temporary worktree with `cherry-pick`. It would run the project's own config, attributes, and merge drivers, and add a worktree registration that a crash could leave behind.

## Consequences

- No ticket yet owns manual Retry, and the journal's source check refuses a retry of a stale consumer whose upstream code changed with `StaleInput`. So a stale result that cannot be rebased, because of a conflict or because it has no code of its own, has no way forward until the Retry ticket relaxes that check. The Updated inputs panel offers no rebase to a result without code, which the coordinator would refuse with `UnsupportedResult`, and says that a Retry, not offered yet, would bring it up to date.
- A rebase does not move the consumer's join branch. The rebase ref keeps the new join commit alive as the candidate's parent.

Source: pull request [#75](https://github.com/Mano-Liaoyan/iDevelop/pull/75) (E3f), its Decisions 1 to 4, 6, and 8 to 10, and its review's note on Retry, and pull request [#81](https://github.com/Mano-Liaoyan/iDevelop/pull/81) (E3g.2), its review fix P2-1.
