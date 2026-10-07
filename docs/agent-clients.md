# Agent client behavior

Single-task execution relies on the client behavior below. The [C1 protocols](#conversation-protocols-from-c1) section describes how each turn talks to its client since the conversation backend, and which parts no real client run has confirmed yet. The client rows in `src/IDevelop.Core/Execution/Clients/` encode it, and the tests replay the recorded fixtures in `tests/Shared/Fixtures`.

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

## Sessions

A person's message resumes the client's own session in a new process. `scripts/probe-clients.mjs` ran each client on Linux on 2026-10-04. Each one resumed its session in a new process with the id it reported itself, including after its first turn was stopped mid tool call. No resumed turn reported a different id. The Claude Code, Pi, and Antigravity CLI rows pass these arguments after the ones above. Codex resumes through its own subcommand, as the table says.

| Client | Session id | Resume | Terminal interface |
| --- | --- | --- | --- |
| Claude Code | `session_id` of the `system/init` event | `--resume <id>` | `claude --resume <id>` |
| Codex | `thread_id` of the `thread.started` event | `codex exec resume` with the same options, `-c sandbox_mode=workspace-write` in place of `--sandbox`, and `<id>` before the final `-` | `codex resume <id>` |
| Pi | `id` of the `session` event | `--session-id <id>` | `pi --session <id>` |
| Antigravity CLI | `conversation_id` of the `init` event | `--conversation <id>` | `agy --conversation <id>` |

- Every session id the probe saw was a UUID. iDevelop takes a session id only when it is a plain id: a letter or digit, then letters, digits, `.`, `_`, `:`, and `-`. It ignores any other, whether a client printed it or an attempt log holds it, because a shared repository can carry attempt logs and the id reaches a client's arguments and the command a person pastes in a terminal.

- `codex exec resume` takes no `--sandbox` option, so a resumed turn sets the sandbox through its configuration key. On Linux on 2026-10-05, a resumed turn with `-c sandbox_mode=read-only` refused to write a file, so resume honors the key. The probe's `resume-readonly` case repeats this check for Claude Code, Codex, and Antigravity CLI. A write fails the check for Claude Code and Codex. For Antigravity CLI, a write reports `pass: null`, because the next item records that limitation, and a resumed turn that writes nothing passes with a note that this page needs updating.
- Read-only access on a resumed session depends on the client. On Linux on 2026-10-05 the probe started each session with write access and resumed it read-only. Claude Code in plan mode and Codex with a read-only sandbox wrote nothing. Antigravity CLI kept writing. A conversation started with accept-edits and resumed with `--mode plan` wrote the file in 4 of 4 runs. Antigravity CLI holds read-only only in a fresh session with `--mode plan`. A session that must stay read-only therefore starts read-only. Claude Code's plan mode also writes its plan to `~/.claude/plans/`, outside the project.
- In the probe, Antigravity CLI's `result` event reported the same `conversation_id` as its `init` event, so the row keeps reading it from `init`.
- The terminal commands come from each client's `--help`. On Linux on 2026-10-05, run from another folder with the same session flags in print mode, `claude -p --resume <id>` and `agy --conversation <id>` found the session but would work in that folder, and `pi -p --session <id>` printed nothing. From the project folder, Pi resumed the session.
- Open in terminal therefore copies the command after a change into the project folder. On Linux and macOS it copies `cd '<folder>' && <command>`. On Windows, whose default terminal is PowerShell, it copies `Set-Location -LiteralPath '<folder>'; <command>`. The folder is quoted for that shell.

## Conversation protocols from C1

C1a moved Claude Code and Codex to protocols that carry questions, permission requests, and interrupts. One client process still runs each turn. The protocols come from D0's Linux probes on 2026-10-06 with Claude Code 2.1.291, Codex 0.160.0, Pi 1.0.4, and Antigravity CLI 1.3.0, and no real client has run these exact launches yet.

- Claude Code runs `claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --model <id> [--effort <level>] --permission-mode plan|acceptEdits [--resume <id>]`, followed by `--permission-prompt-tool stdio` when the turn may surface questions, or `--permission-prompts none` otherwise. iDevelop writes an `initialize` control request and sends the user message only after its success response. It keeps stdin open for answers, denials, and the `interrupt` control request, and closes it at the `result` event.
- Codex runs `codex app-server -c approval_policy=never -c features.default_mode_request_user_input=false`. iDevelop sends `initialize` with `experimentalApi` off, `initialized`, then `thread/start` or `thread/resume` with the model, approval policy `never`, reviewer `user`, and the access sandbox, then `turn/start` with the prompt and effort. The thread id is the session. Questions arrive as message text. A command or file-change approval request is declined, and any other server request gets a method-not-supported error. iDevelop closes stdin at `turn/completed`.
- Pi and Antigravity CLI keep the launches above and still read the whole prompt from stdin, which then closes.
- After a client reports success and its stdin closes, it has 5 seconds to exit before iDevelop stops it and fails the turn.

## Unverified items

- Every launch in the C1 protocols section, on each platform: Claude Code questions under `acceptEdits` and `plan`, a denied `ExitPlanMode`, `--permission-prompts none`, an interrupt with trailing output, and resuming after a deferred question; Codex app-server with the question feature off, `thread/resume` of a session that `codex exec` created, and read-only turns; npm shims on Windows holding stdin open.

- Whether `agy models` fails for a signed-out account, which iDevelop treats as the readiness signal.
- Whether each client's terminal command opens its interactive interface on the session that iDevelop's turns used, and whether a later resumed turn sees the turns a person took there. No probe opened an interactive interface.
- Real client runs on macOS, and the login shell's PATH on macOS and Linux. On Linux on 2026-10-05, `scripts/probe-clients.mjs` and a scratch harness that drives `ProjectRuns` ran all four real clients, but the harness inherited a full PATH. CI runs the fake client on both.
