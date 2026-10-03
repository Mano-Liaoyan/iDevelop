# Replace Gemini CLI with Antigravity CLI

## Task

The user asked to uninstall Gemini CLI and use Antigravity CLI instead. This covers the machine install and the project's Google client adapter. The requested model stays Gemini 3.8 Flash.

After the user signed in to Antigravity CLI, they asked to finish the open items, remove the leftover files, and commit. Commit messages must not carry a Claude co-author trailer.

## Feature playbook checklist

- [x] `how` over the affected subsystem. Read `scripts/agent.ps1`, `scripts/model-policy.mjs`, the policy tests, README, context, and the documentation bundled with `agy`.
- [ ] `architect` for parallel design exploration. Skip: the launcher change is one branch. The CLI's lack of an isolated profile left three options, compared under Decisions.
- [x] Throughput checkpoint. Blocking first steps: install `agy` and read its real `--help`. Independent workstreams: n/a, one coupled migration. Shared mutable state: this checkout, which a paused Codex session also wrote to. Smallest safe decomposition: one owner.
- [ ] Delegate code-writing. Skip: the policy assigns implementation to Astra through Codex, which this Claude Code session cannot dispatch. A Claude delegate would be a same-provider substitute. The coordinator owned the diff, and Gemini 3.8 Flash reviewed it.
- [x] Verify on the matching surface. The launcher ran `agy` 1.2.16 under PowerShell 7 and Windows PowerShell 5.1, and signed-in requests confirmed the served model.
- [x] Rebase into small, ordered commits. Earlier uncommitted tasks and this change are separate commits.
- [ ] `interrogate`. Skip: the design is not contested.
- [ ] Opening a PR. Skip: the user asked for commits only.

## Decisions

- Gemini CLI stopped serving Google AI Pro, Ultra, and no-cost individual accounts on June 18, 2026. Antigravity CLI replaces it for those accounts. [Transition notice](https://github.com/google-gemini/gemini-cli/discussions/27274)
- The client identifier is `agy` in both `scripts/agent.ps1` and `.pstack/models.json`. Every existing client identifier is the executable name, so the launcher needs no name mapping.
- The model registry key stays `gemini` because it names the model, not the client.
- `agy` 1.2.16 has no documented profile override. Its bundled customization guide says built-in skills are mounted by the agent configuration, and that workspace `skills.json` exclusions never filter skills inherited from the user's environment. Three launcher options were considered:

| Option | Result |
| --- | --- |
| Point `USERPROFILE` at a project directory | Rejected. Undocumented, and every tool the agent spawns, including Git, would inherit the fake home. |
| Warn about personal skills and launch anyway | Rejected. It loses the fail-closed behavior the other adapters have. |
| Stop when a personal skill or plugin source under `%USERPROFILE%\.gemini` has content | Chosen. It matches how `agy` resolves home, and it passes today because those sources are empty. |

- The checked sources are `config/skills`, `config/plugins`, `config/skills.json`, `config/plugins.json`, `antigravity-cli/skills`, `antigravity-cli/plugins`, and `skills`. The first six come from the official skills page and the bundled guide. `~/.gemini/skills` comes from a third-party experiment, and checking it costs nothing.
- The eight bundled skills stay available. They are `agy-customizations`, `antigravity_guide`, `automation`, `generative_ui`, `migrate-workflows`, `permissioned-github`, `plugin`, and `ui-plugin-navigation`. The README records this limit.
- `GEMINI.md` was deleted. `agy` reads `AGENTS.md` directly, and its include syntax is `@[label](path)`, so the `@AGENTS.md` line would have been plain text. The `.gemini/settings.json` ignore rule was removed because only Gemini CLI wrote that file.
- `.pstack/runtime/` stays ignored. Nothing uses it now, but the paused Codex session put a 189 MB binary there, and the rule keeps a repeat out of commits.
- `docs/context.md` now lists Antigravity CLI, not Gemini CLI, among the coordinated agents. Gemini CLI no longer serves the individual subscriptions the product targets.
- The route uses the bare ID `gemini-3.8-flash` with a separate `--effort high`. `agy models` lists only combined IDs such as `gemini-3.8-flash-high`. The bare ID with `--effort` reached the matching backend variant, which keeps the policy's separate model and effort fields.
- The review ran inside the repository through the launcher. `agy` print mode cannot prompt, so it denied file reads in an untrusted scratch folder. The user's `agy` settings trust this repository, which allowed reads. Commands and writes stayed denied, and the run avoided `--dangerously-skip-permissions`.

## Changed artifacts

- `scripts/agent.ps1` replaces the `gemini` branch and its `GEMINI_CLI_HOME` handling with an `agy` branch.
- `scripts/model-policy.mjs` maps client `agy` to provider `google`. `scripts/model-policy.test.mjs` uses `agy` and rejects the retired `gemini` client.
- `.pstack/models.json` routes Gemini 3.8 Flash through `agy` and activates `gemini-3.8-flash` with `client-catalog` verification.
- `.pstack/compatibility.md`, `README.md`, `docs/context.md`, and `docs/provider-access.md` describe Antigravity CLI and the verified route.
- `GEMINI.md` was deleted, and one rule was removed from `.gitignore`.

## Machine changes

- `npm uninstall -g @google/gemini-cli` removed seven packages. `Get-Command gemini` no longer finds it.
- The official installer was read in full before it ran. It verifies the SHA-512 from Google's manifest, installs `%LOCALAPPDATA%\agy\bin\agy.exe`, and adds that directory to the user `PATH`. `agy --version` reports `1.2.16`.
- `%USERPROFILE%\.gemini` was left in place. Antigravity CLI keeps its own state under `.gemini\antigravity-cli` and `.gemini\config`. Gemini CLI's old login files, `oauth_creds.json` and `google_accounts.json`, are still there.
- Removed leftovers: the installer's locked staging copy under `%LOCALAPPDATA%\antigravity`, and the Codex session's `.pstack/runtime/antigravity/bin/agy.exe` and `.pstack/local/install-antigravity.ps1`.

## Commands and observed results

| Command | Result |
| --- | --- |
| `node --test scripts/model-policy.test.mjs` | 60 passed, 0 failed, including `rejects retired Gemini CLI client`. |
| `node scripts/model-policy.mjs resolve frontend-review` | `ready`, with Gemini 3.8 Flash at high through `agy` and Astra at xhigh. |
| `node scripts/pstack.mjs check` | PASS for 49 skills, clean upstream `23e4138`, adapters, links, and model configuration. |
| `scripts/agent.ps1 agy --version` on the real profile | Check passed, printed `1.2.16`, exit 0. |
| Same, with `USERPROFILE` at a temporary profile holding `.gemini\config\skills\personal-skill\SKILL.md` | Stopped with the personal-source error, exit 1. |
| Same, after emptying that skill directory | Printed `1.2.16`, exit 0. |
| `powershell.exe -File scripts/agent.ps1 agy --version` | Printed `1.2.16`, exit 0, under Windows PowerShell 5.1. |
| `agy models` after sign-in | Lists `gemini-3.8-flash-high`, `-medium`, and `-low`, plus other models. No bare `gemini-3.8-flash`. |
| `agy -p ... --model gemini-3.8-flash --effort high --log-file ...` | `IDEVELOP_MODEL_OK`. The log propagated backend label `Gemini 3.8 Flash (High)`, with 72 thinking tokens. |
| Same with `--effort low` | Backend label `Gemini 3.8 Flash (Low)`, with 0 thinking tokens. |
| Same with `--model gemini-9.9-nonexistent` | `invalid model selection`, exit 1. No silent fallback. |
| `--output-format stream-json` | The `init` event reports the requested model ID. The `json` result reports no model. |
| Review through `scripts/agent.ps1 agy --print ... --effort high` | Gemini 3.8 Flash (High) approved with no findings after 40,204 thinking tokens. SHA-1 hashes of all 340 checkout files were unchanged afterward. |

## Open issues

- **Launcher short flags.** `scripts/agent.ps1 agy -p ...` fails because PowerShell treats `-p` as an ambiguous script parameter. `[Parameter()]` makes the script advanced, so it gains common parameters such as `-ProgressAction`. Long flags such as `--print` pass through. This predates the change and affects every client. It is flagged as a separate task.
- **Bundled skills.** The eight bundled `agy` skills cannot be turned off in this version.
- **Weak review signal.** The only cross-provider review approved with no findings, and it did not catch the launcher flag problem. Astra did not review this change because Codex was paused.

## Next action

Fix the launcher's short-flag forwarding. Then use the active frontend-review route on the first Avalonia UI change.
