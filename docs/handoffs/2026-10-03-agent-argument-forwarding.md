# Launcher argument forwarding

## Request

`scripts/agent.ps1` should forward every argument after the client name unchanged. Its `[Parameter(Mandatory, Position = 0)]` and `[Parameter(ValueFromRemainingArguments)]` attributes made it an advanced script with common parameters. PowerShell then bound short client flags itself. `-p` failed as ambiguous between `-ProgressAction` and `-PipelineVariable`, and `-c` matched `-Client` and `-ClientArguments` by prefix. Long flags such as `--print` passed through.

## Decisions

- Base the work on `main`. The requested base, `antigravity-cli`, was not on GitHub when this work started. Only `main` existed, and no pull request referenced the branch. Its handoff `2026-10-03-antigravity-cli.md` was also absent. The launcher on `main` supports `codex`, `claude`, `pi`, and `gemini`, so the client list and verification use those names. The `agy` client, its `$env:USERPROFILE` personal-source check, and the `agy` runtime checks were not available to this session.
- Declare no parameters at all. Removing only the attributes still leaves named parameters, and PowerShell prefix matching would bind `-c` to `-Client`. The client comes from `$args[0]`. It is validated against one list before the install check, with separate errors for a missing and an unknown client. The undocumented named form `-Client claude` is now rejected as an unknown client.
- Join each forwarded argument with commas and type the result as `[string[]]`. Called from a PowerShell prompt, `--allowedTools Read,Edit` arrives as an array. The old and intermediate launchers sent `Read Edit`, while a direct `& claude` call sends `Read,Edit`. The join matches the direct call. The resulting plain strings also keep the `-p:` token of an in-process `-p:val`, which splatting the raw `$args` elements silently dropped. Both were measured on PowerShell 7.6.2.
- Add a Windows CI step that drives the launcher with a fake `claude.cmd` under `pwsh` and Windows PowerShell 5.1. It calls the launcher through `-File` and through an in-process `-Command` call. It asserts the forwarded argv case-sensitively, including a comma list. It asserts that unknown and missing clients exit nonzero with their own messages. This is the only Windows PowerShell 5.1 surface available to a Linux session.
- Everything after argument parsing is unchanged. That covers the `pstack.mjs check` gate, Codex isolation, Claude setting sources, Pi flags, the Gemini block, and exit code propagation.

## Changed artifacts

- `scripts/agent.ps1` parses the client and arguments from `$args`.
- `.github/workflows/pstack.yml` adds the "Forward Windows launcher arguments" step.
- `README.md` documents that short flags are forwarded and the PowerShell host limits that remain.

## Commands and results

Run on Linux with PowerShell 7.6.2 installed from packages.microsoft.com. Fake `claude` and `pi` executables printed their argv as JSON.

- Old launcher, `pwsh -NoProfile -File scripts/agent.ps1 claude -p "Reply OK"`: `Parameter cannot be processed because the parameter name 'p' is ambiguous. Possible matches include: -ProgressAction -PipelineVariable.` Exit 1.
- Old launcher, `pwsh -NoProfile -File scripts/agent.ps1 pi -c`: `Parameter cannot be processed because the parameter name 'c' is ambiguous. Possible matches include: -Client -ClientArguments.` Exit 1.
- New launcher, `pwsh -NoProfile -File scripts/agent.ps1 claude -p "Reply OK" -c --model m`: client received `["--setting-sources","project,local","-p","Reply OK","-c","--model","m"]`. Exit 0.
- New launcher, `pwsh -NoProfile -File scripts/agent.ps1 pi -c -p x`: client received `["--no-extensions","--no-skills","--skill","<project>/.agents/skills","--no-prompt-templates","-c","-p","x"]`. Exit 0.
- New launcher, in-process `& ./scripts/agent.ps1 claude --allowedTools Read,Edit -p:val -c 'a b' '' -p 'Reply OK'`: client received `["--setting-sources","project,local","--allowedTools","Read,Edit","-p:","val","-c","a b","","-p","Reply OK"]`. The same call through `-File` differs only in `-p` for `-p:`.
- New launcher, `agent.ps1 nope -p`: `Unknown client 'nope'. Usage: scripts/agent.ps1 <codex|claude|pi|gemini> [client arguments]`. Exit 1.
- New launcher with no arguments: `Missing client. Usage: ...`. Exit 1.
- The CI step body ran locally under `pwsh` with a Linux shim and a copy of GitHub's pwsh wrapper. It exited 0 on the new launcher. It exited 1 on the original launcher, on a launcher without the comma join, and on a launcher that rejects clients without its messages.
- Windows CI run [37137560447](https://github.com/Mano-Liaoyan/iDevelop/actions/runs/37137560447) passed every forwarding assertion under `pwsh` and Windows PowerShell 5.1, through both `-File` and `-Command`. The step still failed, because GitHub's pwsh wrapper ends with `exit $LASTEXITCODE` and the last expected rejection left it at 1. The step now ends with `exit 0`.
- Windows CI run [37137667179](https://github.com/Mano-Liaoyan/iDevelop/actions/runs/37137667179) passed on that fix. Its log shows the unknown and missing client errors in both the PowerShell 7 and the Windows PowerShell 5.1 error formats.
- The CI parse step snippet accepted the new launcher. `node scripts/pstack.mjs check` passed for 49 skills. `node --check scripts/codex-skills.mjs` and `git diff --check` passed.

An independent review found nothing blocking. It reported the comma list join, the case-insensitive CI comparison, the exit-code-only rejection checks, and the missing record of the passing run. All four were corrected and remeasured.

## Open issues

- Windows PowerShell 5.1 and the Windows `.cmd` path ran only in CI on `windows-latest`, not on the user's machine.
- The `agy` checks (`agy --version`, the `gemini-3.8-flash` prompt, and the personal-source check) need the `antigravity-cli` branch and a signed-in `agy` on Windows.
- PowerShell splits `-name:value` before any script sees it. `-File` delivers `-p` and `val`, and an in-process call delivers `-p:` and `val`. An in-process call also drops a bare `--`, which `-File` keeps on PowerShell 7.6.2. Windows PowerShell 5.1 handling of `--` and `-name:value` under `-File` was not measured. No script can change this host behavior.
- An array held in a variable, such as `$tools = 'Read','Edit'` passed as `$tools`, is now joined as `Read,Edit`. A direct native call would pass two arguments. The script cannot tell this apart from a typed `Read,Edit`, and the typed form is the common one.
- The Gemini block strips ANSI codes with the `` `e `` escape. The review inferred from documentation that Windows PowerShell 5.1 has no `` `e `` escape, so the stripping does nothing there. This predates the change and was not touched. `'\x1b\[[0-9;]*m'` works in both versions.

## Next action

Bring these commits into `antigravity-cli`, by merge or cherry-pick. The expected conflict is the launcher's parameter block, where that branch adds `agy`. Keep this parsing block with `$clients = 'codex', 'claude', 'pi', 'agy'`. Then run on Windows:

```powershell
pwsh -NoProfile -File scripts/agent.ps1 agy --version
pwsh -NoProfile -File scripts/agent.ps1 agy -p "Reply with exactly OK" --model gemini-3.8-flash --effort low
powershell.exe -NoProfile -File scripts/agent.ps1 agy -p "Reply with exactly OK" --model gemini-3.8-flash --effort low
pwsh -NoProfile -File scripts/agent.ps1 nope
pwsh -NoProfile -File scripts/agent.ps1
node scripts/pstack.mjs check
```
