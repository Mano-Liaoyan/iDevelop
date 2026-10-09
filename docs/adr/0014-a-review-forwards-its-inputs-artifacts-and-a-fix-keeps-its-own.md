# A review forwards its inputs' artifacts, and a fix keeps those it does not declare again

An agreed review's result forwards its input code, as before, and its dependency inputs' artifacts, copied under the review's own result with their names, bytes, and digests kept, as an approval's result does in [ADR 0007](0007-a-gate-approval-is-a-human-origin-result.md). The code's owners stay the subject's. A review fix's result keeps the artifacts of the subject's prior result that it does not declare again, matched by name without case, and a redeclared name replaces the kept artifact. The coordinator decided the carry-over, and the owner may revise it.

## How it works

- **One forwarding rule.** Reviews and approvals share `ArtifactForwarding`. An artifact reached through two inputs counts once, and two different artifacts whose names differ at most in case are an `ArtifactCollision`. For a review, a collision blocks the preparation of the reviewer's first turn and the refresh before each later turn.
- **Each forwarding result stores its own copy.** Every forwarding result reads its artifacts from its own folder. A review result's ID derives from its acceptance operation, so a crash converges on the same copy. Validation expects exactly the forwarded artifacts on a review result.
- **What a fix keeps.** The prior result is the subject's newest result accepted before the fix was reserved, so only a review's fix chain carries artifacts forward, and any other attempt keeps none. The kept bytes are copied under the fix's result, and publication validation expects them. The fix prompt names the kept artifacts and says that declaring one with the same name replaces it.

## Considered options

- A fix result with only the artifacts it declares, as every other publication has. A fix that changed only code would drop the artifacts the subject's earlier result handed to the reviewer and to the review's consumers.

## Consequences

- A fix can replace an artifact of its earlier result but cannot remove one.

Source: pull request [#79](https://github.com/Mano-Liaoyan/iDevelop/pull/79) (E3c.2), its Decisions 7 and 9, the last of them the coordinator's, and its review round 1 (P2-1 and P2-2).
