# iDevelop

A project for building a graphical interface that coordinates multiple coding agents. The development environment is ready. Product requirements, the interface, and the technology stack remain undecided.

English is the project's working language. The shared policy is in [`AGENTS.md`](AGENTS.md).

This project uses [Lauren Tan's PStack, @poteto](https://github.com/cursor/plugins/tree/main/pstack), version `0.15.6`, pinned to commit `23e4138daa01c42d4969f7a5465f82704e64f798`. Its 49 skills, 24 principles, and 23 playbooks come from the official source. This repository adds local adapters for Codex, Pi, Claude Code, and Gemini CLI. These adapters are maintained by this project and are not an official PStack port.

## Initialize the project

Install Node.js 22 or later, Git, and the agent client you want to use. There are no application dependencies to install yet.

```powershell
git clone https://github.com/Mano-Liaoyan/iDevelop.git
cd iDevelop
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
```

Setup downloads the recorded PStack submodule revision and generates `.agents/skills` inside the project. Claude and Cursor link to that directory. Gemini discovers it natively, and the Pi launcher loads it explicitly. Run setup after cloning or creating a worktree. Initial setup needs network access. Generated files and personal settings are excluded from commits.

The upstream repository lives at `.pstack/upstream`. The main repository records its URL and commit. PStack belongs to the official `cursor/plugins` monorepo, so the submodule points to that repository. Setup uses sparse checkout to expose `pstack` and upstream root files without expanding other plugin directories. The source and license are under `.pstack/upstream/pstack`.

A submodule keeps upstream changes to a version pointer in this repository's commits and reviews. A subtree would keep upstream files as ordinary tracked files. Setup installs no global plugin. Earlier source snapshots remain in Git history.

## Invoke skills in Codex desktop

Search for `poteto-mode` in the composer's `@` menu, or include `$poteto-mode` in a message:

```text
$poteto-mode Check this project's development environment and explain the next step.
```

The upstream `/poteto-mode` examples use Cursor's invocation syntax. Codex's slash menu is not the installation check. Codex added skills to the [`@` menu in March 2026](https://learn.chatgpt.com/docs/changelog). This project's 49 PStack skills have been verified through its discovery API. If you opened a conversation before setup, start a new conversation in this project and search again.

Invoking project skills and hiding other skills are separate capabilities. The table below records the desktop isolation limit. Global skill settings have not been changed to hide them.

## Launch a client

Use the project launcher in Windows PowerShell to apply the client's skill isolation settings:

```powershell
.\scripts\agent.ps1 codex
.\scripts\agent.ps1 claude
.\scripts\agent.ps1 pi
.\scripts\agent.ps1 gemini
```

The launcher does not install clients, copy credentials, or change other projects. Append client arguments after its name. The launcher declares no PowerShell parameters, so short flags such as `-p` and `-c` reach the client unchanged. PowerShell itself still splits `-name:value` into two arguments, and a call from a PowerShell prompt drops a bare `--`. Skill filtering depends on the client version and organization policy. Arguments that add other plugins or skill sources can bypass the intended isolation.

| Client | Project isolation | Verification and limits |
| --- | --- | --- |
| Codex CLI | Generates session-level `-c skills.config=...` overrides from discovered `SKILL.md` paths. Tool connections remain available. | `node scripts/pstack.mjs isolate-codex` generates the filter. `audit-codex` uses the same override with the real `skills/list` API. It verifies exactly 49 enabled PStack skills and confirms that an ordinary parent-directory session retains its other skills. The model's actual prompt was also checked for the same 49 entries. |
| Codex desktop | Reads project PStack skills and `AGENTS.md`. | Version `0.160.0` ignores project-layer `skills.config` filters. No supported desktop setting for project-only skill hiding was found. Ordinary desktop conversations may display other skills. Use the CLI launcher for the verified isolation. |
| Claude Code | Uses `--setting-sources project,local` to exclude personal and synced skill sources. Disables bundled skills and hides doctor. | Shares `.claude/skills`. Organization-managed settings may still apply. After installing the client, inspect `/skills` and `/plugin`. |
| Pi | Uses `--no-extensions --no-skills --skill <project-directory> --no-prompt-templates`. | Explicitly loads only PStack. Disabling extensions also disables subagent and MCP capabilities supplied by those extensions. Review any required extension before explicitly loading it from the project. An ordinary `pi` launch does not guarantee isolation. |
| Gemini CLI | Uses a project-local `GEMINI_CLI_HOME`. Lists skills, disables non-PStack skills, and verifies the enabled list at launch. | Requires login and project trust in this separate profile. Management lists may still show disabled built-in skills. No complete display allowlist is available. The launcher stops if the list format changes and verification cannot complete. |

For Gemini's initial login and trust setup, open a new PowerShell window in this project. Run the following commands, complete the native prompts, and exit. This initial session has not applied skill filtering, so use it only for account setup. Close the window to restore the ordinary environment, then use the project launcher.

```powershell
$env:GEMINI_CLI_HOME = Join-Path (Get-Location) '.pstack/runtime/gemini-home'
gemini
```

The Codex launcher refreshes and verifies its filter before each launch. Running plain `codex` does not include these overrides. The path filter reflects the current installation, not a wildcard prohibition on future plugins. A client's management page may still list disabled entries. Verification checks that only PStack skills are enabled in the session.

Only Codex has been tested on this machine. The Claude, Pi, and Gemini adapters follow official documentation but have not been verified in their actual clients.

## Follow the workflow and share records

Engineering tasks follow PStack by default. State the goal, constraints, and a verifiable completion condition. `poteto-mode` selects the playbook. Explicit invocation is also available: `$poteto-mode` in Codex, `/poteto-mode` in Claude, and `/skill:poteto-mode` in Pi. In Gemini, ask the agent to use the poteto-mode skill.

- [`AGENTS.md`](AGENTS.md) is the shared entry point. `CLAUDE.md` and `GEMINI.md` import it.
- [`docs/context.md`](docs/context.md) stores stable facts and the agreed project direction.
- `docs/handoffs/YYYY-MM-DD-<task>.md` records each task's decisions, evidence, unfinished work, and continuation steps. Each task writes its own file.
- Agents that write code concurrently use separate branches and worktrees. The coordinator checks their evidence before integrating changes.

These versioned records support handoffs. They do not provide live messaging, cross-client scheduling, or complete conversation synchronization.

## Configure models for quality

Edit [`.pstack/models.json`](.pstack/models.json). The initial Codex implementation model is `gpt-6.1-sol`. Judgment and complex tasks use `gpt-6-astra`. Both use `max` reasoning, and independent reviews use both models. These IDs were available during setup. Check the available catalog when switching clients.

The Pi, Claude, and Gemini entries remain `null` because their catalogs have not been verified locally. Discover available models and configure their roles on first use. Codex and Cursor model IDs are not interchangeable with another client's IDs. Explicit user edits take precedence over the default policy.

This configuration tells agents which models to choose for each role. A programmatic dispatcher across providers and natural-language configuration generation remain future product work.

## Upstream practices and adaptations

The setup follows the official guide's task routing, on-demand principle loading, reproduction before repair, verification through real behavior, independent review, separate worktrees for concurrent writers, and recorded handoffs. PStack's Build the Lever and Prove It Works principles informed the repeatable setup and discovery checks.

See [`.pstack/compatibility.md`](.pstack/compatibility.md) for adaptation boundaries. Upstream references to `cursor-team-kit`, Cursor cloud orchestration, Bun, and Bash helpers do not imply global installation. Agents must report missing capabilities and distinguish substitutions from tools they actually ran. There is no application to drive yet. Once an application exists, use PStack to create its verification workflow.

## Update PStack

Only an explicit upgrade follows upstream `main`. Fetch its latest revision, stage the new version pointer, then regenerate and check the skills. Staging does not commit the upgrade. Resolve any failures before committing.

```powershell
git submodule update --remote --checkout -- .pstack/upstream
git add .pstack/upstream
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
git diff --cached --submodule=log
```

If Codex is installed, run `node scripts/pstack.mjs isolate-codex` to verify the updated skill list. Review the upstream changes and client adapters. After verification, commit the version pointer:

```powershell
git commit -m "Update PStack"
git push
```

On another machine, pull the main repository and synchronize its recorded version:

```powershell
git pull
git submodule update --init --checkout -- .pstack/upstream
node scripts/pstack.mjs setup
```

Ordinary setup does not follow the latest upstream version. If an initialized submodule differs from the index, setup asks you to synchronize it or stage an intentional upgrade. Generation also rejects upstream working-tree changes.

The script owns the generated directory. Regeneration removes files and skills deleted upstream. Put project customizations in the adapter script or shared configuration.

## References

- [Lauren Tan's PStack README](https://github.com/cursor/plugins/blob/main/pstack/README.md) and [official guide](https://github.com/cursor/plugins/tree/main/pstack/docs/guide). The author is Lauren Tan, @poteto. The setup requirements come from verified sources rather than unverified video claims.
- [Codex skills](https://learn.chatgpt.com/docs/build-skills), [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference), [project filtering limitation](https://github.com/openai/codex/issues/20210), and [filter configuration sources](https://github.com/openai/codex/blob/main/codex-rs/config/src/skills_config.rs).
- [Claude setting-source scope](https://code.claude.com/docs/en/agent-sdk/claude-code-features#control-filesystem-settings-with-settingsources) and [skill visibility](https://code.claude.com/docs/en/skills#override-skill-visibility-from-settings).
- [Pi resource loading flags](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/cli.md#resources).
- [Gemini skills](https://geminicli.com/docs/cli/skills/), [configuration](https://geminicli.com/docs/reference/configuration/), and [list implementation](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/commands/skills/list.ts).

Upstream PStack uses the [MIT license](https://github.com/cursor/plugins/blob/23e4138daa01c42d4969f7a5465f82704e64f798/pstack/LICENSE). A license for this project's original files has not yet been selected.
