# A gate approval is a human-origin result that forwards its inputs

An Approval node's approval is a single `ResultAccepted` entry whose origin is `Human(request)`, which forwards the reports and artifacts the person saw. It is atomic, and every reader of results, such as current results, staleness, input bindings, and completion, treats it as any accepted result. This is how E3e records [design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422)'s human-origin accepted result.

## How it works

- **No decision event beside the result.** Send back is its own `GateSentBack` event with the reason. The node takes no attempt and no client slot.
- **The request fixes its inputs.** Once the node's dependencies hand on, its request records E2's composition of them: the reports and the artifacts.
- **An approval forwards what the person saw.** Its report is each input's report under its task's title, in binding order and unshortened, because E2's composed text can point at delivered files, which a gate does not have. Its artifacts are the dependency inputs' artifacts, stored again under the approval's result ID with name, digest, and length kept. One artifact reached through two inputs counts once. Two different artifacts whose names differ at most in case block the request with `ArtifactCollision`. An agreed review's result follows the same rule ([ADR 0014](0014-a-review-forwards-its-inputs-artifacts-and-a-fix-keeps-its-own.md)).

## Considered options

- A separate decision event beside an ordinary result. A crash between the two would leave a decision without its result, and readers of results would need a second source of truth.

Source: pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74) (E3e), its Decisions 1, 2, and 10.
