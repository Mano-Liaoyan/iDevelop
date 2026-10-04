# iDevelop

A project for building a graphical interface that coordinates multiple coding agents. The product direction and the C#, .NET, and Avalonia stack are selected in [`docs/context.md`](docs/context.md). The first application slice is a desktop editor for one project's workflow. It opens a project folder, edits tasks and their connections on a node canvas, and saves the workflow inside the folder without a server.

English is the project's working language. The shared policy is in [`AGENTS.md`](AGENTS.md).

This project uses [Lauren Tan's PStack, @poteto](https://github.com/cursor/plugins/tree/main/pstack), version `0.15.6`, pinned to commit `23e4138daa01c42d4969f7a5465f82704e64f798`. Its 49 skills, 24 principles, and 23 playbooks come from the official source. This repository adds local adapters for Codex, Pi, Claude Code, and Antigravity CLI. These adapters are maintained by this project and are not an official PStack port.

## Initialize the project

Install Node.js 22 or later, Git, and the agent client you want to use. Building the application also needs the .NET SDK described in [Build and run the application](#build-and-run-the-application).

```powershell
git clone https://github.com/Mano-Liaoyan/iDevelop.git
cd iDevelop
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
```

Setup downloads the recorded PStack submodule revision and generates `.agents/skills` inside the project. Claude and Cursor link to that directory. Antigravity CLI discovers it natively, and the Pi launcher loads it explicitly. Run setup after cloning or creating a worktree. Initial setup needs network access. Generated files and personal settings are excluded from commits.

The upstream repository lives at `.pstack/upstream`. The main repository records its URL and commit. PStack belongs to the official `cursor/plugins` monorepo, so the submodule points to that repository. Setup uses sparse checkout to expose `pstack` and upstream root files without expanding other plugin directories. The source and license are under `.pstack/upstream/pstack`.

A submodule keeps upstream changes to a version pointer in this repository's commits and reviews. A subtree would keep upstream files as ordinary tracked files. Setup installs no global plugin. Earlier source snapshots remain in Git history.

## Build and run the application

Install the .NET 10 SDK that [`global.json`](global.json) pins, version `10.0.401` or a later patch in the same feature band.

Avalonia's `Avalonia.BuildServices` package sends anonymous usage data when a project builds. Its `AvaloniaStats` build target runs before each compile. According to the package's own README, it sends the build timestamp, the hashed project and machine names, an anonymous machine identifier, the output type, target framework, runtime identifier, Avalonia version, and license tier, the development environment, the operating system and architecture, and the detected CI system. The same README says it sends no source code, file paths, or personal information. CI opts out with `AVALONIA_TELEMETRY_OPTOUT=1`. To opt out locally, set that variable in your shell before you build, or set it once in your user environment.

In PowerShell:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
```

In bash or zsh:

```bash
export AVALONIA_TELEMETRY_OPTOUT=1
```

Run these commands from the repository root:

```powershell
dotnet restore --locked-mode
dotnet build -c Release
dotnet test -c Release
node scripts/check-licenses.mjs
node scripts/planweave-tokens.mjs --check
dotnet run --project src/IDevelop.Desktop
```

The solution is [`iDevelop.slnx`](iDevelop.slnx). `src/IDevelop.Core` holds the workflow model, its edit rules, and the project file format. It has no UI dependency. `src/IDevelop.Desktop` is the Avalonia application with the NodifyAvalonia canvas. The tests in `tests/IDevelop.Desktop.Tests` drive the real main window headlessly with pointer and keyboard input.

On Windows, `scripts/check-real-window.ps1 -Exe src/IDevelop.Desktop/bin/Release/net10.0/IDevelop.Desktop.exe -OutDir <folder>` drives the built app's real window through UI Automation without moving the mouse. It edits and saves a workflow, answers the unsaved-changes prompt, reopens the project, and switches the theme across a restart. Each run writes its projects and screenshots to a new timestamped folder inside the given folder. The script replaces your theme preference while it runs and restores it afterward, or on its next start if it was killed.

Choose the folder button beside **PROJECT** in the sidebar, or name a folder after `--` in the run command to open it at start. Any existing folder opens, including a repository. Its workflow is saved to `.idp/workflows/<workflow-id>.json`, which travels with the repository and reviews as an ordinary diff. The first save creates that folder. Earlier builds wrote the same file format to a differently named data folder, so renaming that folder to `.idp` by hand is enough to open it. [`samples/storage-change`](samples/storage-change) is a three-task example. Copy it to a scratch folder and open the copy, or open a folder of your own. Saving rewrites the opened folder's workflow file, and the tests compare the sample byte for byte.

**New task** in the sidebar, or **Add task** in the canvas menu, adds a task. The sidebar lists the open project's tasks. Choosing one there selects its card, opens it in the inspector, and scrolls the canvas to it when it is out of view. Drag a task's output onto another task's input to make the second task depend on the first. Click a connection to change its kind in the inspector, or right-click it. Dependency and review connections cannot form a cycle. Context connections can. Delete removes the selected tasks and connections. The save button at the end of the breadcrumb over the canvas, Ctrl+S, or Cmd+S on macOS saves. The buttons at the canvas's bottom left zoom in, zoom out, and fit every task on the screen. The minimap at the bottom right shows the whole workflow. Click or drag in it to move the canvas there, and turn the mouse wheel over it to zoom.

The **System**, **Light**, and **Dark** switch at the bottom of the sidebar sets the theme. **System** follows the operating system and is the default. The app remembers the choice per user in `iDevelop/settings.json` under `%APPDATA%` on Windows, `~/Library/Application Support` on macOS, and `$XDG_CONFIG_HOME` or `~/.config` on Linux. A project folder holds no theme setting.

Every color the app sets comes from `src/IDevelop.Desktop/Theme/Tokens.axaml`. `scripts/planweave-tokens.mjs` generates that file from PlanWeave's `oklch` color tokens, converted to sRGB. To change a color, edit the tables in the script and run `node scripts/planweave-tokens.mjs`. The `--check` option fails when the generated file is stale. It also fails when any other `.axaml`, `.xaml`, or `.cs` file under `src/IDevelop.Desktop` contains one of these:

- A hex color in the `#RGB`, `#ARGB`, `#RRGGBB`, or `#AARRGGBB` form. A character reference such as `&#160;` is not a hex color.
- In XAML, an attribute value or element content that is only one of Avalonia's color names, in any letter case, other than `Transparent`. `Black` is also a font weight, so the check skips the value of a `FontWeight` attribute and the value of a setter written `Property="FontWeight" Value="Black"`.
- In C#, `Brushes.` or `Colors.` followed by a name, `Color.Parse`, or a `Color.From` method.

The check matches nothing else. It misses a color name inside a longer value, such as a `BoxShadow`, and `{x:Static Colors.Red}` in XAML. In C# it misses a color made any other way, such as `new Color(...)` or `Brush.Parse("Red")`.

Central package management in [`Directory.Packages.props`](Directory.Packages.props) pins direct dependencies, and the committed `packages.lock.json` files pin transitive ones. After a restore, `node scripts/check-licenses.mjs` prints every package with its SPDX license. It fails on a license outside MIT, Apache-2.0, BSD-2-Clause, and BSD-3-Clause, or on a package without a license expression that has no reviewed exception in the script. It also searches every folder of each package for third-party notice and `COPYING` files. Each notice needs a reviewed entry that records its SHA-256 and names every license in it outside that list, so a changed notice fails until someone reads it again. The script prints those summaries after the table. The SkiaSharp and HarfBuzzSharp native packages share one notice that names MPL-1.1, GPL-2.0, LGPL-2.1, and other licenses for bundled code such as Skia's GIF decoder. CI runs the restore, license check, token check, build, and tests on Linux, Windows, and macOS.

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

Edit [`.pstack/models.json`](.pstack/models.json). Until the app prototype works, the user assigned every development role to one model.

| Work | Required model | Reasoning |
| --- | --- | --- |
| Frontend and backend implementation, review, judgment, and exploration | Claude Opus 5.5 | xhigh |

Each review runs in a fresh Opus session that did not write the change. After the prototype works, the user will choose each part's owner inside the app. Reassigning a role also changes `requiredRoles` in `scripts/model-policy.mjs` and its tests.

Every dispatch needs an explicit supported effort at or below xhigh. Max, higher levels, and inherited effort are prohibited. Provider effort names do not imply identical token budgets. The policy overrides upstream skill defaults.

Run `node scripts/model-policy.mjs validate` to check the policy. Run `node scripts/model-policy.mjs resolve backend-implementation` to inspect a route. Missing participants produce a blocked result and exit code 2. A partially available panel does not run, and a missing provider is not replaced automatically. The resolver prepares instructions; it does not launch clients or authenticate accounts.

Claude Code 2.1.288 passed a subscription-authenticated request to `claude-opus-5-5` with explicit xhigh, so the Opus route is active. The registry keeps two verified routes that no role selects yet. Astra is available through the native Codex catalog. Antigravity CLI 1.2.16 passed a signed-in request with `--model gemini-3.8-flash --effort high`, and its stream output and log identified Gemini 3.8 Flash (High) as the served model. A ready route does not mean any review has run.

[`.codex/config.toml`](.codex/config.toml) sets new project launches to Astra at xhigh. No role uses this default until the user reassigns work. It does not change an existing conversation or prevent explicit app or CLI overrides. Check the active setting when starting work. [`.pstack/compatibility.md`](.pstack/compatibility.md) maps PStack's workflow roles to the scoped assignments.

These settings govern the agents developing iDevelop. The application's provider connections and a programmatic dispatcher across providers remain separate product work.

## Upstream practices and adaptations

The setup follows the official guide's task routing, on-demand principle loading, reproduction before repair, verification through real behavior, independent review, separate worktrees for concurrent writers, and recorded handoffs. PStack's Build the Lever and Prove It Works principles informed the repeatable setup and discovery checks.

See [`.pstack/compatibility.md`](.pstack/compatibility.md) for adaptation boundaries. Upstream references to `cursor-team-kit`, Cursor cloud orchestration, Bun, and Bash helpers do not imply global installation. Agents must report missing capabilities and distinguish substitutions from tools they actually ran. The application's headless tests drive its real main window with pointer and keyboard input.

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
