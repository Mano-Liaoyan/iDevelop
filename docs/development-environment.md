# Development environment

This project uses [Lauren Tan's PStack, @poteto](https://github.com/cursor/plugins/tree/main/pstack). The Git submodule at `.pstack/upstream` pins the PStack version from the official `cursor/plugins` repository. Its `pstack` folder holds PStack's source and MIT license. `node scripts/pstack.mjs check` prints how many skills setup installs. This repository adds local adapters for Codex, Pi, Claude Code, and Antigravity CLI. These adapters are maintained by this project and are not an official PStack port. Project-owned skills live in [`skills/`](../skills/), and setup installs them beside the PStack skills. [`verify-idevelop`](../skills/verify-idevelop/SKILL.md) drives the built app's real window on Windows.

## Initialize the project

Install Node.js 22 or later, Git, and the agent client you want to use. Clone the repository as [Build and run the application](../README.md#build-and-run-the-application) shows, then run these commands from its root:

```powershell
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
```

Setup downloads the recorded PStack submodule revision and generates `.agents/skills` inside the project from PStack and from each folder in `skills/`. Both get the same adapter notice and drift check, and a project skill named like a PStack skill fails setup. Claude and Cursor link to that directory. Antigravity CLI discovers it natively, and the Pi launcher loads it explicitly. Run setup after cloning or creating a worktree. Initial setup needs network access. Generated files and personal settings are excluded from commits. Setup installs no global plugin.

## Invoke skills in Codex desktop

Search for `poteto-mode` in the composer's `@` menu, or include `$poteto-mode` in a message:

```text
$poteto-mode Check this project's development environment and explain the next step.
```

The upstream `/poteto-mode` examples use Cursor's invocation syntax. Codex's slash menu is not the installation check. If you opened a conversation before setup, start a new conversation in this project and search again.

## Launch a client

Use the project launcher in Windows PowerShell to apply the client's skill isolation settings:

```powershell
.\scripts\agent.ps1 codex
.\scripts\agent.ps1 claude
.\scripts\agent.ps1 pi
.\scripts\agent.ps1 agy
```

The launcher does not install clients, copy credentials, or change other projects. Append client arguments after its name. The launcher declares no PowerShell parameters, so short flags such as `-p` and `-c` reach the client unchanged. A comma list such as `Read,Edit` stays one argument. PowerShell itself still splits `-name:value` into two arguments, and a call from a PowerShell prompt drops a bare `--`. Skill filtering depends on the client version and organization policy. Arguments that add other plugins or skill sources can bypass the intended isolation.

| Client | Project isolation | Verification and limits |
| --- | --- | --- |
| Codex CLI | Generates session-level `-c skills.config=...` overrides from discovered `SKILL.md` paths. Tool connections remain available. | `node scripts/pstack.mjs isolate-codex` generates the filter. `audit-codex` uses the same override with the real `skills/list` API. It verifies that exactly the PStack skills and the project skills are enabled, and confirms that an ordinary parent-directory session retains its other skills. |
| Codex desktop | Reads project PStack skills and `AGENTS.md`. | Version `0.160.0` ignores project-layer `skills.config` filters. No supported desktop setting for project-only skill hiding was found. Ordinary desktop conversations may display other skills. Use the CLI launcher for the verified isolation. |
| Claude Code | Uses `--setting-sources project,local` to exclude personal and synced skill sources. Disables bundled skills and hides doctor. | Shares `.claude/skills`. Organization-managed settings may still apply. After installing the client, inspect `/skills` and `/plugin`. |
| Pi | Uses `--no-extensions --no-skills --skill <project-directory> --no-prompt-templates`. | Explicitly loads only PStack and the project skills. Disabling extensions also disables subagent and MCP capabilities supplied by those extensions. Review any required extension before explicitly loading it from the project. An ordinary `pi` launch does not guarantee isolation. |
| Antigravity CLI | Discovers `.agents/skills` and reads `AGENTS.md` natively. The launcher stops if personal skill or plugin sources under `~/.gemini` contain anything. | `agy` has no isolated profile and no setting that hides personal or bundled skills. The project chose not to point `USERPROFILE` at a project folder, because every tool the agent starts, Git included, would inherit the fake home folder. Its eight bundled skills, such as `agy-customizations` and `antigravity_guide`, stay available. Inspect `/skills` after sign-in. |

Invoking project skills and hiding other skills are separate capabilities. The Codex desktop row records the desktop isolation limit. Global skill settings have not been changed to hide the other skills. Claude's complete skill catalog and the Pi and Antigravity isolation adapters remain unverified in their actual clients.

Install Antigravity CLI with Google's installer, then open a new terminal so `PATH` includes `agy`. The first `.\scripts\agent.ps1 agy` launch opens Google sign-in. Gemini CLI no longer serves individual Google accounts, so this project no longer uses it.

```powershell
irm https://antigravity.google/cli/install.ps1 | iex
```

The Codex launcher refreshes and verifies its filter before each launch. Running plain `codex` does not include these overrides. The path filter reflects the current installation, not a wildcard prohibition on future plugins. A client's management page may still list disabled entries. Verification checks that only PStack and the project skills are enabled in the session.

## Follow the workflow and share records

Engineering tasks follow PStack by default. State the goal, constraints, and a verifiable completion condition. `poteto-mode` selects the playbook. Explicit invocation is also available: `$poteto-mode` in Codex, `/poteto-mode` in Claude, and `/skill:poteto-mode` in Pi. In Antigravity CLI, ask the agent to use the poteto-mode skill.

- [`AGENTS.md`](../AGENTS.md) is the shared entry point. `CLAUDE.md` imports it, and Antigravity CLI reads it directly.
- [`docs/context.md`](context.md) stores stable facts and the agreed project direction.
- `docs/handoffs/YYYY-MM-DD-<task>.md` records a decision or design together with its rejected alternatives, evidence, and open issues. Only tasks that settle such a decision write one. Other tasks keep their evidence in the pull request. `node scripts/check-handoffs.mjs` fails when a record is not linked from `docs/context.md` or `docs/product-direction.md`, or when an inline link in either names a missing record. CI runs it.
- Agents that write code concurrently use separate branches and worktrees. The coordinator checks their evidence before integrating changes.

These versioned records support handoffs. They do not provide live messaging, cross-client scheduling, or complete conversation synchronization.

## Configure models for quality

[`.pstack/models.json`](../.pstack/models.json) assigns each development role its model and reasoning level, and [`.pstack/compatibility.md`](../.pstack/compatibility.md) states the policy and maps PStack's workflow roles to those roles. Run `node scripts/model-policy.mjs validate` to check the policy, and `node scripts/model-policy.mjs resolve <role>` to get the model and effort for a role, or the missing participants that block it. A blocked role exits with code 2. A ready route does not mean any review has run.

These settings govern the agents developing iDevelop. The application's provider connections and a programmatic dispatcher across providers remain separate product work.

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

The script owns the generated directory. Regeneration removes files and skills deleted upstream or from `skills/`. Put project customizations in the adapter script, shared configuration, or a project skill in `skills/`.

## References

- [Lauren Tan's PStack README](https://github.com/cursor/plugins/blob/main/pstack/README.md) and [official guide](https://github.com/cursor/plugins/tree/main/pstack/docs/guide). The author is Lauren Tan, @poteto.
- [Codex skills](https://learn.chatgpt.com/docs/build-skills), [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference), [project filtering limitation](https://github.com/openai/codex/issues/20210), and [filter configuration sources](https://github.com/openai/codex/blob/main/codex-rs/config/src/skills_config.rs).
- [Claude setting-source scope](https://code.claude.com/docs/en/agent-sdk/claude-code-features#control-filesystem-settings-with-settingsources) and [skill visibility](https://code.claude.com/docs/en/skills#override-skill-visibility-from-settings).
- [Pi resource loading flags](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/cli.md#resources).
- [Antigravity skills](https://antigravity.google/docs/skills/), [Antigravity CLI](https://github.com/google-antigravity/antigravity-cli), and the [Gemini CLI transition notice](https://github.com/google-gemini/gemini-cli/discussions/27274).
