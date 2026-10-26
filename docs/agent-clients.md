# Agent client behavior

Every turn relies on the client behavior below. The four client sections record the first launches, run on their own, and the [C1 protocols](#conversation-protocols-from-c1) section describes how each turn talks to its client now, and which parts no real client run has confirmed yet. The client rows in `src/IDevelop.Core/Clients/` encode it, and the tests replay the recorded fixtures in `tests/Shared/Fixtures`.

These observations come from running each client on Windows on 2026-10-04, first from a shell and then through iDevelop. Each run used the sign-in that client already had. Each client wrote a requested file in a fresh Git repository and returned its final text. The recorded event streams, trimmed and without local paths, are the fixtures. Some Antigravity CLI observations come from running it on 2026-10-03 while setting up this project.

## Claude Code 2.1.289

- `claude -p --output-format stream-json --verbose --model <id> --effort <level> --permission-mode acceptEdits` reads the prompt from stdin. Accept-edits mode let it write files in the working folder.
- The `system/init` event names the requested model, and each assistant message names the served model with a date suffix, such as `claude-haiku-4-5-20251001`. The final `result` event carries `is_error` and the final text.
- An unknown model exits with code 1 and a `result` event whose `is_error` is true.
- Claude Code has no command that lists models. `claude auth status` prints JSON with `loggedIn`.
- Claude Code treats a folder under `~/.claude` as sensitive and refused to write there even in accept-edits mode.

## Codex 0.160.0

- `codex exec --json -m <id> -c model_reasoning_effort=<level> -c approval_policy=never --sandbox workspace-write --skip-git-repo-check -` reads the prompt from stdin. The unquoted value works, and the session file under `~/.codex/sessions` recorded the requested effort. The quoted form `model_reasoning_effort="high"` also works, but a quote cannot pass through an npm `codex.cmd` shim.
- A user's `~/.codex/config.toml` can set `approval_policy = "on-request"` with an automatic reviewer. On Linux on 2026-10-04 such a configuration let a run with `--sandbox read-only` write a file, because the reviewer approved the escalation. With `-c approval_policy=never` the sandbox held, so iDevelop passes it on every Codex run.
- `codex debug models` prints the model catalog as JSON, with each model's `visibility` and its supported reasoning levels.
- The JSON events never name the served model. A turn ends with `turn.completed`, or with `turn.failed` and exit code 1. A top-level `error` event can precede either.
- `codex login status` exits with code 0 when Codex is signed in.

## Pi 1.0.1

- `pi -p --mode json --model <provider>/<id> --thinking <level>` reads the prompt from stdin.
- A failed turn still exits with code 0. The `stopReason` and `errorMessage` of the last assistant message report the failure, as Pi's own `docs/cli-integration.md` says.
- Pi silently replaces a thinking level that the model does not support. DeepSeek V4 Pro asked for `low` ran at `high`. The event stream reports the level that Pi used.
- In RPC mode (`pi --mode rpc --no-session`), `get_available_models` returns each model with its `thinkingLevelMap`. A level mapped to null is not supported. iDevelop never sends `set_model`, which can change the user's default model.
- `pi auth check --provider <provider> --json` prints `{"status":"ready"}` or `{"status":"invalid",...}`. It prints its answer at once but exits only when its stdin closes.
- Killing Pi's RPC process right after it answered made the next `pi` command take about 30 seconds to exit. Closing its stdin, the shutdown that Pi's `docs/rpc.md` describes, let it exit in 22 milliseconds.
- On Windows, `pi` is an npm `pi.cmd` shim, so it runs through `cmd.exe`. Pi runs commands through Git Bash. `Process.Kill(entireProcessTree: true)` missed the inner `bash.exe` and its `sleep.exe`, and a Windows Job Object around the client stops them.

## Antigravity CLI 1.2.16

- `agy --input-format stream-json --output-format stream-json --model <id> --effort <level> --mode accept-edits --print=` reads one line from stdin, `{"event":"user","message":{"role":"user","content":"<prompt>"}}`. The CLI's help documents the input format but not the line's shape, which came from the CLI's own error messages. The argument form `-p "<prompt>"` also works, but the Windows command line limits its length.
- Print mode (`-p`) cannot ask for permission. On 2026-10-03 it refused to read files in an untrusted folder, and it read them in a repository the user's `agy` settings trust. Commands and file writes stayed blocked. Accept-edits mode let it write a file in a folder that its settings do not trust. On Linux on 2026-10-05, print mode without a mode reported `permission_mode` `request-review` and wrote a requested file in an untrusted folder in 2 of 3 runs, so leaving out accept-edits does not keep a turn read-only. A fresh turn with `--mode plan` wrote nothing in 3 of 3 runs. No run passed `--dangerously-skip-permissions`, so its effect is unverified.
- The final `result` event carries `status` (`SUCCESS` or `ERROR`), `response`, and `error`. An unknown model ID exits with code 1, with no silent fallback.
- `agy models` prints `<id><TAB><name>` lines. An ID ends with its effort level only when the name ends with the same level in parentheses, such as `gemini-3.8-flash-high` and `Gemini 3.8 Flash (High)`. A model without a level, such as `claude-opus-4-6-thinking`, takes no `--effort`. On 2026-10-03, the bare ID `gemini-3.8-flash` with a separate `--effort high` or `--effort low` reached the matching backend variant.
- On 2026-10-03, with `--output-format stream-json`, the `init` event reported the requested model ID, and `--log-file` recorded the backend label. The `json` output reported no model.

## Process control on Windows

The first real runs through iDevelop, on Windows on 2026-10-04, drove each client through the Release window and a scratch harness:

- All four clients ran a task to success with the requested model and reasoning level, and wrote the requested file. Pi reported the level it used.
- A Pi model whose provider was signed out was refused with its sign-in reason, and nothing launched.
- `Process.Kill(entireProcessTree: true)` missed Git Bash's inner `bash.exe` and `sleep.exe` under Pi. A Job Object per client, without `JOB_OBJECT_LIMIT_BREAKAWAY_OK`, stops the whole tree: Stop ended Codex's tree of 10 processes and Pi's Git Bash tree, and killing iDevelop stopped the client through its job. Codex ran its sandboxed PowerShell command inside the job.
- A tool that asks to break away from the job fails with "Access is denied". No client asked in these runs, but Claude Code and Antigravity CLI ran no shell command there.
- A project folder could plant `node` for an npm `.cmd` shim, because `cmd.exe` searches the current folder before PATH. Every child therefore gets `NoDefaultCurrentDirectoryInExePath=1`, which also reaches the agent's own commands, so a bare `build.cmd` from the project folder fails and `.\build.cmd` works.
- A `.cmd` client for a project on a UNC path runs in `C:\Windows`, so iDevelop refuses that start and says why.
- A child's PATH drops an entry that is not fully qualified, which includes a quoted entry such as `"C:\Program Files\Tool"` that `cmd.exe` still searches.
- DeepSeek through Pi once declined to run a bare `sleep 300` and finished at once, so a check that needs a long command words it as a build step.

## Sessions

A person's message resumes the client's own session in a new process. `scripts/probe-clients.mjs` ran each client on Linux on 2026-10-04. Each one resumed its session in a new process with the id it reported itself, including after its first turn was stopped mid tool call. No resumed turn reported a different id. The Claude Code, Pi, and Antigravity CLI rows pass these arguments after the ones above. Codex resumes with `thread/resume` over `codex app-server`, as the table says.

| Client | Session id | Resume | Terminal interface |
| --- | --- | --- | --- |
| Claude Code | `session_id` of the `system/init` event | `--resume <id>` | `claude --resume <id>` |
| Codex | `thread.id` of the `thread/started` notification | `thread/resume` with the thread id, the model, and the sandbox | `codex resume <id>` |
| Pi | `id` of the `session` event | `--session-id <id>` | `pi --session <id>` |
| Antigravity CLI | `conversation_id` of the `init` event | `--conversation <id>` | `agy --conversation <id>` |

- Every session id the probe saw was a UUID. iDevelop takes a session id only when it is a plain id: a letter or digit, then letters, digits, `.`, `_`, `:`, and `-`. It ignores any other, whether a client printed it or an attempt log holds it, because a shared repository can carry attempt logs and the id reaches a client's arguments and the command a person pastes in a terminal.

- `codex exec resume`, which `scripts/probe-clients.mjs` uses, takes no `--sandbox` option, so a resumed turn there sets the sandbox through its configuration key. On Linux on 2026-10-05, a resumed turn with `-c sandbox_mode=read-only` refused to write a file, so resume honors the key. The probe's `resume-readonly` case repeats this check for Claude Code, Codex, and Antigravity CLI. A write fails the check for Claude Code and Codex. For Antigravity CLI, a write reports `pass: null`, because the next item records that limitation, and a resumed turn that writes nothing passes with a note that this page needs updating.
- Read-only access on a resumed session depends on the client. On Linux on 2026-10-05 the probe started each session with write access and resumed it read-only. Claude Code in plan mode and Codex with a read-only sandbox wrote nothing. Antigravity CLI kept writing. A conversation started with accept-edits and resumed with `--mode plan` wrote the file in 4 of 4 runs. Antigravity CLI holds read-only only in a fresh session with `--mode plan`. A session that must stay read-only therefore starts read-only. Claude Code's plan mode also writes its plan to `~/.claude/plans/`, outside the project.
- In the probe, Antigravity CLI's `result` event reported the same `conversation_id` as its `init` event, so the row keeps reading it from `init`.
- Pi finds a session only in the folder where it ran. On Linux on 2026-10-09, Pi 1.1.0 given `--session-id <id>` from another folder printed "No project session found with id" on stderr, reported the same id, and answered from an empty session. `--session-dir` did not help, `--session <file>` resumed the session but worked in the session's old folder, and Pi's code offers only `--fork` to carry the history to another folder, under a new id ([research](research/2026-10-09-client-sessions-and-skills.md#pi)). iDevelop records the folder each turn runs in and refuses a message to a Pi conversation whose project folder moved or was renamed since its last turn, with a note that offers Start a new conversation ([ADR 0026](adr/0026-a-continued-session-reads-as-one-conversation.md)).
- The terminal commands come from each client's `--help`. On Linux on 2026-10-05, run from another folder with the same session flags in print mode, `claude -p --resume <id>` and `agy --conversation <id>` found the session but would work in that folder, and `pi -p --session <id>` printed nothing. From the project folder, Pi resumed the session.
- Open in terminal therefore copies the command after a change into the project folder. On Linux and macOS it copies `cd '<folder>' && <command>`. On Windows, whose default terminal is PowerShell, it copies `Set-Location -LiteralPath '<folder>'; <command>`. The folder is quoted for that shell.

## Conversation protocols from C1

C1a moved Claude Code and Codex to protocols that carry questions, permission requests, and interrupts. One client process still runs each turn. The protocols come from D0's Linux probes on 2026-10-06 with Claude Code 2.1.291, Codex 0.160.0, Pi 1.0.4, and Antigravity CLI 1.3.0. On Linux on 2026-10-09 a probe ran two short read-only turns of each real client with iDevelop's arguments and stdin protocol ([research](research/2026-10-09-client-sessions-and-skills.md#probe-results-2026-10-09)).

- Claude Code runs `claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --model <id> [--effort <level>] --permission-mode plan|bypassPermissions [--resume <id>]`, followed by `--permission-prompt-tool stdio` when the turn may surface questions, or `--permission-prompts none` otherwise. iDevelop writes an `initialize` control request and sends the user message only after its success response. It keeps stdin open for answers, denials, and the `interrupt` control request, and closes it at the `result` event.
- Codex runs `codex app-server -c approval_policy=never -c features.default_mode_request_user_input=false`. iDevelop sends `initialize` with `experimentalApi` off, `initialized`, then `thread/start` or `thread/resume` with the model, approval policy `never`, reviewer `user`, the access sandbox, then `turn/start` with the prompt and effort. The thread id is the session. Questions arrive as message text. A command or file-change approval request is declined, and any other server request gets a method-not-supported error. iDevelop closes stdin at `turn/completed`.
- Pi and Antigravity CLI keep the launches above and still read the whole prompt from stdin, which then closes.
- After Claude Code or Codex reports success and its stdin closes, it has 60 seconds to exit before iDevelop stops it and fails the turn. Pi and Antigravity CLI have no deadline after success. After a stop or a deferral, any client has 5 seconds.
- A Claude Code turn ends when the process exits, even when its `result` arrives while work it started in the background, such as a subagent, still runs. iDevelop follows `system` events `task_started`, `task_updated`, `task_notification`, and `background_tasks_changed`. A backgrounded task counts until the `result` that follows its end, because a `-p` run answers it in a turn of its own after stdin has closed. The 60 seconds stand still while any task counts, and the card says "Waiting for a background subagent". A message with a `parent_tool_use_id` is the subagent's own, and it shows under the subagent's type ([ADR 0030](adr/0030-a-claude-code-turn-ends-when-its-client-exits-after-its-background-work.md)).
- To stop a turn, iDevelop sends Claude Code or Codex its interrupt and reads until the final result before it stops the process tree. Pi and Antigravity CLI have no interrupt, so iDevelop stops their process tree at once.

## Skills

A node's skill choice ([ADR 0025](adr/0025-a-node-s-skill-choice-is-exactly-its-skills-and-each-client-gets-it-its-own-way.md)) needs each client to list the skills it loads in the project folder without a model turn, and to take the choice on every turn, because a resume keeps none of it. The [research note](research/2026-10-09-client-sessions-and-skills.md#skills-by-client) gives the sources. The owner approved a real-client probe of them on 2026-10-11, which ran Claude Code 2.1.296, Codex 0.160.1, and Pi 1.1.0 on Linux.

| Client | Listing, run in the project folder | Each turn of a node with a choice |
| --- | --- | --- |
| Claude Code | `claude -p --input-format stream-json --output-format stream-json --verbose`, an `initialize` control request, then `reload_skills`, whose answer lists the skills alone, the bundled ones among them. Without `reload_skills`, the `initialize` commands that are not Claude Code's own. | The chosen names as the `skills` array of the turn's `initialize` request |
| Codex | `codex app-server`, `initialize`, `initialized`, then `skills/list` with the project as its folder, again every 300 ms until at least 1.5 s have passed and two answers agree, or until 8 s, because the remote plugins' skills join the list only once Codex has loaded those plugins in the background. A skill whose `enabled` is false is left out. | `skills.config` in the `config` of `thread/start` or `thread/resume`, with `enabled = false` for each listed skill not chosen. A `-c` value would not pass an npm `codex.cmd` shim. |
| Pi | `pi --mode rpc --no-session` and `get_commands`. Each entry whose `source` is `skill` gives the name after `skill:` and its file in `sourceInfo.path`. A project's own skills appear only once the folder is trusted in Pi, which `trust.json` in Pi's agent folder records. | `--no-skills` and `--skill <file>` for each chosen skill. A `--skill` file loads in an untrusted folder too. |
| Antigravity CLI | None. It has no per-run skill control. | Nothing |

Claude Code's `skills` allow-list applies to the main session only: a subagent uses its own `AgentDefinition.skills` and sees every skill. It matches a skill's full name or a `:name` suffix, so a chosen `code-review` also lets in a plugin's `code-review:code-review`. A probe on 2026-10-11 confirmed both. The picker says both: under its list for subagents, and on a chosen skill for the plugin skills it lets in.

On Windows, a `--skill` path that cmd.exe could misread, such as one with a space, goes to the node script that Pi's npm shim runs instead of through `pi.cmd`.

When the listing fails, a turn of a node with a choice still starts, as the owner decided on 2026-10-11. Claude Code gets the chosen names unchecked. Codex and Pi get nothing and run with all of their skills, because Codex's list of skills to turn off and Pi's skill files both come from the listing.

## Unverified items

- Every launch in the C1 protocols section on Windows and macOS, and on Linux beyond the 2026-10-09 probe: Claude Code questions under `bypassPermissions` and `plan`, a denied `ExitPlanMode`, `--permission-prompts none`, an interrupt with trailing output, and resuming after a deferred question; Codex app-server with the question feature off, `thread/resume` of a session that `codex exec` created, and read-only turns; npm shims on Windows holding stdin open.

- The [Skills](#skills) listings and launches on Windows and macOS, in a Pi project the person trusted, and with Claude Code plugins that a project's settings enable. The 2026-10-11 probe ran on Linux only.
- Whether `agy models` fails for a signed-out account, which iDevelop treats as the readiness signal.
- Whether each client's terminal command opens its interactive interface on the session that iDevelop's turns used, and whether a later resumed turn sees the turns a person took there. No probe opened an interactive interface.
- Real client runs on macOS, and the login shell's PATH on macOS and Linux. On Linux on 2026-10-05, `scripts/probe-clients.mjs` and a scratch harness ran all four real clients, but the harness inherited a full PATH. The release workflow runs the fake client on macOS.
- On macOS and Linux, `Process.Kill(entireProcessTree: true)` misses a descendant whose parent already exited, because .NET cannot list the children of an exited process there. A turn's process group covers it ([ADR 0001](adr/0001-unix-workflow-turns-lead-their-own-session.md)).
- Whether each provider's plan permits unattended turns started by iDevelop is each provider's policy, as the [provider access reference](provider-access.md) records.
