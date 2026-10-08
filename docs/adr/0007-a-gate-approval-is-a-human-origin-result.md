# A gate approval is a human-origin result that forwards its inputs

An Approval node's approval is a single `ResultAccepted` entry whose origin is `Human(request)`, which forwards the code, reports, and artifacts the person saw. It is atomic, and every reader of results, such as current results, staleness, input bindings, and completion, treats it as any accepted result. This is how E3e records [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s human-origin accepted result.

## How it works

- **No decision event beside the result.** Send back is its own `GateSentBack` event with the reason. The node takes no attempt and no client slot.
- **The request fixes its inputs.** Once the node's dependencies hand on, its request records E2's composition of them: the reports, the artifacts, and the code, with a named join under the node's join ref when two or more commits come in. A fan-in conflict blocks the node instead.
- **An approval forwards what the person saw.** Its code is the inputs' code as forwarded code, a single source or the named join, as a read-only node forwards code. Its report is each input's report under its task's title, in binding order and unshortened, because E2's composed text can point at delivered files and a gate has no checkout. Its artifacts are the dependency inputs' artifacts, stored again under the approval's result ID with name, digest, and length kept. One artifact reached through two inputs counts once. Two different artifacts whose names differ at most in case block the request with `ArtifactCollision`.
- **Gates take no drift hold.** Requests and approvals skip the producer recheck of [ADR 0003](0003-a-block-holds-a-task-only-through-its-current-attempt-or-result.md). A gate has no checkout, and a consumer's first claim already rechecks forwarded code at its original owners.

## Considered options

- A separate decision event beside an ordinary result. A crash between the two would leave a decision without its result, and readers of results would need a second source of truth.

Source: pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74) (E3e), its Decisions 1, 2, and 10.
