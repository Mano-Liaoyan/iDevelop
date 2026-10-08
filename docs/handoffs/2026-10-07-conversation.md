# Conversation

## Task

C1 is the slice "Build conversation and attention" of the [approved execution plan](2026-10-06-workspace-execution-plan.md#approved-delivery-slices). Its completion condition is that the selected task has durable, correctly routed history, Markdown, a composer, structured requests, and honest client capability limits. The inspector and compact-card attention open the same conversation.

C1 shipped as two pull requests. Pull request [#40](https://github.com/Mano-Liaoyan/iDevelop/pull/40) is C1a, the backend, merged as `7a7789e`. It moves Claude Code and Codex to protocols that carry questions and interrupts, adds the permission policy, and gives each task an `IConversationSession` through `ProjectRuns.OpenConversation`. Pull request [#42](https://github.com/Mano-Liaoyan/iDevelop/pull/42) is C1b, the view, merged as `ea66404`. It changes no C1a execution code.

A throwaway prototype compared the Markdown renderers and read D0's raw client logs before design. A fresh Opus review of the design note made five findings, and the revised note accepted all five. GPT-6.1 Sol wrote C1a through Codex. An Opus backend review and a GPT-6 Astra difficult-task review reviewed it in six rounds. A fresh Opus frontend verification reviewed C1b and its fix round.

## Decisions and reasons

### iDevelop renders Markdown with its own view on Markdig

`MarkdownView` is iDevelop's own renderer on Markdig 1.4.0, which is BSD-2-Clause with no dependencies. It parses with `UsePipeTables().DisableHtml()` and maps blocks to Avalonia controls. A streamed message rebuilds only its changed blocks, and a block with selected text waits until the selection clears. History is a virtualized list.

The prototype measured three candidates on Linux with a Release build under Xvfb with software rendering. Each stream paced one 16-character chunk to each animation frame. CPU per append counts all threads. Each timing is the median of five runs, or of three for the 20 KB stream. The 500-message history used a virtualized list.

| Candidate | CPU per append, 5 KB | UI-thread p95 per append | 20 KB stream, frames over 33 ms | 500 messages to first frame | Opened at the latest message |
| --- | --- | --- | --- | --- | --- |
| Markdown.Avalonia.Tight 11.0.3 | 26.5 ms | 33.2 ms | 643 | 191 ms | 5 of 5 |
| LiveMarkdown.Avalonia 1.12.2 | 11.4 ms | 0.2 ms | 1 | 364 ms | 1 of 5 |
| Own renderer on Markdig 1.4.0 | 8.5 ms | 3.9 ms | 0 | 111 ms | 5 of 5 |

The own renderer finished its 20 KB stream in 14.5 s, against 20.9 s at one append per frame, so some of its appends shared a frame. Its CPU per append may therefore be understated by up to about 30%. That bound is inferred from the wall time. Its UI-thread time is measured per append and is not affected.

Without virtualization, the own renderer took 2.7 s and 568 MB for the same history, and the others took longer. Virtualization is therefore required whatever the renderer.

The own renderer and Markdown.Avalonia.Tight both pass the repository's license rules without a new decision. The prototype checked them with an adapted copy of `scripts/check-licenses.mjs`. The own renderer shows HTML as literal text, fetches no images, and sends every click to iDevelop's handler. Markdown.Avalonia.Tight lost on performance and link behavior. It missed the frame budget while streaming and launched links on a plain left click. LiveMarkdown.Avalonia embeds TextMate grammars under licenses outside the permissive list, drops HTML text silently, fetches remote images, and failed to open history at the latest message in 4 of 5 runs. Avalonia.Controls.Markdown depends on Avalonia's commercial licensing package. The renderer has no syntax highlighting and no selection across blocks.

### Rendering never fetches, runs, or opens anything by itself

HTML shows as literal text. An image shows its description and address and is never fetched. Every link activation, by click, key, or context menu, goes through `ConversationLinkRouter`. Only an absolute http or https address opens, after a confirmation that shows the actual address. Any other address can only be copied. Code blocks have **Copy** in a header row.

### Large messages show as their source

A message shows as its source text instead of Markdown once it passes any of four limits in `MarkdownView`. The limits are 20,000 characters, 1,000 table cells, 500 links, and a block weight of 1,000. A code block weighs 10, a table weighs 1 because its cells have their own limit, and any other block weighs 1.

The limits come from headless layout of one message in a 700 px window, best of three after a warm-up.

| Content | Measured layout time |
| --- | --- |
| Table cells, 10 columns | 500 cells 142 ms, 1,000 cells 165 ms, 2,000 cells 368 ms, 5,000 cells 854 ms |
| Links | 200 links 24 ms, 500 links 165 ms, 1,000 links 225 ms |
| Ordinary prose | 16,000 characters 26 ms, 80,000 characters 139 ms |
| Nested brackets, the slowest parse | 10,000 characters 164 ms, 19,982 characters 530 ms |

Before the limits, a 3,000-row table blocked the window for 5.8 s and 10,000 links for 5.7 s. The cell and link limits sit near 165 ms. The length limit keeps ordinary long replies as Markdown, at the cost of about 530 ms for pathological nesting just under 20,000 characters.

The source view wraps with `WrapWithOverflow`, because ordinary wrapping of a long unbroken run took time that grew with its square, 585 ms at 25,000 characters and 8.7 s at 100,000. With the limits, 17 hostile probes from the Opus verification each finished under 0.9 s. In the real window under Xvfb, 1,999 empty code blocks took 4.5 s before the block weight limit and 420 ms after it.

### Each client keeps one process per turn, with its own protocol

One client process still runs each turn, and a node that waits between turns holds no process and no lock. The [agent client reference](../agent-clients.md#conversation-protocols-from-c1) lists the exact launches.

| Client | Protocol |
| --- | --- |
| Claude Code | Stream JSON in both directions with partial messages. iDevelop sends an `initialize` control request and then the user message, keeps stdin open for answers, denials, and `interrupt`, and closes it at the `result` event. |
| Codex | `codex app-server`, one process per turn. iDevelop sends `initialize` and `initialized`, then `thread/start` for a new session or `thread/resume` with `excludeTurns: true`, then `turn/start`. It closes stdin at `turn/completed`. |
| Pi | `-p --mode json` as before. The prompt goes over stdin, which then closes. |
| Antigravity CLI | Print mode with stream JSON as before. Its print mode skipped `ask_question` in D0's probe. |

Codex moved to app-server because it alone supported all nine of D0's interaction cases. Claude Code raised `can_use_tool` only in this bidirectional mode. Pi's RPC mode adds only steering, and it also relays the user's Pi extensions.

For Claude Code and Codex, Stop and send and Cancel first send the client's own interrupt, keep reading until the turn's final result, and then stop the process tree. Pi and Antigravity CLI have no interrupt, so iDevelop stops their process tree at once. A stop or a deferral gives the client 5 seconds, `ProjectRuns.ShutdownTime`, to exit. A Claude Code or Codex client that reported success gets 60 seconds, `ProjectRuns.SuccessExitTime`, to exit after its input closes. Pi and Antigravity CLI have no deadline after success. A fake client that exited 6.5 seconds after its input closed failed both Claude Code and Codex turns under the old 5 second limit.

### Questions surface, and permission requests are declined

`ClientPolicy.For` is the one table that launch arguments, request handling, and capability text read.

| Turn | Claude Code launch | Questions | Permission requests |
| --- | --- | --- | --- |
| Autonomous, any access | `--permission-prompts none` | Declined | Declined |
| Review node | `--permission-mode plan --permission-prompts none` | Declined | Declined |
| May ask or Chat, host questions on | `--permission-prompt-tool stdio` | Surfaced | Declined, including `ExitPlanMode` |
| Host questions `Disabled` | `--permission-prompts none` | Declined | Declined |

Access still picks `plan` or `acceptEdits`, and a question channel grants no extra tool permission. The design review found that surfacing permissions in May ask would relax the command rules by conversation mode, and that built-in Plan and Architect are read-only May ask nodes that could reach an `ExitPlanMode` approval. Codex always runs with approval policy `never`, reviewer `user`, the access sandbox, and `features.default_mode_request_user_input=false`, so its questions arrive as message text. A declined permission closes as `Denied` and needs no action from the person. The view shows the exact action and that iDevelop denied it, with no Allow button.

### A structured question is deferred at once into a text question

The recorded rule is that a waiting node holds no process and no lock. A live structured question can be answered only while its turn's process runs. The desktop therefore opens each runner with `HostQuestions.DeferImmediately`.

When a May ask or Chat turn asks a structured question, iDevelop records the unanswered questions as text, interrupts the turn, waits up to `ShutdownTime` for its exit, and leaves the attempt waiting with no process and no lock. Send stays disabled until the deferred turn has released its process and lock. The person's next message resumes the same session in the same attempt. Any answer the person had started moves into the composer once. A question that arrives while a deferral stops the turn closes as `PolicyDenied`.

`HostQuestions.Bounded.Recommended` holds the process and the task lock for up to 55 seconds to answer plus 5 seconds to shut down, then defers by the same path. It stays off until the user confirms that timeout.

### One published record per task, paired with its log revision

`ProjectRuns.Latest` is the only published record of each task. Each published record and its log revision form one private value, `PublishedAttempt`. Every writer sets both through `SetPublished`. A run's live record is private to the run. Other readers see its task, title, attempt ID, and live requests. The conversation snapshot, the attempt list, `Current`, notification subjects, and Cancel all read the published pair. The compiler therefore rejects a new reader of a run's live status. Both lock-take rereads keep the published pair of every task that this window runs. `Finish` publishes the settled record once, under the same lock, as the run leaves `_active`.

This fixed a race. A run published its waiting or cancelled record while it still disposed its turn and held the task lock. The gap took about 0.02 ms when idle and up to 700 ms under load. In that gap, Mark done was dropped silently and Send was refused. A subject freed by deleting its review was still refused with `AlreadyRunning`. A 1 s gap injected by the coordinator reproduced both macOS CI timeouts. The same root cause made a review test fail with `AlreadyRunning` on `main`.

The fix took three rounds. Round 4 kept a run's published record running until teardown ended. Round 5 stopped a lock-take reread from replacing a held run's record with its waiting one. It also moved the snapshot and the attempt list to the published record. Round 6 paired the record with its own log revision. Before round 6, the snapshot showed `Running` at the final revision and then `WaitingForInput` at that same revision. A consumer that reloads attempts only when the revision changes kept `Running` until the next log change. C1b's attempt picker is such a consumer.

### The view keeps one draft per task in the open-project session

The [workspace session](2026-10-07-workspace-sessions.md) owns conversation view state, keyed by workflow and task. The conversation takes the main area by default, or a dock. **Open conversation** in the inspector and the state glyph on a card that waits or failed open it. The composer keeps one draft per task across task, workflow, project, and dock switches, and the inspector's composer shows the same draft. Send clears the text only after iDevelop recorded the message, and only if the person did not change it meanwhile. A line under the header states what the client cannot do here. An earlier attempt is read-only. Unsent drafts do not survive a restart of the app.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| Markdown.Avalonia.Tight or LiveMarkdown.Avalonia | Measured above. One misses the frame budget and launches links on click. The other needs a license decision, drops HTML text, and fetches images. |
| Hold a structured question until the person answers | A waiting node would hold a process and the task lock, against the recorded rule. |
| Surface permission requests in May ask and Chat | It relaxes command rules by conversation mode and could approve `ExitPlanMode` for a read-only node. |
| Separate card and inspector histories | They duplicate routing and drafts. |
| Persist every streamed delta | Complete messages define durable history. One Pi turn produced 492 deltas. |
| A persistent client process across turns | It would hold a process while the node waits. |

## Changed artifacts

- `src/IDevelop.Core/Execution/ClientPolicy.cs`, `TurnProtocol.cs`, `Clients/ClaudeProtocol.cs`, and `Clients/CodexProtocol.cs` hold the policy and the per-turn protocols.
- `src/IDevelop.Core/Execution/ConversationContract.cs`, `ConversationHistory.cs`, `ConversationPager.cs`, and `ProjectRuns.Conversation.cs` hold the session contract and history paging. `ProjectRuns.cs` and `ProjectRuns.ActiveRun.cs` hold the drain and the published records.
- `src/IDevelop.Desktop/Conversation/` holds `MarkdownView`, `ConversationLinkRouter`, the view, its view model, and attention.
- `Directory.Packages.props` pins Markdig 1.4.0, and `THIRD-PARTY-NOTICES.md` carries its license.
- `docs/agent-clients.md` gains the C1 protocols section.
- `.claude/skills/verify-idevelop/features/conversation-view.md` is the Windows recipe for the view.
- `tests/Shared/Fixtures/c1/` holds client streams from D0's logs, with paths, accounts, and IDs removed.

## Commands and observed results

| Command or check | Observed result |
| --- | --- |
| `dotnet build -c Release` at C1a `22e0ac2` | 0 warnings. |
| `dotnet test -c Release` at C1a `22e0ac2` | Core passed 528 tests with 9 skipped. Desktop passed 357. The same passed with a test hook that delays every run's teardown by 300 ms. |
| Each C1a review fix | Each has a test that failed on the previous head and passes after the fix. |
| `A_revision_cached_attempt_selector_reloads_the_waiting_status_when_teardown_finishes` | Before round 6 it expected `WaitingForInput` and got `Running`. After it, the held and final revisions are 5 and 6. |
| The two macOS CI tests under CPU load, 3 CPUs and 6 busy loops | 10 of 10 runs passed after round 5. |
| `ProjectRuns.DisposeAsync` from a UI thread that does not pump | It never completed in 25 s before round 2, and completes in 0.04 s after it. |
| Each C1b fix-round test | Each failed with its fix reverted and passes with it. |
| Landing check of C1b at `15a2c2d` | Two conflicts with U1, in the icon manifest and the skill, resolved as the integration probe did. Build with 0 warnings. Core passed 529 with 9 skipped, and Desktop passed 392. The license, token, icon, handoff, and PStack checks pass. |
| Real window on Linux under Xvfb | Fake Claude Code and Codex shims only. Streaming, a deferred question, card attention, the dock, and both themes render. `xdg-open` was never called. |

## Open issues

1. Real-client checks still need the user's approval, because each spends subscription quota. Claude Code needs `AskUserQuestion` under `acceptEdits` and `plan`, a denied `ExitPlanMode`, Autonomous and review turns with `--permission-prompts none`, an interrupt with trailing output, resume after a deferred question, and the exit time of a client with SessionEnd hooks. Codex needs app-server with the question feature off, `thread/resume` with `excludeTurns` of a session that `codex exec` created, and fresh and resumed read-only turns.
2. No client ran on Windows, so npm shims holding stdin open, shutdown after the final result, Job Object stopping, and continuation are unverified. The Windows recipe for the view has not run.
3. The bounded live hold for structured questions waits for the user's confirmation of its timeout.
4. A log-write failure can change a run's in-memory status without advancing its durable revision, at `ProjectRuns.ActiveRun.cs`. The Astra review found it and marked it as older than C1a.
5. The inspector's older **Talk to the Agent** transcript stays beside **Open conversation**. Removing it was left until U1 landed, and U1 has landed.
6. The renderer has no syntax highlighting and no selection across blocks. It was not measured on Windows, at 125% or 150% scaling, or with a screen reader.

## Next action

E3 routes run-owned conversations through the same session, as the [workflow execution design](2026-10-07-workflow-execution-design.md) describes. N1 later adds the compact conversation on expanded cards.
