# Run approval records an intent before the journal

A run approval writes a durable intent in the run's own folder before any side effect, and only then appends the approval to the run's journal. The first confirmation's command ID names the run, so repeated and concurrent confirmations converge on one run. This is how E3d.1 meets [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s preapproval intent and its one run per confirmed content.

## How it works

- **The intent lives in the run's own folder.** Under the workflow's `.idp/runs/<workflow>/approval.lock`, a confirmation writes `.idp/runs/<workflow>/<run-id>/approval.json`, then marks done an included planner that waits for the person ([ADR 0011](0011-a-run-approval-includes-a-planner-report-in-its-own-entry.md)), and only then appends the approval to the run's journal. A run folder with an intent and no journal is a pending approval. Every other reader skips run folders without `events.jsonl`.
- **The first confirmation names the run.** The run ID and its approval operation derive from the first confirmation's command ID, and a confirmation without one is refused. A repeated confirmation returns its run in any phase. Another confirmation of the same content (the revision and the reports it includes) adopts a pending intent or returns the active run whose intent matches. Different content while a run is active is `Busy`. After a run settles, a new confirmation starts another run.
- **A preview is stale when the revision changed.** A layout move does not make it stale.

Source: pull request [#73](https://github.com/Mano-Liaoyan/iDevelop/pull/73) (E3d.1), its Decisions 1 to 3 and 5 to 7, and pull request [#78](https://github.com/Mano-Liaoyan/iDevelop/pull/78) (E3d.2), its Decisions 1 and 2.
