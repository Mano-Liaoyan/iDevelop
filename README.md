# iDevelop

A project for building a graphical interface that coordinates multiple coding agents. The development environment is ready. Product requirements, the interface, and the technology stack remain undecided.

English is the project's working language. The shared policy is in [`AGENTS.md`](AGENTS.md).

This project uses [Lauren Tan's PStack, @poteto](https://github.com/cursor/plugins/tree/main/pstack), version `0.15.6`, pinned to commit `23e4138daa01c42d4969f7a5465f82704e64f798`. Its 49 skills, 24 principles, and 23 playbooks come from the official source. This repository adds local adapters for Codex, Pi, Claude Code, and Antigravity CLI. These adapters are maintained by this project and are not an official PStack port.

## Initialize the project

Install Node.js 22 or later, Git, and the agent client you want to use. There are no application dependencies to install yet.

```powershell
git clone https://github.com/Mano-Liaoyan/iDevelop.git
cd iDevelop
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
```

Setup downloads the recorded PStack submodule revision and generates `.agents/skills` inside the project. Claude and Cursor link to that directory. Antigravity CLI discovers it natively, and the Pi launcher loads it explicitly. Run setup after cloning or creating a worktree. Initial setup needs network access. Generated files and personal settings are excluded from commits.

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
.\scripts\agent.ps1 agy
```

The launcher does not install clients, copy credentials, or change other projects. Append client arguments after its name. The launcher declares no PowerShell parameters, so short flags such as `-p` and `-c` reach the client unchanged. A comma list such as `Read,Edit` stays one argument. PowerShell itself still splits `-name:value` into two arguments, and a call from a PowerShell prompt drops a bare `--`. Skill filtering depends on the client version and organization policy. Arguments that add other plugins or skill sources can bypass the intended isolation.

| Client | Project isolation | Verification and limits |
| --- | --- | --- |
| Codex CLI | Generates session-level `-c skills.config=...` overrides from discovered `SKILL.md` paths. Tool connections remain available. | `node scripts/pstack.mjs isolate-codex` generates the filter. `audit-codex` uses the same override with the real `skills/list` API. It verifies exactly 49 enabled PStack skills and confirms that an ordinary parent-directory session retains its other skills. The model's actual prompt was also checked for the same 49 entries. |
| Codex desktop | Reads project PStack skills and `AGENTS.md`. | Version `0.160.0` ignores project-layer `skills.config` filters. No supported desktop setting for project-only skill hiding was found. Ordinary desktop conversations may display other skills. Use the CLI launcher for the verified isolation. |
| Claude Code | Uses `--setting-sources project,local` to exclude personal and synced skill sources. Disables bundled skills and hides doctor. | Shares `.claude/skills`. Organization-managed settings may still apply. After installing the client, inspect `/skills` and `/plugin`. |
| Pi | Uses `--no-extensions --no-skills --skill <project-directory> --no-prompt-templates`. | Explicitly loads only PStack. Disabling extensions also disables subagent and MCP capabilities supplied by those extensions. Review any required extension before explicitly loading it from the project. An ordinary `pi` launch does not guarantee isolation. |
| Antigravity CLI | Discovers `.agents/skills` and reads `AGENTS.md` natively. The launcher stops if personal skill or plugin sources under `~/.gemini` contain anything. | `agy` has no isolated profile and no setting that hides personal or bundled skills. Its eight bundled skills, such as `agy-customizations` and `antigravity_guide`, stay available. Inspect `/skills` after sign-in. |

Install Antigravity CLI with Google's installer, then open a new terminal so `PATH` includes `agy`. The first `.\scripts\agent.ps1 agy` launch opens Google sign-in. Gemini CLI no longer serves individual Google accounts, so this project no longer uses it.

```powershell
irm https://antigravity.google/cli/install.ps1 | iex
```

The Codex launcher refreshes and verifies its filter before each launch. Running plain `codex` does not include these overrides. The path filter reflects the current installation, not a wildcard prohibition on future plugins. A client's management page may still list disabled entries. Verification checks that only PStack skills are enabled in the session.

Codex skill isolation has been tested on this machine. Claude Code's subscription authentication and exact Opus model access have also been verified. Claude's complete skill catalog and the Pi and Antigravity isolation adapters remain unverified in their actual clients.

## Follow the workflow and share records

Engineering tasks follow PStack by default. State the goal, constraints, and a verifiable completion condition. `poteto-mode` selects the playbook. Explicit invocation is also available: `$poteto-mode` in Codex, `/poteto-mode` in Claude, and `/skill:poteto-mode` in Pi. In Antigravity CLI, ask the agent to use the poteto-mode skill.

- [`AGENTS.md`](AGENTS.md) is the shared entry point. `CLAUDE.md` imports it, and Antigravity CLI reads it directly.
- [`docs/context.md`](docs/context.md) stores stable facts and the agreed project direction.
- `docs/handoffs/YYYY-MM-DD-<task>.md` records each task's decisions, evidence, unfinished work, and continuation steps. Each task writes its own file.
- Agents that write code concurrently use separate branches and worktrees. The coordinator checks their evidence before integrating changes.

These versioned records support handoffs. They do not provide live messaging, cross-client scheduling, or complete conversation synchronization.

## Configure models for quality

Edit [`.pstack/models.json`](.pstack/models.json). The version 2 policy records these user-selected development assignments.

| Work | Required model | Reasoning |
| --- | --- | --- |
| Backend implementation | GPT-6 Astra | xhigh |
| Avalonia UI implementation | Claude Opus 5.5 | xhigh |
| Backend review | Claude Opus 5.5 | xhigh |
| Frontend review | Gemini 3.8 Flash and GPT-6 Astra | high and xhigh |
| Judgment and difficult-task review | GPT-6 Astra and Claude Opus 5.5 | xhigh |

Every dispatch needs an explicit supported effort at or below xhigh. Max, higher levels, and inherited effort are prohibited. Provider effort names do not imply identical token budgets. The policy overrides upstream skill defaults.

Run `node scripts/model-policy.mjs validate` to check the policy. Run `node scripts/model-policy.mjs resolve backend-implementation` to inspect a route. Missing participants produce a blocked result and exit code 2. A partially available panel does not run, and a missing provider is not replaced automatically. The resolver prepares instructions; it does not launch clients or authenticate accounts.

Astra is available through the native Codex catalog. Claude Code 2.1.288 passed a subscription-authenticated request to `claude-opus-5-5` with explicit xhigh, so the Opus route is active. Antigravity CLI 1.2.16 passed a signed-in request with `--model gemini-3.8-flash --effort high`. Its stream output and log identified Gemini 3.8 Flash (High) as the served model, so the Gemini route is active. Frontend and backend review now resolve. A ready route does not mean the review has run.

[`.codex/config.toml`](.codex/config.toml) sets new project launches to Astra at xhigh. It does not change an existing conversation or prevent explicit app or CLI overrides. Check the active setting when starting work. [`.pstack/compatibility.md`](.pstack/compatibility.md) maps PStack's workflow roles to the scoped assignments.

These settings govern the agents developing iDevelop. The application's provider connections and a programmatic dispatcher across providers remain separate product work.

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
- [Antigravity skills](https://antigravity.google/docs/skills/), [Antigravity CLI](https://github.com/google-antigravity/antigravity-cli), and the [Gemini CLI transition notice](https://github.com/google-gemini/gemini-cli/discussions/27274).

Upstream PStack uses the [MIT license](https://github.com/cursor/plugins/blob/23e4138daa01c42d4969f7a5465f82704e64f798/pstack/LICENSE). A license for this project's original files has not yet been selected.
