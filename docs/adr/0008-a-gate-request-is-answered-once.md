# A gate request is answered once, and only new inputs reopen it

Design v5 says Approve and Send back carry the expected request and input IDs, obsolete responses return `Stale`, identical repeats return the original receipt, and superseded inputs produce a new "Waiting for approval" request. E3e settled what supersedes a request and what an answer can still change.

- **What supersedes a request.** A request is superseded when one of its provided inputs is stale or no longer its producer's current result. Context that arrives later supersedes nothing, as for a reserved attempt. Superseded inputs make the node ready for a new request.
- **A stale approval asks again.** When an approval's own result goes stale, the node takes a new request. It does not use the rebase of [ADR 0009](0009-a-stale-result-is-rebased-as-one-merge-tree-commit.md): an Approval node takes only a human-origin result, so the `Stale` task state never applies to it. A stale gate needs no rebase, because it changes no code.
- **Send back is final for its request.** Approve after a send back is `Stale`. A new request comes only when the request's inputs are superseded, for example by a Retry upstream.
- **Repeats and stale answers.** An answer that repeats the recorded one, with the same request, input IDs, answer, and send-back reason, returns the original entry, even after a supersession. Any other answer to an answered, superseded, or mismatched request returns `Stale`, which names the open request the node waits on now, if there is one. A sent-back request waits for nothing, so `Stale` never names one.
- **IDs derive from the inputs.** The request's gate, input, and result IDs derive from its operation, which derives from the run, the task, and the input bindings. A retry after a crash converges on one request, and changed inputs get a new one, without a plan event.
- **A request that loses a race is made again.** A request reads its inputs first and records them last. When the journal refuses it because a result landed or the revision changed in between, the coordinator holds nothing, and the next decision reads the inputs again under a new operation. A refusal on unchanged inputs still holds the node, so nothing retries in a loop. Agent starts keep their refusals: making `StaleInput` a busy refusal for them would retry every such start each second.

## Consequences

- A person who sent back by mistake has to change something upstream. No ticket yet owns manual Retry, so today only Stop Workflow is left. Whether a send back can be undone is the owner's decision.

Source: pull request [#74](https://github.com/Mano-Liaoyan/iDevelop/pull/74) (E3e), its Decisions 3, 4, 5, 7, and 8, and its open question on stale approvals, which E3f's merge kept.
