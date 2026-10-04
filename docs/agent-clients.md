# Agent client behavior

Single-task execution relies on the client behavior below. The client rows in `src/IDevelop.Core/Execution/Clients/` encode it, and the tests replay the recorded fixtures in `tests/Shared/Fixtures`.

These observations come from running each client on Windows on 2026-10-04, first from a shell and then through iDevelop. Each run used the sign-in that client already had. Each client wrote a requested file in a fresh Git repository and returned its final text. The recorded event streams, trimmed and without local paths, are the fixtures. Some Antigravity CLI observations come from running it on 2026-10-03 while setting up this project.

## Claude Code 2.1.289

- `claude -p --output-format stream-json --verbose --model <id> --effort <level> --permission-mode acceptEdits` reads the prompt from stdin. Accept-edits mode let it write files in the working folder.
- The `system/init` event names the requested model, and each assistant message names the served model with a date suffix, such as `claude-haiku-4-5-20251001`. The final `result` event carries `is_error` and the final text.
- An unknown model exits with code 1 and a `result` event whose `is_error` is true.
- Claude Code has no command that lists models. `claude auth status` prints JSON with `loggedIn`.
- Claude Code treats a folder under `~/.claude` as sensitive and refused to write there even in accept-edits mode.

## Codex 0.160.0

- `codex exec --json -m <id> -c model_reasoning_effort=<level> --sandbox workspace-write --skip-git-repo-check -` reads the prompt from stdin. The unquoted value works, and the session file under `~/.codex/sessions` recorded the requested effort. The quoted form `model_reasoning_effort="high"` also works, but a quote cannot pass through an npm `codex.cmd` shim.
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
- Print mode (`-p`) cannot ask for permission. On 2026-10-03 it refused to read files in an untrusted folder, and it read them in a repository the user's `agy` settings trust. Commands and file writes stayed blocked. Accept-edits mode let it write a file in a folder that its settings do not trust. No run passed `--dangerously-skip-permissions`, so its effect is unverified.
- The final `result` event carries `status` (`SUCCESS` or `ERROR`), `response`, and `error`. An unknown model ID exits with code 1, with no silent fallback.
- `agy models` prints `<id><TAB><name>` lines. An ID ends with its effort level only when the name ends with the same level in parentheses, such as `gemini-3.8-flash-high` and `Gemini 3.8 Flash (High)`. A model without a level, such as `claude-opus-4-6-thinking`, takes no `--effort`. On 2026-10-03, the bare ID `gemini-3.8-flash` with a separate `--effort high` or `--effort low` reached the matching backend variant.
- On 2026-10-03, with `--output-format stream-json`, the `init` event reported the requested model ID, and `--log-file` recorded the backend label. The `json` output reported no model.

## Unverified items

- Whether `agy models` fails for a signed-out account, which iDevelop treats as the readiness signal.
- Real client runs on macOS and Linux, and the login shell's PATH there. CI runs the fake client on both.
