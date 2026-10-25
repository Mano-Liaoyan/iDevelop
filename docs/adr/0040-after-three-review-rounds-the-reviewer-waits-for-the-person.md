# After three review rounds the reviewer waits for the person

A review loop stops for the person after a set number of rounds. Each review node has a round limit, 3 by default. A round is one reject sent along the feedback edge and the predecessor's new result. Once the limit is reached, the reviewer does not review again by itself and waits for the person, input-required. On 2026-10-25 the owner chose that a review loop that stops making progress pauses for them, after a Review and a Merge node in their project went 111 rounds in 54 minutes with the same three findings. Map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179) made the pause a round count ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

In Implement → Review, with Review's limit at 3:

- **The loop runs its rounds.** Review rejects, Implement continues in its session with the findings, and Review reviews its new result, three times.
- **Then the reviewer waits.** After the third round, Review is input-required, and its inspector says it reached its round limit, beside its findings.
- **The person decides.** The person's message to Review continues the reviewer's session and lets it review again. Raising the limit lets the loop go on. A Retry starts the loop over in a fresh session.
- **The wait holds nothing.** No client runs while Review waits, and the wait is in the node's log, so it survives a restart.

## Considered options

- Detecting a round that repeats an earlier round's findings and positions. It needs a comparison of rounds that a fixed count does not, and it can return later as a routing rule.

Source: the owner's decision of 2026-10-25 and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
