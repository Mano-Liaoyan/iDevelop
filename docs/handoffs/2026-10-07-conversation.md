# Conversation

## Task

C1 is the slice "Build conversation and attention" of the workspace milestone the owner approved on 2026-10-06. Its completion condition is that the selected task has durable, correctly routed history, Markdown, a composer, structured requests, and honest client capability limits. The inspector and compact-card attention open the same conversation.

Pull request [#40](https://github.com/Mano-Liaoyan/iDevelop/pull/40) built the client protocols and the permission policy, and pull request [#42](https://github.com/Mano-Liaoyan/iDevelop/pull/42) built the view. The engine of [ADR 0042](../adr/0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md) serves each node's conversation from its log through `IConversation`.

## Decisions and reasons

### iDevelop renders Markdown with its own view on Markdig

`MarkdownView` is iDevelop's own renderer on Markdig 1.4.0, which is BSD-2-Clause with no dependencies. It parses with `UsePipeTables().DisableHtml()` and maps blocks to Avalonia controls. A streamed message rebuilds only its changed blocks, and a block with selected text waits until the selection clears. History is a virtualized list.

A prototype measured three candidates on Linux with a Release build under Xvfb with software rendering. Each stream paced one 16-character chunk to each animation frame. CPU per append counts all threads. Each timing is the median of five runs, or of three for the 20 KB stream. The 500-message history used a virtualized list.

| Candidate | CPU per append, 5 KB | UI-thread p95 per append | 20 KB stream, frames over 33 ms | 500 messages to first frame | Opened at the latest message |
| --- | --- | --- | --- | --- | --- |
| Markdown.Avalonia.Tight 11.0.3 | 26.5 ms | 33.2 ms | 643 | 191 ms | 5 of 5 |
| LiveMarkdown.Avalonia 1.12.2 | 11.4 ms | 0.2 ms | 1 | 364 ms | 1 of 5 |
| Own renderer on Markdig 1.4.0 | 8.5 ms | 3.9 ms | 0 | 111 ms | 5 of 5 |

The own renderer finished its 20 KB stream in 14.5 s, against 20.9 s at one append per frame, so some of its appends shared a frame. Its CPU per append may therefore be understated by up to about 30%. That bound is inferred from the wall time. Its UI-thread time is measured per append and is not affected.

Without virtualization, the own renderer took 2.7 s and 568 MB for the same history, and the others took longer. Virtualization is therefore required whatever the renderer.

The own renderer and Markdown.Avalonia.Tight both pass the repository's license rules without a new decision. The own renderer shows HTML as literal text, fetches no images, and sends every click to iDevelop's handler. Markdown.Avalonia.Tight lost on performance and link behavior. It missed the frame budget while streaming and launched links on a plain left click. LiveMarkdown.Avalonia embeds TextMate grammars under licenses outside the permissive list, drops HTML text silently, fetches remote images, and failed to open history at the latest message in 4 of 5 runs. Avalonia.Controls.Markdown depends on Avalonia's commercial licensing package. The renderer has no syntax highlighting and no selection across blocks.

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

Without the limits, a 3,000-row table blocked the window for 5.8 s and 10,000 links for 5.7 s. The cell and link limits sit near 165 ms. The length limit keeps ordinary long replies as Markdown, at the cost of about 530 ms for pathological nesting just under 20,000 characters.

The source view wraps with `WrapWithOverflow`, because ordinary wrapping of a long unbroken run took time that grew with its square, 585 ms at 25,000 characters and 8.7 s at 100,000. With the limits, 17 hostile probes each finished under 0.9 s. In the real window under Xvfb, 1,999 empty code blocks took 4.5 s without the block weight limit and 420 ms with it.

### Each client keeps one process per turn, with its own protocol

One client process runs each turn, and a node that waits for the person's message holds no process. The [agent client reference](../agent-clients.md#conversation-protocols-from-c1) lists the exact launches.

| Client | Protocol |
| --- | --- |
| Claude Code | Stream JSON in both directions with partial messages. iDevelop sends an `initialize` control request and then the user message, keeps stdin open for answers, denials, and `interrupt`, and closes it at the `result` event. |
| Codex | `codex app-server`, one process per turn. iDevelop sends `initialize` and `initialized`, then `thread/start` for a new session or `thread/resume` with `excludeTurns: true`, then `turn/start`. It closes stdin at `turn/completed`. |
| Pi | `-p --mode json`. The prompt goes over stdin, which then closes. |
| Antigravity CLI | Print mode with stream JSON. Its print mode skipped `ask_question` in D0's probe. |

Codex runs app-server because it alone supported all nine of D0's interaction cases. Claude Code raises `can_use_tool` only in this bidirectional mode. Pi's RPC mode adds only steering, and it also relays the user's Pi extensions.

For Claude Code and Codex, Stop and send and Stop first send the client's own interrupt, keep reading until the turn's final result, and then stop the process tree. Pi and Antigravity CLI have no interrupt, so iDevelop stops their process tree at once. A stop gives the client 5 seconds, `CliAgent.ShutdownTime`, to exit. A Claude Code or Codex client that reported success gets 60 seconds, `CliAgent.SuccessExitTime`, to exit after its input closes. Pi and Antigravity CLI have no deadline after success.

### Questions surface, and permission requests are declined or never asked

`ClientPolicy.For` is the one table that launch arguments, request handling, and capability text read.

| Turn | Claude Code launch | Questions | Permission requests |
| --- | --- | --- | --- |
| Autonomous, any access | `--permission-prompts none` | Declined | Declined |
| Review node | `--permission-mode plan --permission-prompts none` | Declined | Declined |
| May ask or Chat | `--permission-prompt-tool stdio` | Surfaced | Declined, including `ExitPlanMode` |

Access picks `plan`, or `bypassPermissions` for an editing node, which then runs every tool without asking ([ADR 0039](../adr/0039-claude-code-in-an-editing-node-runs-with-bypass-permissions.md)). A question channel grants no extra tool permission. Surfacing permissions in May ask would relax the command rules by conversation mode, and built-in Plan and Architect are read-only May ask nodes that could reach an `ExitPlanMode` approval. Codex always runs with approval policy `never`, reviewer `user`, the access sandbox, and `features.default_mode_request_user_input=false`, so its questions arrive as message text. A declined permission closes as `Denied` and needs no action from the person. The view shows the exact action and that iDevelop denied it, with no Allow button.

### A Claude Code question waits in its running turn

When a May ask or Chat turn of Claude Code asks a structured question, its turn stays alive and the node is input-required with the question and its choices. `WorkflowEngine.Answer` sends the person's answer back into that turn under a key the engine owns. The node's log records each question and answer, and a question still waiting when the app closes reopens as an interrupted turn.

### The view keeps one draft per task in the open-project session

The [workspace session](2026-10-07-workspace-sessions.md) owns conversation view state, keyed by workflow and task. The conversation takes the main area by default, or a dock. **Open conversation** in the inspector and the state glyph on a card that waits or failed open it. The composer keeps one draft per task across task, workflow, project, and dock switches, and the inspector's composer shows the same draft. Send clears the text only after iDevelop recorded the message, and only if the person did not change it meanwhile. A line under the header states what the client cannot do here. Unsent drafts do not survive a restart of the app.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| Markdown.Avalonia.Tight or LiveMarkdown.Avalonia | Measured above. One misses the frame budget and launches links on click. The other needs a license decision, drops HTML text, and fetches images. |
| Surface permission requests in May ask and Chat | It relaxes command rules by conversation mode and could approve `ExitPlanMode` for a read-only node. |
| Separate card and inspector histories | They duplicate routing and drafts. |
| Persist every streamed delta | Complete messages define durable history. One Pi turn produced 492 deltas. |
| A persistent client process across turns | It would hold a process while the node waits for the person's message. |

## Open issues

1. Real-client checks still need the user's approval, because each spends subscription quota. Claude Code needs `AskUserQuestion` under `bypassPermissions` and `plan`, a denied `ExitPlanMode`, Autonomous and review turns with `--permission-prompts none`, an interrupt with trailing output, and the exit time of a client with SessionEnd hooks. Codex needs app-server with the question feature off, and fresh and resumed read-only turns.
2. No client ran on Windows, so npm shims holding stdin open, shutdown after the final result, Job Object stopping, and continuation are unverified. The Windows recipe for the view has not run.
3. The renderer has no syntax highlighting and no selection across blocks. It was not measured on Windows, at 125% or 150% scaling, or with a screen reader.
