# Run approval records an intent and pins its base before the journal

A run approval writes a durable intent in the run's own folder before any side effect, rebuilds its snapshot commit from that intent, and pins the base under `refs/idp/approvals/<run-id>` before the run's journal names it. The first confirmation's command ID names the run, so repeated and concurrent confirmations converge on one run. This is how E3d.1 meets [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s preapproval intent, its one run per confirmed content, and its hidden snapshot that leaves the person's branch, index, and files alone.

## How it works

- **The intent lives in the run's own folder.** Under the workflow's `.idp/runs/<workflow>/approval.lock`, a confirmation writes `.idp/runs/<workflow>/<run-id>/approval.json`, then makes the snapshot commit, pins the base, and only then appends the approval to the run's journal. A run folder with an intent and no journal is a pending approval. Every other reader skips run folders without `events.jsonl`.
- **The first confirmation names the run.** The run ID and its approval operation derive from the first confirmation's command ID, and a confirmation without one is refused. A repeated confirmation returns its run in any phase. Another confirmation of the same content (revision, base choice, HEAD, and for a snapshot its tree) adopts a pending intent or returns the active run whose intent matches. Different content while a run is active is `Busy`. After a run settles, a new confirmation starts another run.
- **The snapshot commit is rebuilt, not stored.** The work tree goes through a temporary index, without `.idp`, `.worktrees`, and ignored files, and is committed over HEAD as `iDevelop <idevelop@localhost>` at the intent's recorded second, so a retry after a crash rebuilds the same commit.
- **The base is pinned**, for HEAD bases too, after the commit and before the journal names it. Nothing else references a snapshot commit until the run's first preparation writes `refs/idp/<run-key>/base`, and Git's garbage collection would remove it. `approvals` can never be a run key, because run keys are hexadecimal, so the pin is outside every `refs/idp/<run-key>/` namespace and every ownership snapshot. The first preparation deletes the pin by compare-and-swap once the base ref holds the same commit. Each confirmation of the workflow deletes any pin left that way by a crash, and removing a stale pending intent deletes its pin first.
- **What a preview offers.** A project without a commit is a gap, not a parentless snapshot. An unmerged index offers HEAD only, because a snapshot through `git add` would commit the conflict markers. A preview is stale when the revision, HEAD, or for a snapshot the work tree changed, or when a clean project turned dirty. A layout move is not, and further dirty edits do not refresh a HEAD preview, whose base they cannot change.

## Consequences

- A pin stays when a run is stopped or abandoned before its first preparation, because only a run with a run key releases its pin. It keeps one commit alive.

Source: pull request [#73](https://github.com/Mano-Liaoyan/iDevelop/pull/73) (E3d.1), its Decisions 1 to 3 and 5 to 7.
