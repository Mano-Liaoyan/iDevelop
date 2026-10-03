# Launcher argument forwarding

## Request

`scripts/agent.ps1` should forward every argument after the client name unchanged. Its `[Parameter(Mandatory, Position = 0)]` and `[Parameter(ValueFromRemainingArguments)]` attributes made it an advanced script with common parameters. PowerShell then bound short client flags itself. `-p` failed as ambiguous between `-ProgressAction` and `-PipelineVariable`, and `-c` matched `-Client` and `-ClientArguments` by prefix. Long flags such as `--print` passed through.

## Decisions

- Base the work on `main`. The requested base, `antigravity-cli`, was not on GitHub when this work started. Only `main` existed, and no pull request referenced the branch. Its handoff `2026-10-03-antigravity-cli.md` was also absent. The launcher on `main` supports `codex`, `claude`, `pi`, and `gemini`, so the client list and verification use those names. The `agy` client, its `$env:USERPROFILE` personal-source check, and the `agy` runtime checks were not available to this session.
- Declare no parameters at all. Removing only the attributes still leaves named parameters, and PowerShell prefix matching would bind `-c` to `-Client`. The client comes from `$args[0]` and is validated against one list before the install check, with separate errors for a missing and an unknown client.
- Keep `$ClientArguments` typed as `[string[]]`, as the old parameter was. Without the cast, an in-process call such as `& ./scripts/agent.ps1 claude -p:val` silently dropped the `-p:` token when splatted to the client (measured on PowerShell 7.6.2). With the cast the client receives `-p:` and `val`.
- Add a Windows CI step that drives the launcher with a fake `claude.cmd` under `pwsh` and Windows PowerShell 5.1, through `-File` and an in-process `-Command` call. It asserts the forwarded argv and that unknown and missing clients exit nonzero. This is the only Windows PowerShell 5.1 surface available to a Linux session, and it fails on the old launcher.
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
- New launcher, in-process `& ./scripts/agent.ps1 claude -p 'Reply OK' -c --model m`: same argv as `-File`. Exit 0.
- New launcher, `agent.ps1 nope -p`: `Unknown client 'nope'. Usage: scripts/agent.ps1 <codex|claude|pi|gemini> [client arguments]`. Exit 1.
- New launcher with no arguments: `Missing client. Usage: ...`. Exit 1.
- The CI step body, run locally under `pwsh` with a Linux shim, failed on the old launcher and passed on the new one.
- The CI parse step snippet accepted the new launcher. `node scripts/pstack.mjs check` passed for 49 skills. `node --check scripts/codex-skills.mjs` and `git diff --check` passed.

## Open issues

- Windows PowerShell 5.1 and the Windows `.cmd` path were not run locally. The CI step covers them on `windows-latest`. Check its result on the pushed branch.
- The `agy` checks (`agy --version`, the `gemini-3.8-flash` prompt, and the personal-source check) need the `antigravity-cli` branch and a signed-in `agy` on Windows.
- PowerShell splits `-name:value` before any script sees it. `-File` delivers `-p` and `val`, and an in-process call delivers `-p:` and `val`. An in-process call also drops a bare `--`. `-File` keeps `--`. No script can change this host behavior.

## Next action

Port the change onto `antigravity-cli`, or merge `main` into it. The conflict is the client list. Keep this parsing block with `$clients = 'codex', 'claude', 'pi', 'agy'`. Then run on Windows:

```powershell
pwsh -NoProfile -File scripts/agent.ps1 agy --version
pwsh -NoProfile -File scripts/agent.ps1 agy -p "Reply with exactly OK" --model gemini-3.8-flash --effort low
powershell.exe -NoProfile -File scripts/agent.ps1 agy -p "Reply with exactly OK" --model gemini-3.8-flash --effort low
pwsh -NoProfile -File scripts/agent.ps1 nope
pwsh -NoProfile -File scripts/agent.ps1
node scripts/pstack.mjs check
```
