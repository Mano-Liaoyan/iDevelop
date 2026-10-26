# D0 design validation

## Task

On 2026-10-06 the user authorized D0, which validates the designs of the approved workspace slices before they are built. It covers UI prototypes and client capabilities. The UI prototypes live on the branch `d0/ui-prototypes`, which is never merged.

## Decisions and reasons

### Shared columns fix the inspector's insets

The unselected inspector had four right edges, identical at 280 and 520 px in both themes, and selected pickers sat at 58 px. The cause was XAML constants, not DPI or binding: separate paddings and column definitions in the inspector styles, the inspector view, and the palette. U1 uses one shared column definition for the header, the filter, and every row, because separate padding adjustments would keep competing alignment rules. [ADR 0021](../adr/0021-the-inspector-keeps-two-right-edges-and-stacks-values-below-440-px.md) holds the inspector's layout.

### Layout A keeps inspector actions visible

The user chose inspector layout A, the aligned grid. The prototypes use one fake task, six sections, Fluent icons, and a 12 px outer inset. Each layout has exactly two right edges in both themes and at both measured widths.

| Layout | Right insets | Height at 440 px width | Height at 280 px width | Measured tradeoff |
| --- | --- | --- | --- | --- |
| A, aligned grid | Actions at 12 px and values at 64 px | 1,120 px | 1,329 px | Revert, info, and Place stay visible. The layout costs 28 px of value width. |
| B, dense property sheet | 12 and 36 px, with counts on the left | 1,022 px | 1,118 px | At 280 px, values clip to "gpt-6.1-s" and Place moves behind More. |
| C, grouped settings | 12 and 36 px | 1,222 px | 1,279 px | Values sit far from their labels. |

Layout A beats B's lower height because its values fit and its actions stay visible at the narrow width. It beats C because labels stay close to their values.

### Conversation uses the main area by default

The user chose conversation in the main area by default, with a dock option. The measured transcript heights favor the main area.

| Window size | Dock transcript height | Main-area transcript height | Canvas height with the dock |
| --- | --- | --- | --- |
| 1280 by 800 px | 243 px | 661 px | 434 px |
| 1024 by 700 px | 243 px | 561 px | 334 px |

A dock as the default loses transcript space and task context. It remains an option when the person wants the canvas visible.

### The redesign decisions have user approval

On 2026-10-06 the user confirmed the planner's-agent box, decision D8 in the [node system redesign record](2026-10-05-node-system-redesign.md#decisions), and approved giving blueprints their own icon and color, which W1 carries in format 3.

### The UI probe measured bounded cases

The UI measurements came from the Release app on Linux under Xvfb, with `samples/storage-change` plus five placed blueprints, at inspector widths of 280 and 520 px in both themes. They do not establish Windows behavior or interaction correctness.

## The client probe found structured requests only in Claude Code and Codex app-server

### The probe ran nine cases against four clients

The user approved the real-client runs on 2026-10-06. The probe ran nine interaction cases against Claude Code, Codex, Pi, and Antigravity CLI. Codex ran in two modes, exec and app-server. Each verdict rests on one sample.

### Support varies by client and launch mode

| Case | Claude Code | Codex exec | Codex app-server | Pi | Antigravity CLI |
| --- | --- | --- | --- | --- | --- |
| 1. Streamed reply | Supported, text deltas | Unsupported, whole messages only | Supported, text deltas | Supported, text deltas | Supported, text deltas |
| 2. User question | Supported, structured answer | Partial, not run, text only | Supported, experimental structured answer | Partial, text only | Partial, text only |
| 3. Permission request | Supported, request before action | Unsupported, policy rejected | Supported, request before action | Unsupported, not run | Unsupported, write without request |
| 4. Allow and deny | Supported, allow wrote, deny did not | Unsupported, not run, policy rejected | Supported, allow wrote, deny did not | Unsupported, not run | Unsupported, no channel, deny not run |
| 5. Stale response | Supported, ignored without write | Unsupported, not run, policy rejected | Supported, ignored without write | Unsupported, not run | Unsupported, not run, no channel |
| 6. Live or queued input | Partial, next turn only | Unsupported, not run, one prompt | Supported, same turn | Supported, same agent run | Partial, next turn only |
| 7. Stop and send | Supported, same process | Partial, signal then resume | Supported, same process | Supported, same process | Partial, signal then resume |
| 8. Resume in new process | Supported, same session recalled | Supported, same session recalled | Supported, same session recalled | Supported, same session recalled | Supported, same session recalled |
| 9. Cancellation | Supported, acknowledgement then result | Partial, exit evidence only | Supported, terminal interruption | Supported, terminal abort | Supported, terminal interruption |

Codex app-server alone supported all nine cases, with an experimental question channel, so C1 chose it for Codex ([conversation record](2026-10-07-conversation.md)).

### The probe used installed versions and low effort on Linux

The captured versions were `claude` 2.1.291, `codex-cli` 0.160.0 for both Codex modes, `pi` 1.0.4, and `agy` 1.3.0. All runs used Linux and scratch Git repositories. Each session started in a fresh repository. Resume reused that session's repository. The probe models differ from the project's configured models.

The launch modes used these arguments, with session IDs and scratch paths omitted:

- Claude Code print mode used `claude -p --output-format stream-json --verbose --include-partial-messages --permission-mode default --model claude-haiku-4-5 --effort low`. The prompt went over stdin. The served model was `claude-haiku-4-5-20251001`.
- Claude Code bidirectional mode used `claude -p --output-format stream-json --verbose --include-partial-messages --permission-mode default --model claude-haiku-4-5 --effort low --input-format stream-json --permission-prompt-tool stdio`. The probe initialized the control channel, sent user lines, answered requests, and sent interrupts.
- Codex exec used `codex exec --json -m gpt-5.6-luna -c model_reasoning_effort=low -c approval_policy=never --skip-git-repo-check --sandbox workspace-write -`. The approval run replaced `-c approval_policy=never` with `-c approval_policy=untrusted -c approvals_reviewer=user`. Resume used `codex exec resume` and replaced `--sandbox workspace-write` with `-c sandbox_mode=workspace-write`.
- Codex app-server used `codex app-server -c sandbox_mode=workspace-write -c approval_policy=never -c approvals_reviewer=user -c model_reasoning_effort=low -c features.default_mode_request_user_input=true`. Protocol requests selected `gpt-5.6-luna` at `low` over stdio JSON-RPC. Initialization enabled `capabilities.experimentalApi`. `thread/start` set `approvalPolicy` to `untrusted` for approval and `never` otherwise, with `approvalsReviewer` `user` and `sandbox` `workspace-write`.
- Pi used `pi --no-tools --no-mcp --model openai-codex/gpt-5.6-luna --thinking low -p --mode json`. RPC runs replaced `-p --mode json` with `--mode rpc`. Both passed `--session-dir` with the scratch session path.
- Antigravity CLI used `agy --input-format stream-json --output-format stream-json --model gemini-3.8-flash --effort low --print=`. Each turn arrived as a user event line. These runs used the default mode without `--mode`. iDevelop launches it with `--mode accept-edits`.

### Raw logs establish sample behavior but not general support

The raw logs establish these measured facts:

- Claude Code print mode in `default` permission mode never asks. It denied the write and wrote nothing. Only bidirectional mode with `--permission-prompt-tool stdio` exposes `can_use_tool`.
- Claude Code acknowledged an interrupt, then returned `error_during_execution`. That result alone does not say it was a stop. The interrupt rejected a pending tool request without a request cancellation notice.
- Claude Code queued input during a turn as the next turn.
- Claude Code ignored duplicate, unknown, and post-interrupt answers without a write. Codex app-server ignored duplicate and unknown answers without a write.
- Codex app-server accepted `decline` although `availableDecisions` listed `accept`, `acceptWithExecpolicyAmendment`, and `cancel`. The command ended as `declined`. Answers produced `serverRequest/resolved`.
- Codex app-server questions required `capabilities.experimentalApi` and `features.default_mode_request_user_input=true`. The server emitted a `warning` about under-development features.
- Codex app-server delivered `turn/steer` input within the running turn. `turn/interrupt` ended that turn with status `interrupted`.
- Codex exec exited 1 on SIGINT without a terminal JSON event. `codex exec resume` recalled the turn. `codex exec` rejects `approval_policy=untrusted` with exit 1.
- Pi's `steer` ran at the next turn boundary inside the same agent run. Its `abort` ended the message with `stopReason` `aborted`, and the process still accepted the next prompt.
- Antigravity CLI 1.3.0 reported `permission_mode` `request-review` without `--mode`. It wrote `probe.txt` through `write_to_file`, an edit tool, without a permission event.
- Antigravity CLI's `init` tool list included `ask_question`, `ask_permission`, and `ask_custom_permission`. Print mode skipped `ask_question` within 16 ms as a `step_update` with step type `unknown`. No input event for answers, approvals, or interrupts was confirmed. Print mode therefore offers no usable question or permission channel.
- Antigravity CLI queued a second stdin line as the next turn. SIGINT produced a `result` with status `ERROR` and error `interrupted`, then exit 1.
- Every client resumed the same session in a new process. The process check found no leftover processes.

These claims remain inferred:

- Pi's permission, allow and deny, and stale-response verdicts rest on [agent client behavior](../agent-clients.md), which records that Pi has no permission system. Those cases did not run.
- Codex exec's question and live-input verdicts follow its protocol limits. Those cases did not run.
- Codex app-server's `item/fileChange/requestApproval` comes from the generated protocol schema. The probe observed only shell-command approval requests.
- No verdict was measured with the project's configured models or on Windows.

No run covered Windows, Antigravity CLI's `accept-edits` and `plan` modes, Codex app-server file-change approvals, the project's configured models, or repeated samples.

## Open issues

1. Neither workstream ran on Windows. The inspector needs Segoe UI at 125% and 150% scaling in both themes, and the client protocols need a check on Windows.
2. Codex app-server's question channel depends on an under-development feature, so iDevelop keeps it off and Codex asks in its reply.
3. Antigravity CLI offers no question or permission channel in print mode. Its conversation shows that limit and keeps its questions as messages.
