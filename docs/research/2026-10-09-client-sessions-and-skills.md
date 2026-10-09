# Client sessions and skills

Research for [#92](https://github.com/Mano-Liaoyan/iDevelop/issues/92) (keep one agent session per conversation) and [#93](https://github.com/Mano-Liaoyan/iDevelop/issues/93) (choose skills per node), 2026-10-09.

## Scope and method

The questions are what each client's session resume does, whether a client can keep one live process across turns, how each client finds skills, and whether a launch can choose them. This note records facts and their sources. It decides nothing. The choices it leaves to the owner are listed [at the end](#choices-for-the-owner).

No real client ran with a prompt for this note, because that needs the owner's approval. The sources are:

- each installed client's `--help` and version output;
- the documentation and changelogs that the clients ship;
- their official online documentation and source;
- the protocol bindings that `codex app-server generate-ts` writes without a model call;
- the structure of existing session files (their keys and ids, never their contents);
- iDevelop's own code and records.

A claim that rests on inference rather than a source says so.

Installed versions on this Linux machine on 2026-10-09:

| Client | Version | Earlier versions in iDevelop's records |
| --- | --- | --- |
| Claude Code | 2.1.295 | 2.1.289 in [agent-clients.md](../agent-clients.md), 2.1.291 in D0 |
| Codex | codex-cli 0.160.1 | 0.160.0 |
| Pi | 1.1.0 | 1.0.1, and 1.0.4 in D0 |
| Antigravity CLI | 1.3.2 | 1.2.16, and 1.3.0 in D0 |

In this note, "resume" always means a client's own session resume, such as `claude --resume`. The [glossary](../../GLOSSARY.md)'s Resume is a run permission, and this note never uses the word that way.

## Summary

| Client | What iDevelop's resume does today | One live process across turns | Per-node skill choice |
| --- | --- | --- | --- |
| Claude Code | The same session id and transcript file. No fork without `--fork-session`. | Supported. `-p --input-format stream-json` takes one user line per turn while stdin stays open. | Allow-list: `skills` in the `initialize` control request iDevelop already sends. |
| Codex | The same thread id and rollout file. `thread/fork` is a separate method. | Supported. One app-server takes several `turn/start` calls on one thread. | Deny-list only: `-c skills.config=[...]` per launch, after listing skills with `skills/list`. |
| Pi | The same session id and file, but only when the turn runs in the same folder. Otherwise Pi starts an empty session under that id. | Supported in RPC mode (`--mode rpc`), which iDevelop does not use for turns. | Allow-list: `--no-skills --skill <path>` per launch. |
| Antigravity CLI | The same conversation id, per its docs and D0. | Supported for input. It has no stdin interrupt, so a stop ends the process. | No per-run control. A generated custom agent (`--agent`) might work, but that is unverified. |

Every client's resume already continues its own session. None of them copies the history into a new session unless asked to. Two things can still make a conversation look as if it starts again:

- A new client process per turn. Every turn starts the client again, reloads the stored history, and re-runs startup work such as hooks, MCP servers, and plugins.
- iDevelop's own attempts. A message to a task that is not running starts a new attempt that continues the earlier attempt's session (see [What iDevelop does today](#what-idevelop-does-today)). The conversation view then shows the earlier attempts above a "New attempt" marker. That probably explains much of what the owner described, but no one has confirmed it with the owner.

## What iDevelop does today

- Each turn is one client process. `LaunchPlan.Resuming` sends the person's message alone, with the session id ([`StartCheck.cs`](../../src/IDevelop.Core/Execution/StartCheck.cs)). No iDevelop prompt copies earlier messages into a resumed turn. A Retry fix is the exception: it starts a fresh session on purpose, and its prompt carries the ticket and the change so far ([ADR 0013](../adr/0013-an-interrupted-fix-waits-for-the-person-s-choice.md)).
- The launches:
  - Claude Code: `--resume <id>` ([`ClaudeCode.cs`](../../src/IDevelop.Core/Execution/Clients/ClaudeCode.cs)).
  - Codex: `thread/resume` with `threadId` and `excludeTurns: true`, then one `turn/start` ([`CodexProtocol.cs`](../../src/IDevelop.Core/Execution/Clients/CodexProtocol.cs)).
  - Pi: `-p --mode json --session-id <id>` ([`Pi.cs`](../../src/IDevelop.Core/Execution/Clients/Pi.cs)).
  - Antigravity CLI: `--conversation <id>` with one stdin line ([`Antigravity.cs`](../../src/IDevelop.Core/Execution/Clients/Antigravity.cs)).
- Claude Code and Codex close stdin at the turn's terminal event (`result`, `turn/completed`), so the process exits ([agent-clients.md](../agent-clients.md#conversation-protocols-from-c1)).
- A message to a task that is not running starts a new attempt whose first turn continues the latest attempt's session ([context.md](../context.md#working-decisions)). `ProjectRuns.EarlierAttempts` chains those attempts ([`ProjectRuns.cs`](../../src/IDevelop.Core/Execution/ProjectRuns.cs)). The conversation view shows each one under a "New attempt" marker and lists them in an attempt picker ([`ConversationItems.cs`](../../src/IDevelop.Desktop/Conversation/ConversationItems.cs), [`ConversationView.axaml`](../../src/IDevelop.Desktop/Conversation/ConversationView.axaml)). Only a reply to a waiting attempt, in May ask or Chat mode, is the next turn of the same attempt ([glossary](../../GLOSSARY.md): Attempt, Continuation).

### What the probes recorded

- **2026-10-04, [`scripts/probe-clients.mjs`](../../scripts/probe-clients.mjs).** Each client continued its session in a new process with the id it had reported itself. No resumed turn reported a different id ([agent-clients.md](../agent-clients.md#sessions)).
  - The probe compares only the reported id. It counts a resumed turn that reports no id as the same session, and it never looked at the client's session store.
  - The Codex case ran `codex exec resume`, not app-server `thread/resume`.
- **D0, 2026-10-06.** Case 8, "resume in new process", passed for all five launch modes as "same session recalled".
  - Case 6, live input: Claude Code queued input as the next turn, Codex app-server took `turn/steer` in the same turn, Pi's `steer` ran inside the same agent run, and Antigravity CLI queued a second stdin line as the next turn.
  - Case 7, Stop and send in the same process, passed for Claude Code, Codex app-server, and Pi.
  - D0 noted that `TurnEnded` and `UserMessageDelivered` events are needed only if one client process is kept across turns ([D0 record](../handoffs/2026-10-06-d0-design-validation.md#the-client-probe-found-structured-requests-only-in-claude-code-and-codex-app-server)).
- **Claude Code's session files.** The Claude Code 2.1.289 probes on 2026-10-04 and 2026-10-05 left seven scratch folders. Each holds a single transcript, and every entry in it carries the file's own session id, although each resume case ran two processes. So a resumed `-p` turn appended to the same file rather than writing a copy. That is inferred from the file structure; no contents were read.
- **Codex's rollout files.** Two Codex 0.160.1 rollout files that iDevelop wrote on 2026-10-09 each hold four turns under one `session_meta`. iDevelop starts one process per turn, so each resumed turn appended to the same file. This too is inferred from the file structure alone.

## Sessions by client

### Claude Code 2.1.295

**Resume keeps the session.**

- "Continue and resume both pick up an existing session and add to it". A fork "creates a new session that starts with a copy of the original's history" and gets its own id ([Agent SDK sessions](https://code.claude.com/docs/en/agent-sdk/sessions)).
- `--fork-session` reads "When resuming, create a new session ID instead of reusing the original" (`claude --help`). iDevelop never passes it.
- Transcripts live at `~/.claude/projects/<project>/<session-id>.jsonl` ([sessions](https://code.claude.com/docs/en/sessions#where-transcripts-are-stored)).
- `--resume <id>` looks first in the current project folder and its Git worktrees, then in every other project. An id found in two other projects counts as not found ([sessions](https://code.claude.com/docs/en/sessions#resume-a-session)).
- "If you resume the same session in two terminals without forking, messages from both interleave into one transcript" ([sessions](https://code.claude.com/docs/en/sessions#branch-a-session)).

**A resume restores the conversation, not the launch.**

- The resumed session restores the history, model, agent, and some modes.
- "If the session depended on `--mcp-config`, `--settings`, `--plugin-dir`, `--fallback-model`, or directories added with `--add-dir`, pass them again when you resume" ([sessions](https://code.claude.com/docs/en/sessions#what-a-resumed-session-restores)). Any per-node configuration therefore goes on every turn's launch.
- A `-p` resume starts in the permission mode a new `-p` run would start in, unless the plan-mode conditions apply ([permission mode on resume](https://code.claude.com/docs/en/sessions#permission-mode-on-resume)).
- `--system-prompt-snapshot` defaults to `on`. The system prompt is recorded on the first request and sent as-is on every later request and resume, until compaction (`claude --help`).

**A live session is the SDK's preferred mode.**

- The SDK's streaming input mode "allows the agent to operate as a long lived process that takes in user input, handles interruptions, surfaces permission requests, and handles session management". It supports "queued messages" that "process sequentially, with ability to interrupt" ([streaming input](https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode)).
- The CLI side is `-p --input-format stream-json`, which iDevelop already uses. The process emits one `result` per turn and waits for the next `user` line while stdin is open.
- The 2.1.265 changelog fixed stream-json sessions "resetting the shell working directory at each new user message", which confirms several user messages in one process ([CHANGELOG](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md)).
- The Agent SDK 0.3.295, which bundles Claude Code 2.1.295, defines control requests that a live session would use between turns: `interrupt`, `set_model`, `set_permission_mode`, and `apply_flag_settings` ([`sdk.d.ts`](https://www.npmjs.com/package/@anthropic-ai/claude-agent-sdk/v/0.3.295)). `--replay-user-messages` echoes each stdin user message back for acknowledgement (`claude --help`).
- After stdin closes, a `-p` run may stay open while background work it started finishes, with a default cap of 10 minutes ([background tasks at exit](https://code.claude.com/docs/en/headless#background-tasks-at-exit)). iDevelop's 60-second limit after success can cut that wait short ([agent-clients.md](../agent-clients.md#conversation-protocols-from-c1)).

**Cost.** A live process does not stop the model from reading the history. Every request carries the conversation, and the prompt cache makes repeats cheap while it stays warm.

- After about an hour idle, "the session's prompt cache has expired by then, so the next request processes the full history once" ([resume from a summary](https://code.claude.com/docs/en/sessions#resume-from-a-summary)).
- The 2.1.90, 2.1.248, and 2.1.277 changelogs fix resumes that caused "a full prompt-cache miss", once "on the resumed session's first turn". So the first turn of a resume has missed the cache in edge cases.

### Codex 0.160.1 (app-server)

**`thread/resume` keeps the thread.**

- It "reopen[s] an existing thread by id so later `turn/start` calls append to it". `thread/fork` instead "fork[s] a thread into a new thread id by copying stored history" ([app-server docs](https://learn.chatgpt.com/docs/app-server), where developers.openai.com/codex/app-server now redirects).
- `excludeTurns` returns "only thread metadata and live-resume state without populating `thread.turns`". It shapes the response, not what the model sees (`ThreadResumeParams` in the generated bindings).
- Resuming a thread that this server is already running "rejoins that thread" and ignores `config`, `baseInstructions`, and `developerInstructions`, with a warning (`ThreadResumeParams`; `thread_processor.rs` in [openai/codex rust-v0.160.1](https://github.com/openai/codex/tree/rust-v0.160.1/codex-rs/app-server)).
- `codex exec resume [SESSION_ID] [PROMPT]` continues an `exec` session (`codex exec resume --help`).

**One app-server can run many turns.**

- `turn/start` takes a `threadId` and per-turn overrides of model, effort, cwd, sandbox, and approvals, which "become the defaults for later turns on the same thread" ([app-server docs](https://learn.chatgpt.com/docs/app-server)).
- `turn/steer` adds input to the running turn "without creating a new turn". `turn/interrupt` ends the turn, and the process lives on (D0).
- A loaded thread stays loaded "until it has no subscribers and no thread activity for 30 minutes".
- End of stdin closes the connection. On Unix a watchdog then exits the process after 45 seconds ([`stdio.rs`, rust-v0.160.1](https://github.com/openai/codex/blob/rust-v0.160.1/codex-rs/app-server-transport/src/transport/stdio.rs)). That is why iDevelop's process per turn ends today.
- 0.160.1 also has a shared background app-server (`codex app-server daemon`, `codex agents`, and the top-level `--no-daemon`). iDevelop's stdio server is a separate process. Whether `codex resume <id>` in a terminal goes through the daemon while iDevelop holds the same thread is unverified.

### Pi 1.1.0

**`--session-id` continues the session only in the same folder.**

- `--session-id <id>` opens the session with that id in the current project, or creates it ([`docs/cli.md`, v1.1.0](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/cli.md)). Sessions are JSONL files under `~/.pi/agent/sessions/--<cwd>--/` ([`docs/session-format.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/session-format.md)).
- The lookup searches only the folder for the current working directory, unless `--session-dir` names another. When it finds nothing, Pi prints "No project session found with id '…'; creating a new session with that id" to stderr and starts an empty session under the same id ([`src/main.ts`, v1.1.0](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/src/main.ts)).
- iDevelop passes no `--session-dir`. A Pi turn that runs in another folder than the session's first turn therefore keeps the id and loses the history, and no probe would catch it by comparing ids. That is inferred from the code, not observed.
- Forks are explicit: `--fork <path|id>`, and `/fork` or `/clone` inside a session ([`docs/cli.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/cli.md)).

**RPC mode is a documented long-lived process.**

- `pi --mode rpc` keeps one session and takes many `prompt` commands. A `prompt` during a run needs `streamingBehavior` `steer` or `followUp`; separate `steer`, `follow_up`, and `abort` commands also exist ([`docs/rpc.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/rpc.md)).
- Other commands include `new_session`, `switch_session`, `fork`, and `get_state`, which returns `sessionId` and `sessionFile`.
- A turn ends at `agent_settled`. Closing stdin shuts the process down.
- `-p --mode json`, which iDevelop uses, runs one prompt per process.
- Whether `--mode rpc --session-id <id>` appends to the same file across prompts is inferred from the code and unprobed.

### Antigravity CLI 1.3.2

**`--conversation <id>` continues the conversation.**

- The headless docs offer "`--conversation` with an ID from a previous run's `conversation_id`" and say "each of these starts a new process" ([headless mode](https://antigravity.google/docs/cli/headless)).
- D0 saw the same id come back. Each conversation is one SQLite file under `~/.gemini/antigravity-cli/conversations/` (file names only were listed).
- Forking exists only as the interactive `/fork` ([CLI reference](https://antigravity.google/docs/cli/reference)). `agy --help` has no fork flag.

**Stream-json input keeps one conversation open.**

- `--input-format stream-json` "reads one NDJSON message per line from stdin and runs a turn for each" (`agy --help`). The 1.1.15 changelog says it runs "one turn per message in a single conversation, so a driver can keep a session open" (`agy changelog`).
- The docs: "a single `conversation_id` tracks the entire session, and `init` is only sent once". "Each prompt executes a full turn and emits its own `result` event", and "the process exits after the input pipe is closed and the current turn completes".
- There is no stdin interrupt. A `control_request` or `control_response` line ends the session with an `ERROR` result and exit code 2 ([headless mode](https://antigravity.google/docs/cli/headless)). Stopping a turn therefore means a signal, which ends the process (D0: SIGINT gave `interrupted` and exit 1). Stop and send then has to continue with `--conversation` in a new process.

## What one live session per attempt would need

All four clients can keep one process across turns. Keeping one would be an addition to the runner, not a replacement for resume: the process dies when iDevelop closes or crashes, and Continue fix, a reopened run, and Open in terminal all still need the client's resume.

**Runner work** (inferred from the code and the protocols above):

- **A turn stops being a process.** `TurnProtocol` ends a process at the terminal event and closes stdin. A live attempt needs a session host that keeps stdin open, starts each continuation by writing to the same process, and tells a turn's end apart from the process's end. These are D0's `TurnEnded` and `UserMessageDelivered` events.
  - Claude Code: the next `user` line, with `--replay-user-messages` as the delivery receipt.
  - Codex: the next `turn/start` on the same `threadId`, whose response carries the turn id.
  - Pi: RPC `prompt`, which means moving Pi from `-p --mode json` to `--mode rpc`.
  - Antigravity CLI: the next stdin line.
- **Mid-turn input** can go into the running turn: Codex `turn/steer`, Pi `steer` or `follow_up`. Claude Code and Antigravity CLI queue it as the next turn.
- **Stop and send** stays in the process for Claude Code (`interrupt`), Codex (`turn/interrupt`), and Pi (`abort`). Antigravity CLI must be stopped and resumed in a new process.
- **Settings changed between turns** need a client channel. Codex takes overrides on `turn/start`. Claude Code has `set_model` and `set_permission_mode`; changing its effort in a live process is unverified. Pi's RPC `set_model` can change the user's default model, which iDevelop never sends ([agent-clients.md](../agent-clients.md#pi-101)). Antigravity CLI has no such channel. So a settings change, the "Settings changed" marker in the conversation view, would have to restart the process with a resume.
- **Read-only and policy changes** also need a restart. Antigravity CLI cannot turn a resumed session read-only at all ([agent-clients.md](../agent-clients.md#sessions)).
- **Pi's folder.** Pass `--session-dir`, or the session file path through `--session`, whether or not turns stay one process per turn. Otherwise a turn that runs in another folder silently starts a new session.

**Records this would change.** These are product decisions for the owner:

- The [node model record](../handoffs/2026-10-04-node-model.md#interaction-resumes-the-clients-own-session) says "A turn is one client process … No process runs between turns, so a waiting node costs nothing and survives quitting iDevelop". [Product direction](../product-direction.md#nodes-have-types-that-users-define) says "a conversation resumes the client's own session, so no process runs while a node waits". [context.md](../context.md#working-decisions) says "a node that waits for the person holds no process and no lock". The glossary defines a Turn as "one client process within an attempt".
- E3's design v5 expects "Managed roots `0` while waiting" in its waiting-reply behavior test (comment on [#54](https://github.com/Mano-Liaoyan/iDevelop/issues/54)).
- The glossary's client slot takes nothing while a task waits. [ADR 0010](../adr/0010-a-reply-is-saved-at-once-and-runs-when-the-slot-is-free.md) starts a reply's turn when the run's one client slot is free. An idle live process either holds the slot, so one waiting chat blocks the run, or iDevelop keeps idle processes outside the slot.
- The `.idp/attempts/<task-id>/run.lock` lock is held per turn. A live process would hold it while the node waits, unless the lock moves to the session host.
- [ADR 0015](../adr/0015-a-start-that-launched-nothing-may-be-asked-again-with-a-new-intent.md) lets a start that "recorded and launched nothing" take a new intent. In a live session a turn launches nothing, so the rule would turn on whether the message was delivered.
- [ADR 0013](../adr/0013-an-interrupted-fix-waits-for-the-person-s-choice.md)'s Continue fix already relies on the client's resume after a close, so it does not change.

**A cheaper first step** (inference): iDevelop could keep the process per turn and change what the person sees and what a message starts. For example, a message to a resting task could continue the same attempt instead of starting a new one, or the conversation could show one continuous thread with no "New attempt" marker when the session is the same. The Pi folder fix applies either way. Only the owner can say which of these the request means.

## Skills by client

The [product direction](../product-direction.md#nodes-have-types-that-users-define) says "a user's own commands and skills work inside a node" and that iDevelop depends on no skill library. Today every launch leaves skills at each client's own default, which loads everything the client finds.

### Claude Code 2.1.295

**Discovery.**

- Skills come from enterprise, personal (`~/.claude/skills/`), project (`.claude/skills/`), nested (`<subdir>/.claude/skills/`, loaded once Claude works there), `--add-dir` folders, and enabled plugins (`<plugin>/skills/`, named `plugin:skill`) ([skills](https://code.claude.com/docs/en/skills)).
- Plugins are switched by `enabledPlugins` in any settings file, such as this repository's `.claude/settings.json`, which enables `mattpocock-skills` ([settings reference](https://code.claude.com/docs/en/settings-reference)).
- Skill folders are watched and reloaded within a session, except in bare mode.

**Per-run controls.**

- **The `skills` allow-list.** The Agent SDK option `skills` takes a list of names or `"all"`: "the model doesn't see unlisted skills and the Skill tool rejects them" ([SDK skills](https://code.claude.com/docs/en/agent-sdk/skills)).
  - The SDK sends it as a `skills` array in the `initialize` control request on stdin. The request type's comment reads: "When provided, only skills whose names match an entry are loaded into the main session system prompt, matching the exact canonical name (e.g. "my-plugin:my-skill") or a ":name" suffix of it" (`SDKControlInitializeRequest` and `sdk.mjs` in [Agent SDK 0.3.295](https://www.npmjs.com/package/@anthropic-ai/claude-agent-sdk/v/0.3.295)).
  - iDevelop already sends `initialize` in `ClaudeProtocol.Start`.
  - The filter is "a context filter, not a sandbox": skill files stay readable, and typing `/name` still runs a user-invocable skill.
- **`--settings <json>`** applies for one session ([CLI reference](https://code.claude.com/docs/en/cli-reference)).
  - `skillOverrides` sets `"off"`, `"name-only"`, or `"user-invocable-only"` per skill, but "Overrides don't apply to plugin skills".
  - `enabledPlugins` switches a whole plugin.
  - `permissions.deny` takes `Skill(name)` and `Skill(name *)`. List keys merge across scopes ([settings](https://code.claude.com/docs/en/settings#lists-merge-instead-of-overriding)).
  - A deny rule blocks a call, but the skill may still be listed to the model.
- **Coarse switches.** `--disable-slash-commands` disables all skills. `--plugin-dir` loads a plugin for one session. `--add-dir` loads a folder's `.claude/skills/`. `--setting-sources` drops whole scopes. `disableBundledSkills` hides the bundled skills.
- **Listing.** `system/init` lists `skills` and `plugins`. The `initialize` response lists `commands`.
- **Explicit use.** A prompt that starts with `/skill-name` runs that skill in `-p` mode ([headless](https://code.claude.com/docs/en/headless#create-a-commit)).
- **Every turn.** A resume does not restore `--settings` or `--plugin-dir` ([sessions](https://code.claude.com/docs/en/sessions#what-a-resumed-session-restores)). Every turn passes the node's choice again, and each `-p` process sends its own `initialize`.

### Codex 0.160.1

**Discovery.**

- Skills come from the repository (`<project>/.codex/skills`, and `.agents/skills` in each folder from the project root down to the working folder), the user (`$HOME/.agents/skills`, the deprecated `$CODEX_HOME/skills`, and a system cache), the admin (`/etc/codex/skills`), and enabled plugins ([`host_roots.rs`, rust-v0.160.1](https://github.com/openai/codex/blob/rust-v0.160.1/codex-rs/ext/skills/src/host_roots.rs), [build skills](https://learn.chatgpt.com/docs/build-skills)).
- The catalog holds names, descriptions, and paths within a context budget. The full `SKILL.md` loads when a skill is used.

**Per-run controls.**

- **`[[skills.config]]`** entries take a `path` or a `name` with `enabled`. Later entries win. There is no wildcard and no allow-list mode. The same file defines `skills.bundled.enabled` and `skills.include_instructions` ([`skills_config.rs`, rust-v0.160.1](https://github.com/openai/codex/blob/rust-v0.160.1/codex-rs/config/src/skills_config.rs)).
  - These keys are read from the user layer and the session-flags layer only, so a project's `.codex/config.toml` cannot set them.
  - `-c` overrides are the session-flags layer ([config loader README](https://github.com/openai/codex/blob/rust-v0.160.1/codex-rs/config/src/loader/README.md)). So `codex app-server -c 'skills.config=[{name="x",enabled=false}]'` applies to one launch.
  - The `config` field of `thread/start` and `thread/resume` merges into the same overrides. It is ignored when resuming a thread that is already running.
- **An allow-list** therefore means listing skills with `skills/list` (each entry has `enabled`, `scope`, `path`, `pluginId`) and disabling every skill that was not chosen. This is inference.
- **Persistent writes don't fit a node.** `skills/config/write` exists, but it presumably persists to the user's config, as its name and the docs suggest; the source was not checked.
- **Plugins.** `--disable plugins` turns plugins off for one run. `plugins."<name>@<marketplace>".enabled` is a config key ([config reference](https://learn.chatgpt.com/docs/config-file/config-reference)).
  - `turn/start` takes `disabledPluginIds`, but the 0.160.1 README says saving that selection "does not yet filter plugin capabilities" ([app-server README](https://github.com/openai/codex/blob/rust-v0.160.1/codex-rs/app-server/README.md)).
  - The experimental `selectedCapabilityRoots` needs `experimentalApi`, which iDevelop leaves off.
- **Explicit use.** A `skill` input item `{type, name, path}` beside `$<skill-name>` in the text injects the skill's instructions (`UserInput` in the generated bindings, [app-server docs](https://learn.chatgpt.com/docs/app-server)).
- **A separate `CODEX_HOME`** would also move the sign-in, and would still not hide `$HOME/.agents/skills` or the repository's skills.

### Pi 1.1.0

**Discovery.** Skills come from `~/.pi/agent/skills/`, `.pi/skills/`, `~/.agents/skills/`, `.agents/skills/` from the working folder up to the repository root, the settings `skills` array, and packages ([`docs/skills.md`, v1.1.0](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/skills.md)).

**Per-run controls.**

- `--no-skills` "disables discovered and configured skills. Explicit `--skill` paths still load", and `--skill <path>` can repeat ([`docs/cli.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/cli.md), `pi --help`). A node's choice maps to `--no-skills --skill <path> …`, which iDevelop resolves from names to paths and passes on every turn.
- The skill descriptions live in a system-prompt section that the session file records as patches ([`docs/session-format.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/session-format.md)). So a resume with another skill set is presumably allowed and recorded. That is inference.
- **Listing.** RPC `get_commands` returns entries with `source: "skill"` without a model call ([`docs/rpc.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/rpc.md)).
- **Explicit use.** `/skill:name` forces a skill, and "manually entered `/skill:name` commands still work" when skill commands are hidden ([`docs/skills.md`](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/skills.md)). Whether that expands in `-p` mode is unverified.

### Antigravity CLI 1.3.2

**Discovery.**

- Skills come from the workspace `.agents/` folder (or `.agent/`, `_agents/`, `_agent/`) from the working folder up to the repository root, the global config, built-in skills, and plugins.
- The built-in customization docs name `~/.gemini/config/` as the global folder. The [online skills page](https://antigravity.google/docs/skills) names `~/.gemini/antigravity-cli/skills/`. The two sources disagree.
- `skills.json` and `plugins.json` in those config folders filter with `include_only` and `exclude`. `agy plugin enable|disable` changes the user's config permanently.

**Per-run controls.**

- There is no per-run skill flag or settings override.
- `--disable-slash-commands` turns off "slash command and skill expansion in print mode", for all skills (`agy --help`).
- `--agent <name>` selects a custom agent, a Markdown file in `.agents/agents/` or the global config. Its frontmatter can list `skills` and set `inheritCustomizations` ([subagents](https://antigravity.google/docs/subagents)). The 1.1.14 changelog says that switch "decides whether the agent adopts your skills, rules, plugins, subagents and MCP servers".
  - A node's choice could map to a generated agent file with `inheritCustomizations: false` and a `skills` list.
  - iDevelop would have to write that file into the project's checkout or the user's global config.
  - It would probably also drop the user's rules and MCP servers.
  - Whether a `skills` list restricts the agent to those skills is undocumented.
- **Listing.** `agy -p "/skills"` lists skills "without starting an agent turn, spending quota, or leaving a conversation behind" (1.1.11 changelog). It was not run for this note.

### What a per-node skill choice maps to

| Client | Choose only these skills | Turn all off | List what exists | Run one explicitly |
| --- | --- | --- | --- | --- |
| Claude Code | `initialize` `skills: [...]`, every turn | `skills: []` or `--disable-slash-commands` | `system/init` `skills` and `plugins` | Prompt starts with `/name` |
| Codex | Disable each unchosen skill: `-c skills.config=[...]` on the app-server, every launch | Disable each, plus `skills.bundled.enabled=false` and `--disable plugins` | `skills/list` | `skill` input item plus `$name` |
| Pi | `--no-skills --skill <path> …`, every turn | `--no-skills` | RPC `get_commands` | `/skill:name`, unverified in `-p` |
| Antigravity CLI | No supported control. An unverified generated `--agent` file is the only route. | `--disable-slash-commands` stops expansion only, unverified for model-invoked skills | `agy -p "/skills"` | `/name` expansion in print mode, unverified |

Honestly, a per-node choice is an allow-list for Claude Code and Pi, a computed deny-list for Codex, and "all of the user's skills" for Antigravity CLI until a probe shows the custom-agent route works. Each client names and finds skills its own way, so a node's choice is per client. Codex, Pi, and Antigravity CLI read the shared `.agents/skills` folder, and Claude Code reads `.claude/skills`.

## Facts only a real-client probe can settle

Each item below needs the owner's approval to run a real client.

**Sessions.**

- Claude Code: that a `-p --input-format stream-json` process with stdin left open takes a second `user` line after `result` as a new turn of the same session, and how `--replay-user-messages`, `set_model`, and effort behave there.
- Claude Code: that the first turn of a `-p --resume` reads the prompt cache, from the `cache_read_input_tokens` of its usage, compared with the second turn of a live process.
- Codex: that a live app-server accepts a second `turn/start` after `turn/completed`, and how long an idle connection stays usable.
- Codex: that `thread/resume` of a thread `codex exec` created works, already listed as unverified in [agent-clients.md](../agent-clients.md#unverified-items), and whether `codex resume <id>` through the daemon conflicts with iDevelop's own process.
- Pi: that `--session-id` from another folder starts an empty session, that `--session-dir` or `--session <path>` fixes it, and that `--mode rpc --session-id <id>` appends to one file across prompts.
- Antigravity CLI 1.3.2: that `--conversation` keeps the id across processes, that a second stdin line during a turn queues, and what a resumed conversation keeps of a turn stopped by SIGINT.
- For every client: that the session store holds one session, not a copy. The 2026-10-04 probe compared reported ids only.

**Skills.**

- Claude Code: that `initialize` `skills` hides unlisted skills on CLI 2.1.295 driven directly, not through the SDK; that it holds on every resumed turn; and what a turn sees when the list changes between turns of one session, given that the skill listing is recorded in the transcript.
- Codex: that `-c skills.config` or the thread `config` removes a skill from the catalog the model sees, on every resume, and what a disabled skill does when a `skill` input item names it.
- Pi: that `--no-skills --skill` on a resumed session changes the skills the model sees.
- Antigravity CLI: that `--agent` with `inheritCustomizations: false` and a `skills` list restricts skills and lasts across `--conversation`, which global skills folder 1.3.2 reads, and that `-p "/skills"` leaves no conversation behind.

## Choices for the owner

These are product decisions, and this note makes none of them:

1. Whether "one live session" means a client process kept alive while a node waits, which changes the records listed in [What one live session per attempt would need](#what-one-live-session-per-attempt-would-need), or one continuous attempt and conversation over the existing per-turn resume.
2. Whether an idle live process holds the run's client slot and the task's run lock, and how long it may stay idle before iDevelop closes it and falls back to resume.
3. Whether a node's skill choice is stored per client, and what an Antigravity CLI node offers while no per-run control is confirmed.

## Sources

Local, on 2026-10-09:

- `claude --help` and `~/.claude/cache/changelog.md` (through 2.1.294), Claude Code 2.1.295.
- `@anthropic-ai/claude-agent-sdk` 0.3.295 from npm (`sdk.d.ts`, `sdk.mjs`, `claudeCodeVersion` 2.1.295).
- `codex --help`, `codex exec resume --help`, `codex features list`, and `codex app-server generate-ts`, codex-cli 0.160.1.
- `pi --help` and the `@earendil-works/pi-coding-agent` 1.1.0 package's `docs/`, `dist/`, and `CHANGELOG.md`.
- `agy --help` and `agy changelog`, Antigravity CLI 1.3.2, and its built-in `agy-customizations` skill docs.
- The keys and ids of session files under `~/.claude/projects/`, `~/.codex/sessions/`, `~/.pi/agent/sessions/`, and `~/.gemini/antigravity-cli/conversations/`. No contents were read.

Online:

- Claude Code: [sessions](https://code.claude.com/docs/en/sessions), [Agent SDK sessions](https://code.claude.com/docs/en/agent-sdk/sessions), [streaming input](https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode), [headless](https://code.claude.com/docs/en/headless), [CLI reference](https://code.claude.com/docs/en/cli-reference), [skills](https://code.claude.com/docs/en/skills), [Agent SDK skills](https://code.claude.com/docs/en/agent-sdk/skills), [settings](https://code.claude.com/docs/en/settings), [settings reference](https://code.claude.com/docs/en/settings-reference), [CHANGELOG](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md).
- Codex: [app-server](https://learn.chatgpt.com/docs/app-server), [build skills](https://learn.chatgpt.com/docs/build-skills), [config reference](https://learn.chatgpt.com/docs/config-file/config-reference), and [openai/codex at rust-v0.160.1](https://github.com/openai/codex/tree/rust-v0.160.1): `codex-rs/app-server/README.md`, `codex-rs/app-server-transport/src/transport/stdio.rs`, `codex-rs/config/src/skills_config.rs`, `codex-rs/config/src/loader/README.md`, `codex-rs/ext/skills/src/host_roots.rs`.
- Pi: [earendil-works/pi at v1.1.0](https://github.com/earendil-works/pi/tree/v1.1.0/packages/coding-agent): `docs/cli.md`, `docs/rpc.md`, `docs/session-format.md`, `docs/skills.md`, `src/main.ts`.
- Antigravity CLI: [headless mode](https://antigravity.google/docs/cli/headless), [CLI reference](https://antigravity.google/docs/cli/reference), [skills](https://antigravity.google/docs/skills), [subagents](https://antigravity.google/docs/subagents).
