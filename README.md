# iDevelop

A project for building a graphical interface that coordinates multiple coding agents. The product direction and the C#, .NET, and Avalonia stack are selected in [`docs/context.md`](docs/context.md). The application is a desktop editor for one project's workflow. It opens a project folder, edits nodes and their connections on a canvas, saves the workflow inside the folder without a server, and runs each node's agent with Claude Code, Codex, Pi, or Antigravity CLI. A node is an agent: its model and effort, system prompt, instructions, skills, conversation, input from earlier agents or a person, and output for later ones.

English is the project's working language. The shared policy is in [`AGENTS.md`](AGENTS.md).

The agents that develop iDevelop use Matt Pocock's skills with GitHub Issues, which [`docs/development-environment.md`](docs/development-environment.md) describes.

## Build and run the application

Install Git, Node.js 22 or later, and the .NET 10 SDK that [`global.json`](global.json) pins, version `10.0.401` or a later patch in the same feature band. Then clone the repository:

```powershell
git clone https://github.com/Mano-Liaoyan/iDevelop.git
cd iDevelop
```

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
node scripts/fluent-icons.mjs --check
dotnet run --project src/IDevelop.Desktop
```

The solution is [`iDevelop.slnx`](iDevelop.slnx). `src/IDevelop.Core` holds the workflow model, its edit rules, the project file format, and the engine that runs a workflow's nodes. Its folders follow its namespaces: `Clients` holds each agent client's launch arguments and wire protocol, `Processes` contains child processes, `Git` wraps the repository, `Engine` holds the workflow engine, its node logs, its agents, and the node worktrees, `Nodes` reads what an agent's reply ends with and what a planner proposes, `Workflows` holds the workflow model and the blueprints, and `Projects` holds the workflow files, the blueprint library, and the project's data folder. It has no UI dependency. `tests/IDevelop.FakeAgent` is a stand-in client that replays recorded client output, so the tests start real processes without any agent client installed. `tests/IDevelop.GitExit` exits in the middle of a Git call, so a test can check what that leaves running. `src/IDevelop.Desktop` is the Avalonia application with the NodifyAvalonia canvas. The tests in `tests/IDevelop.Desktop.Tests` drive the real main window headlessly with pointer and keyboard input.

On Windows, `scripts/check-real-window.ps1 -Exe src/IDevelop.Desktop/bin/Release/net10.0/IDevelop.Desktop.exe -OutDir <folder>` drives the built app's real window through UI Automation without moving the mouse. It edits and saves a workflow, answers the unsaved-changes prompt, reopens the project, and switches the theme across a restart. It reads the AGENTS section and the agent pickers against the clients installed on your machine. It then runs and cancels a task through a fake Codex from the Release build of `tests/IDevelop.FakeAgent`, so the run needs no agent account. Each run writes its projects and screenshots to a new timestamped folder inside the given folder. The script replaces your theme preference while it runs and restores it afterward, or on the next start of a real-window run if it was killed. It refuses to start while a `verify-idevelop` session holds the preference. That skill drives the same window one feature at a time through the script's module, [`scripts/real-window.psm1`](scripts/real-window.psm1), and keeps its evidence in `.verify/`.

Choose the folder button beside **PROJECT** in the sidebar, or name a folder after `--` in the run command to open it at start. Any existing folder opens, including a repository. Its workflow is saved to `.idp/workflows/<workflow-id>.json`, which travels with the repository and reviews as an ordinary diff. The first save creates that folder. [`samples/storage-change`](samples/storage-change) is a three-task example. Copy it to a scratch folder and open the copy, or open a folder of your own. Saving rewrites the opened folder's workflow file, and the tests compare the sample byte for byte.

Each task is a node of a type. The built-in types are **Implement**, **Plan**, **Architect**, **Review**, **Approval**, and **Merge**, and a project or personal library adds your own. Each kind has its own color and glyph. To add a node, right-click or double-click empty canvas, press N, or choose **Add Node** in the sidebar. The Add popover lists every type and a few actions, and typing filters them. Drag from a task's output to empty canvas to add a connected node there, or drop it on another task's input to make the second task depend on the first. A dependency waits for its source, and a context connection only reads it. Dependencies cannot form a cycle. Context connections can. Right-click a card for Run, Choose Agent, Stop or Retry when they apply, Rename, Duplicate, Replace With, Disconnect, Derive Blueprint, Save as Blueprint, and Delete. Right-click a connection to change its kind, insert a node into it, or delete it. On the canvas, Delete removes the selection, Ctrl+D duplicates it, Ctrl+A selects every task, F2 renames, and F fits every task on the screen. Ctrl+Z undoes a workflow edit, and Ctrl+Shift+Z or Ctrl+Y redoes it. The breadcrumb's Undo and Redo buttons do the same. The sidebar lists the open project's tasks. Choosing one there selects its card, opens it in the inspector, and scrolls the canvas to it when it is out of view. The save button at the end of the breadcrumb over the canvas, Ctrl+S, or Cmd+S on macOS saves. The buttons at the canvas's bottom left zoom in, zoom out, and fit every task on the screen. The minimap at the bottom right shows the whole workflow. Click or drag in it to move the canvas there, and turn the mouse wheel over it to zoom.

The inspector shows the selected task's fields, agent, run, and conversation in foldable sections. Type in its filter box to find a property by name. A revert arrow beside a value sets it back to its type's default. With nothing selected, the inspector shows the workflow's kinds and the blueprint library.

**Generate** at the canvas's top right, or **Generate Workflow…** on an empty canvas, drafts a workflow from a description. It places a **Plan** task in **Chat** mode with your description as its goal and runs it with the planner agent you choose. The planner proposes tasks, which show as dashed cards. Nothing joins the workflow until you choose **Accept** or **Accept and Finish** in the planner's inspector. Reply to the planner to change the proposal first.

The **System**, **Light**, and **Dark** switch at the bottom of the sidebar sets the theme. **System** follows the operating system and is the default. The app remembers the choice per user in `iDevelop/settings.json` under `%APPDATA%` on Windows, `~/Library/Application Support` on macOS, and `$XDG_CONFIG_HOME` or `~/.config` on Linux. A project folder holds no theme setting.

Every color the app sets comes from `src/IDevelop.Desktop/Theme/Tokens.axaml`. `scripts/planweave-tokens.mjs` generates that file. Surfaces, text, and borders come from PlanWeave's `oklch` color tokens, converted to sRGB. The accent, the node kinds, and the states come from Apple's system colors. To change a color, edit the tables in the script and run `node scripts/planweave-tokens.mjs`. The `--check` option fails when the generated file is stale. It fails when a pair in the script's contrast list falls below its least WCAG 2 contrast ratio in either theme, which is 4.5:1 for text and 3:1 for a glyph, stroke, or tile. It also fails when any other `.axaml`, `.xaml`, or `.cs` file under `src/IDevelop.Desktop` contains one of these:

- A hex color in the `#RGB`, `#ARGB`, `#RRGGBB`, or `#AARRGGBB` form. A character reference such as `&#160;` is not a hex color.
- In XAML, an attribute value or element content that is only one of Avalonia's color names, in any letter case, other than `Transparent`. `Black` is also a font weight, so the check skips the value of a `FontWeight` attribute and the value of a setter written `Property="FontWeight" Value="Black"`.
- In C#, `Brushes.` or `Colors.` followed by a name, `Color.Parse`, or a `Color.From` method.

The check matches nothing else. It misses a color name inside a longer value, such as a `BoxShadow`, and `{x:Static Colors.Red}` in XAML. In C# it misses a color made any other way, such as `new Color(...)` or `Brush.Parse("Red")`.

Every icon comes from Microsoft's [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons), which are MIT licensed. [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) holds the license, and the build copies it beside the app. `scripts/icons/fluent-icons.json` pins the source commit and lists each icon's key, asset folder, and 16 px SVG file. `node scripts/fluent-icons.mjs` writes `src/IDevelop.Desktop/Theme/FluentIcons.axaml` from the SVG files in `scripts/icons/fluent/`, and fails on a file that is not 16 by 16, fills even-odd, has a transform, or draws anything but paths.

To add an icon, add its entry to the manifest and run `node scripts/fluent-icons.mjs --fetch`, which downloads each missing file and then writes the XAML. It skips files that exist, so a move to another commit starts from an empty folder. The `--check` option fails when the generated file is stale, and when the manifest lacks a key that XAML names as `{StaticResource IconName}` or `{DynamicResource IconName}`, or that C# names in a string such as `"IconRun"`. Draw an icon with `<PathIcon Theme="{StaticResource Glyph}" Data="{StaticResource IconRun}" />`. The `Glyph` theme keeps the 16 unit frame each icon is drawn in, so icons keep their relative sizes.

Central package management in [`Directory.Packages.props`](Directory.Packages.props) pins direct dependencies, and the committed `packages.lock.json` files pin transitive ones. After a restore, `node scripts/check-licenses.mjs` prints every package with its SPDX license. It fails on a license outside MIT, Apache-2.0, BSD-2-Clause, and BSD-3-Clause, or on a package without a license expression that has no reviewed exception in the script. It also searches every folder of each package for third-party notice and `COPYING` files. Each notice needs a reviewed entry that records its SHA-256 and names every license in it outside that list, so a changed notice fails until someone reads it again. The script prints those summaries after the table. The SkiaSharp and HarfBuzzSharp native packages share one notice that names MPL-1.1, GPL-2.0, LGPL-2.1, and other licenses for bundled code such as Skia's GIF decoder. GitHub Actions runs the restore, license check, token check, icon check, build, and tests on Windows and macOS for a `v*` release tag or when started by hand.

## Run a node with an agent client

Install and sign in to the clients you want iDevelop to run: Claude Code (`claude`), Codex (`codex`), Pi (`pi`), or Antigravity CLI (`agy`). iDevelop finds them on PATH. On Windows it also reads your current user and machine PATH, so it finds a client installed after iDevelop or the terminal that started it. On macOS and Linux it also reads your login shell's PATH, so it finds a client installed through Homebrew, npm, or nvm when the app starts from Finder or a desktop launcher. The **AGENTS** section of the sidebar shows whether each client is ready, and why not. Pi checks sign-in for each provider, so a Pi model whose provider is signed out cannot run while Pi's other models can. The refresh button beside **AGENTS** checks the clients again.

To run a node:

1. Select the node on the canvas or in the sidebar.
2. In the inspector's **Agent** section, choose the client, the model, and the reasoning level. Each list offers only what that client offers on this machine. A model saved on another machine stays visible, marked as not offered here.
3. Choose **Run**. The node starts, and each node after it starts once all of its predecessors are complete. If the node cannot start, Run is off and the reason appears under it.

**Run Workflow** at the canvas's top right runs every node that is not completed or stale, along its dependency connections. Every node that is ready starts at once.

The card's ring, glyph, and subtitle follow the node's state: **Idle**, **Running**, **Input required**, **Completed**, **Failed**, or **Stale**. A result goes stale when a node before it has a newer result, or when you change the node's instruction, model, or connections. When a node before it runs again, a stale node runs again by itself in a fresh session. When you edit a running node, it stops and waits for your **Run**. **Retry** starts the node again in a fresh session. **Stop** stops the client and the processes it started, and the bar at the bottom of the canvas offers **Stop All**. When a client exits, iDevelop stops what it left running, through a Job Object on Windows and the process group the turn leads on macOS and Linux. If iDevelop closes while a node runs, the node reopens as **Input required**, and you choose to continue its session or retry it.

The inspector's **Conversation** setting decides when a node waits for you. **Autonomous** never waits. **May ask** waits when the agent ends a turn with a question, and **Chat** waits after every turn. A waiting node shows the question under **Talk to the Agent**. Your reply continues the node's session in the client's own session. No process runs while a node waits, so the wait survives closing iDevelop. **Open in terminal** copies the client's command for the session while the node waits. The button at the top of the canvas counts the waiting nodes and selects the next one.

An **Approval** node waits for you once the nodes before it complete. **Approve** hands their results on. **Send back** sends your note to the nodes before it, which continue in their own sessions, and the Approval node waits for you again on their new results. A **Review** node's reject sends its findings back the same way, and after three rounds the review waits for you.

Each editing node works in its own Git worktree at `.worktrees/<workflow>/<node>` in the project folder, on its own branch `idp/<workflow>/<node>`, and each turn ends with one commit. A node starts from the merge of its predecessors' commits, and a node with no predecessors from your branch's last commit. A conflict goes to a **Merge** node connected after the conflicting nodes, or the node waits for you: **Open worktree** opens its worktree, and **Mark resolved** commits your fix. Once every node is completed and nothing is stale, **Merge into my branch** merges the work of the last nodes into the branch you have checked out. iDevelop never merges into your branch by itself. A node's prompt comes from its node type's template and its fields, such as an Implement node's title, instructions, and acceptance criteria. Each client keeps its own rules for what it may do without asking:

- Claude Code in an editing node runs every tool without asking. In a read-only node it runs in plan mode.
- Codex may edit files and runs commands in its workspace sandbox.
- Antigravity CLI may edit files, and its commands stay blocked.
- Pi has no permission system. It can edit any file and run any command as you.

On Windows, Pi and npm installs of the other clients are `.cmd` shims that run through `cmd.exe`. iDevelop sets `NoDefaultCurrentDirectoryInExePath` for every client, so `cmd.exe` never runs a program from the project folder by its bare name. The setting reaches the agent's own commands too, so a batch file in the project folder runs as `.\build.cmd`, not `build.cmd`. It also refuses to start a `.cmd` client for a project on a network path, because `cmd.exe` would run it in `C:\Windows` instead.

A project opens in one iDevelop window at a time. Each node keeps one append-only log of its results and its client session under `.idp/`, which `.idp/.gitignore` keeps out of Git. Workflow files use format `idevelop.workflow/3`, which embeds one copy of each node type the workflow uses and stores each node's type, field values, agent, model, reasoning level, and conversation mode. Opening a format 2 file converts it, says what it converted, and the next save writes format 3.

## License

A license for this project's original files has not yet been selected.
