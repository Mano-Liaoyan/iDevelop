# D0 design validation

## Task

On 2026-10-06 the user authorized D0 and lifted the execution plan's deferral of implementation. D0 validates the designs before the approved delivery slices build them.

D0's two workstreams are done. They cover UI prototypes and client capabilities. C1 can start after W1 with the probe's results.

No application code, test, script, or skill changed on `main`. The UI prototypes live on the local branch `d0/ui-prototypes` at commit `65efddd`. That branch is never pushed or merged.

## Decisions and reasons

### Shared columns fix the inspector's constant insets

The unselected inspector has four right edges. The measured insets are identical at 280 and 520 px in both themes. Selected pickers sit at 58 px. The cause is XAML constants, not DPI or binding.

The line numbers match `main` at `11a0b22`. Paths in the table are relative to `src/IDevelop.Desktop/`.

| Right inset | Controls | XAML constants on `main` |
| --- | --- | --- |
| 8 px | Header and filter menu | `Inspector/InspectorStyles.axaml` lines 8 and 46 set padding `12,12,8,8` and `12,0,8,8`. |
| 12 px | Section content and overview counts | `Inspector/InspectorView.axaml` line 133 sets `Margin="12,0,12,16"`. Its line 679 and `Blueprints/PaletteView.axaml` line 27 use `Layout="Full"`. These rows span `ColumnDefinitions="2*,3*,46"` in `Inspector/InspectorStyles.axaml` line 160. |
| 36 px | Filter box | A 24 px menu button and 4 px spacing add to the 8 px padding from `Inspector/InspectorStyles.axaml` line 46. |
| 38 px | Library Derive action | `Blueprints/PaletteView.axaml` line 29 sets `Auto,*,Auto,Auto,24`. The hidden Edit column still reserves 24 px. |
| 58 px | Selected pickers | `Inspector/InspectorStyles.axaml` line 160 reserves the trailing 46 px in `2*,3*,46`. The section inset adds 12 px. |

U1 uses one shared column definition for the header, the filter, and every row. Separate padding adjustments would retain the competing alignment rules.

### Layout A keeps inspector actions visible

The user chose inspector layout A, the aligned grid. The prototypes use one fake task, six sections, Fluent icons, and a 12 px outer inset. Each layout has exactly two right edges in both themes and at both measured widths.

| Layout | Right insets | Height at 440 px width | Height at 280 px width | Measured tradeoff |
| --- | --- | --- | --- | --- |
| A, aligned grid | Actions at 12 px and values at 64 px | 1,120 px | 1,329 px | Revert, info, and Place stay visible. Pickers stack below labels under 320 px. The layout costs 28 px of value width. |
| B, dense property sheet | 12 and 36 px, with counts on the left | 1,022 px | 1,118 px | At 280 px, values clip to "gpt-6.1-s" and Place moves behind More. |
| C, grouped settings | 12 and 36 px | 1,222 px | 1,279 px | Values sit far from their labels. |

Layout A beats B's lower height because its values fit and its actions stay visible at the narrow width. It beats C because labels stay close to their values.

### Content-sized cards preserve room for answers

The user chose cards sized to content and capped at 320 px. The prototype laid out a six-task diamond at a 158 px row pitch.

| Card state | Content-sized height | Fixed height |
| --- | --- | --- |
| Idle | 149 px | 312 px |
| Blocked | 186 px | 312 px |
| Running | 216 px | 312 px |
| Finished | 228 px | 312 px |
| Question | 311 px | 312 px |

Only the idle card fits the row pitch, so overlap between expanded cards is unavoidable. A 268 px fixed height clips the answer box. A running card grows 95 px when a question arrives. Content sizing avoids the unused space of a 312 px fixed card while leaving room for that answer.

The selected card draws whole on top. Other headers stay above other bodies. Raising the selected card alone can hide a neighbor completely. Keeping only headers above bodies cuts across the question being answered. The chosen policy preserves the selected interaction and the other task headers.

### Conversation uses the main area by default

The user chose conversation in the main area by default, with a dock option. The measured transcript heights favor the main area.

| Window size | Dock transcript height | Main-area transcript height | Canvas height with the dock |
| --- | --- | --- | --- |
| 1280 by 800 px | 243 px | 661 px | 434 px |
| 1024 by 700 px | 243 px | 561 px | 334 px |

At 1024 px, the dock header truncates the breadcrumb before the task name. A dock as the default loses transcript space and task context. It remains an option when the person wants the canvas visible.

### The redesign decisions now have user approval

On 2026-10-06 the user confirmed the "New tasks use the planner's agent" box, decision D8 in the [node system redesign record](2026-10-05-node-system-redesign.md#decisions). Its rejected alternatives stay rejected. Always falling back would override the rule that a new node takes its blueprint's default agent. Never falling back would leave every generated node reading "Choose an agent".

The user also approved giving blueprints their own icon and color. The file-format field arrives within W1's format and migration review. The user rejected a separate format change. W1 already needs that review, so one review covers both changes. Until W1 adds the field, a project or personal blueprint shows its base kind's tile with a library badge.

## What was measured and what was inferred

### The UI probe measured bounded cases

The UI measurements came from the Release app on Linux under Xvfb. The reproduction used `samples/storage-change` plus five placed blueprints, at inspector widths of 280 and 520 px in both themes. Prototype bounds and screenshots supplied the layout, card, and conversation measurements above. They do not establish Windows behavior or interaction correctness. The conversation prototype used a synthetic transcript, hand-built Markdown, and a placeholder capability line. It proves no client capability.

### The Windows claim is inferred

The inspector's constant-based cause is expected to hold on Windows. D0 did not observe Segoe UI at 125% or 150% scaling.

## The client probe found structured requests only in Claude Code and Codex app-server

### The probe ran nine cases against four clients

The user approved the real-client runs on 2026-10-06. The probe ran nine interaction cases against Claude Code, Codex, Pi, and Antigravity CLI. Codex ran in two modes, with app-server as a candidate beside Codex exec. Each verdict rests on one sample.

### Support varies by client and launch mode

| Case | Claude Code | Codex exec | Codex app-server (candidate) | Pi | Antigravity CLI |
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

### The probe used installed versions and low effort on Linux

The captured versions were `claude` 2.1.291, `codex-cli` 0.160.0 for both Codex modes, `pi` 1.0.4, and `agy` 1.3.0. All runs used Linux and scratch Git repositories. Each session started in a fresh repository. Resume reused that session's repository. The probe models differ from the project's configured models.

The launch modes used these arguments, with session IDs and scratch paths omitted:

- Claude Code print mode used `claude -p --output-format stream-json --verbose --include-partial-messages --permission-mode default --model claude-haiku-4-5 --effort low`. The prompt went over stdin. The served model was `claude-haiku-4-5-20251001`.
- Claude Code bidirectional mode used `claude -p --output-format stream-json --verbose --include-partial-messages --permission-mode default --model claude-haiku-4-5 --effort low --input-format stream-json --permission-prompt-tool stdio`. The probe initialized the control channel, sent user lines, answered requests, and sent interrupts.
- Codex exec used `codex exec --json -m gpt-5.6-luna -c model_reasoning_effort=low -c approval_policy=never --skip-git-repo-check --sandbox workspace-write -`. The approval run replaced `-c approval_policy=never` with `-c approval_policy=untrusted -c approvals_reviewer=user`. Resume used `codex exec resume` and replaced `--sandbox workspace-write` with `-c sandbox_mode=workspace-write`.
- Codex app-server used `codex app-server -c sandbox_mode=workspace-write -c approval_policy=never -c approvals_reviewer=user -c model_reasoning_effort=low -c features.default_mode_request_user_input=true`. Protocol requests selected `gpt-5.6-luna` at `low` over stdio JSON-RPC. Initialization enabled `capabilities.experimentalApi`. `thread/start` set `approvalPolicy` to `untrusted` for approval and `never` otherwise, with `approvalsReviewer` `user` and `sandbox` `workspace-write`.
- Pi used `pi --no-tools --no-mcp --model openai-codex/gpt-5.6-luna --thinking low -p --mode json`. RPC runs replaced `-p --mode json` with `--mode rpc`. Both passed `--session-dir` with the scratch session path.
- Antigravity CLI used `agy --input-format stream-json --output-format stream-json --model gemini-3.8-flash --effort low --print=`. Each turn arrived as a user event line. These runs used the default mode without `--mode`. iDevelop launches it with `--mode accept-edits`.

### C1 needs five additive event cases whatever protocol it selects

The current `AgentEvent` cases are `SessionStarted`, `Reported`, `Message`, `ToolStarted`, `Notice`, `Succeeded`, and `Failed`. The report proposes the cases below as additive `JsonDerivedType` entries, so existing attempt logs still replay. They are proposals for C1, not implemented code.

1. `MessageDelta(string Text)` presents text deltas from Claude Code, Codex app-server, Pi, and Antigravity CLI. Codex exec has no delta source, and `Message` stays the authoritative complete text.
2. `QuestionAsked(string RequestId, IReadOnlyList<AskedQuestion> Questions)` carries Claude Code's `can_use_tool` for `AskUserQuestion` and Codex app-server's experimental `item/tool/requestUserInput`. Questions from Pi, Antigravity CLI, and Codex exec stay as `Message` text.
3. `PermissionRequested(string RequestId, string Action, string? Detail)` carries Claude Code's `can_use_tool` for any other tool and Codex app-server's `item/commandExecution/requestApproval`. Questions and approvals render differently, so they are separate cases. The `item/fileChange/requestApproval` source comes from the generated protocol schema, not an observed event.
4. `RequestClosed(string RequestId)` comes from Codex app-server's `serverRequest/resolved` and from Claude Code's `tool_result` for the request's `tool_use_id`, which arrives after an answer and after an interrupt. Any turn-ending event also closes open requests. The composer disables a closed request, so iDevelop prevents stale answers rather than relying on clients to ignore them.
5. `Interrupted` distinguishes a person's stop from `Failed`. Sources are Codex app-server's interrupted `turn/completed`, Pi's `stopReason` `aborted`, Antigravity CLI's `result` error `interrupted`, and Claude Code's interrupt acknowledgement followed by `error_during_execution`.

`TurnEnded` and `UserMessageDelivered` are needed only if C1 keeps one client process across several turns, which in-process Stop and send and live input require. No allow, deny, or stale-response event is needed.

### Codex app-server remains a candidate for C1

Codex app-server alone supported all nine cases in this sample, with an experimental question channel. It is a candidate for C1's Codex adapter. The execution plan reserves protocol selection for C1, and Codex exec remains iDevelop's current adapter. App-server accepted `approvalPolicy` `untrusted` with `approvalsReviewer` `user` and a `workspaceWrite` sandbox with network access off. `codex exec` rejects `approval_policy=untrusted` with exit 1 and "no longer supported".

### Raw logs establish sample behavior but not general support

The raw logs establish these measured facts:

- Claude Code print mode in `default` permission mode never asks. It denied the write and wrote nothing. Only bidirectional mode with `--permission-prompt-tool stdio` exposes `can_use_tool`.
- Claude Code acknowledged an interrupt, then returned `error_during_execution`. That result alone does not say it was a stop. The interrupt rejected a pending tool request without a request cancellation notice.
- Claude Code queued input during a turn as the next turn.
- Claude Code ignored duplicate, unknown, and post-interrupt answers without a write. Codex app-server ignored duplicate and unknown answers without a write.
- Codex app-server accepted `decline` although `availableDecisions` listed `accept`, `acceptWithExecpolicyAmendment`, and `cancel`. The command ended as `declined`. Answers produced `serverRequest/resolved`.
- Codex app-server questions required `capabilities.experimentalApi` and `features.default_mode_request_user_input=true`. The server emitted a `warning` about under-development features.
- Codex app-server delivered `turn/steer` input within the running turn. `turn/interrupt` ended that turn with status `interrupted`.
- Codex exec exited 1 on SIGINT without a terminal JSON event. `codex exec resume` recalled the turn.
- Pi's `steer` ran at the next turn boundary inside the same agent run. Its `abort` ended the message with `stopReason` `aborted`, and the process still accepted the next prompt.
- Antigravity CLI 1.3.0 reported `permission_mode` `request-review` without `--mode`. It wrote `probe.txt` through `write_to_file`, an edit tool, without a permission event. This agrees with `docs/context.md`, which allows project-file edits and says Antigravity CLI blocks commands.
- Antigravity CLI's `init` tool list included `ask_question`, `ask_permission`, and `ask_custom_permission`. Print mode skipped `ask_question` within 16 ms as a `step_update` with step type `unknown`. The model's reply said the question was skipped. No input event for answers, approvals, or interrupts was confirmed. Print mode therefore offers no usable question or permission channel, and the probe did not spawn `agy-deny`.
- Antigravity CLI queued a second stdin line as the next turn. SIGINT produced a `result` with status `ERROR` and error `interrupted`, then exit 1.
- Every client resumed the same session in a new process. The process check found no leftover processes.

These claims remain inferred or proposed:

- Pi's permission, allow and deny, and stale-response verdicts use the report's citation of `docs/agent-clients.md`. Those cases did not run.
- Codex exec's question and live-input verdicts follow its protocol limits. Those cases did not run.
- Codex app-server's `item/fileChange/requestApproval` comes from the generated protocol schema. The probe observed only shell-command approval requests.
- The normalized event mapping is a design proposal for C1.
- No verdict was measured with the project's configured models or on Windows.

### Protocol limits left several cases unrun

These cases did not run:

- Codex exec question. Exec has no request channel, so a question can only be `agent_message` text. The existing `scripts/probe-clients.mjs` block case already relies on that behavior.
- Codex exec live input. Exec reads one prompt from stdin.
- Codex exec allow, deny, and stale response. The one `untrusted` run exited 1. The probe did not try `on-request` because it is not stricter than `never`.
- Pi permission, allow and deny, and stale response. The report cites `docs/agent-clients.md` for Pi's lack of a permission system.
- Antigravity CLI `agy-deny`. No answer or approval input was confirmed, so the probe did not spawn the run.
- Antigravity CLI stale response. It has no request channel.

No run covered Windows, Antigravity CLI's `accept-edits` and `plan` modes, Codex app-server file-change approvals, the project's configured models, or repeated samples.

## Changed artifacts

This documentation change touches only these repository files:

- `docs/handoffs/2026-10-06-d0-design-validation.md` records the decisions, evidence, client capability results, and remaining gates.
- `docs/product-direction.md` records D0's state and the accepted direction.
- `docs/handoffs/2026-10-05-node-system-redesign.md` records the user's two decisions on its open issues.

The evidence sits in the D0 session's throwaway scratch folder outside the repository. It is not committed and may disappear. These paths are relative to that folder:

- `ui/report.md`, `ui/index.html`, and `ui/shots/` hold the UI report and the 50-shot comparison page.
- `ui/evidence/` and `ui/tools/` hold the bounds, page renders, and capture tools.
- `ui/wt/` holds the throwaway prototype worktree on `d0/ui-prototypes`.
- `clients/results/capabilities.md`, `clients/results/raw/`, and `clients/probe-interaction.mjs` hold the client report, raw logs with metadata, and the probe. `clients/explore/protocols.md` holds delegate exploration notes.

## Commands and observed results

| Command or check | Observed result |
| --- | --- |
| UI Release build | The build reported 0 warnings. Tests and script checks did not run on the throwaway branch. |
| Inspector reproduction under Linux Xvfb | `samples/storage-change` plus five placed blueprints showed the same four unselected right edges at 280 and 520 px in both themes. Selected pickers had a 58 px right inset. |
| `ui/index.html` and its 50 shots | The page compared the three inspector layouts, expanded-card sizes and overlap, and dock and main-area conversation. Evidence includes page renders at 375 and 1280 px. |
| Fairness fixes before the UI comparison | The UI workstream attached cards before measurement to retain button styles. It removed a leaked width that laid out dark 280 px shots at 320 px. It fixed status text overflow and a dock breadcrumb that overlapped the attempt selector. |
| Source checks against `main` at `11a0b22` | The XAML constants and port locations matched the report. |
| `node probe-interaction.mjs --clients claude,codex,codex-app,pi,agy` | Six invocations recorded 30 runs, one of them skipped, `agy-deny`. No run reached its hard timeout of 150 s, 240 s for `codex-app-live`, or 90 s for `agy-question-tool`. |
| Client process check | No run left a process to kill. Each invocation ended with 0 owned processes and 0 new client processes not tied to a run. |
| Checks against the client raw logs | Antigravity CLI wrote `probe.txt` through `write_to_file`. `agy-deny` recorded its skip and never spawned. No log contains `item/fileChange/requestApproval`. The Codex app-server question run emitted the under-development feature `warning`. |
| `node scripts/check-handoffs.mjs` and `git diff --check` on this change | The handoff check reported seven records, each linked from `docs/context.md` or `docs/product-direction.md`, and exited 0. `git diff --check` reported nothing and exited 0. |

## Open issues

1. Neither workstream ran on Windows. U1 needs Segoe UI at 125% and 150% scaling in both themes. C1 needs the selected client protocols checked on Windows, where `docs/agent-clients.md` recorded its observations.
2. The UI prototypes did not test drag, focus, or typing. U1, C1, and N1 need interaction evidence against their implemented controls.
3. The Markdown renderer is not chosen. C1 must select and validate it against the plan's rendering, history, and content-safety requirements.
4. Four of 16 inspector rows use the neutral fallback icon. Model, Reasoning, Report, and Attempt have candidates Brain Circuit, Gauge, Document Text, and History. U1 must check those candidates against the pinned Fluent commit before adding them.
5. N1 must anchor ports to the header row. `src/IDevelop.Desktop/Canvas/CanvasTemplates.axaml` lines 86 and 96 center them vertically on `main`, so a taller card moves every wire. The disclosure chevron costs about 32 px of title width.
6. `docs/agent-clients.md` names Claude Code 2.1.289, Pi 1.0.1, and Antigravity CLI 1.2.16. The probe ran 2.1.291, 1.0.4, and 1.3.0. Codex is 0.160.0 in both. The page's headings and observations predate the installed versions. C1 must recheck the behavior it relies on against installed versions or record which version each observation covers.
7. Codex app-server's question channel depends on an under-development feature. C1 must not rely on it without a fallback.
8. Antigravity CLI offers no question or permission channel in print mode. Its conversation must show that limit truthfully and keep its questions as messages.

## Next action

C1 can start after W1 from the capability table, the five event cases, and the Codex app-server candidate. C1 selects each client's protocol.

U1 can start from layout A and the shared column definition. W1 can start and includes the blueprint icon and color field in its format and migration review.

Delete the local `d0/ui-prototypes` branch and its worktree once U1 no longer needs them. The branch is never pushed or merged.
