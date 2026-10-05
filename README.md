# iDevelop

A project for building a graphical interface that coordinates multiple coding agents. The product direction and the C#, .NET, and Avalonia stack are selected in [`docs/context.md`](docs/context.md). The application is a desktop editor for one project's workflow. It opens a project folder, edits tasks and their connections on a node canvas, saves the workflow inside the folder without a server, and runs a single task with Claude Code, Codex, Pi, or Antigravity CLI.

English is the project's working language. The shared policy is in [`AGENTS.md`](AGENTS.md).

The agents that develop iDevelop use PStack and the project's client launchers, which [`docs/development-environment.md`](docs/development-environment.md) describes.

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
dotnet run --project src/IDevelop.Desktop
```

The solution is [`iDevelop.slnx`](iDevelop.slnx). `src/IDevelop.Core` holds the workflow model, its edit rules, the project file format, and the engine that finds the agent clients and runs a task. It has no UI dependency. `tests/IDevelop.FakeAgent` is a stand-in client that replays recorded client output, so the tests start real processes without any agent client installed. `src/IDevelop.Desktop` is the Avalonia application with the NodifyAvalonia canvas. The tests in `tests/IDevelop.Desktop.Tests` drive the real main window headlessly with pointer and keyboard input.

On Windows, `scripts/check-real-window.ps1 -Exe src/IDevelop.Desktop/bin/Release/net10.0/IDevelop.Desktop.exe -OutDir <folder>` drives the built app's real window through UI Automation without moving the mouse. It edits and saves a workflow, answers the unsaved-changes prompt, reopens the project, and switches the theme across a restart. It reads the AGENTS section and the agent pickers against the clients installed on your machine. It then runs and cancels a task through a fake Codex from the Release build of `tests/IDevelop.FakeAgent`, so the run needs no agent account. Each run writes its projects and screenshots to a new timestamped folder inside the given folder. The script replaces your theme preference while it runs and restores it afterward, or on the next start of a real-window run if it was killed. It refuses to start while a `verify-idevelop` session holds the preference. That skill drives the same window one feature at a time through the script's module, [`scripts/real-window.psm1`](scripts/real-window.psm1), and keeps its evidence in `.verify/`.

Choose the folder button beside **PROJECT** in the sidebar, or name a folder after `--` in the run command to open it at start. Any existing folder opens, including a repository. Its workflow is saved to `.idp/workflows/<workflow-id>.json`, which travels with the repository and reviews as an ordinary diff. The first save creates that folder. [`samples/storage-change`](samples/storage-change) is a three-task example. Copy it to a scratch folder and open the copy, or open a folder of your own. Saving rewrites the opened folder's workflow file, and the tests compare the sample byte for byte.

**New task** in the sidebar, or **Add task** in the canvas menu, adds a task. Each task is a node of a type, and the only type so far is the built-in **Implement**, whose fields are its instructions and acceptance criteria. The sidebar lists the open project's tasks. Choosing one there selects its card, opens it in the inspector, and scrolls the canvas to it when it is out of view. Drag a task's output onto another task's input to make the second task depend on the first. Click a connection to change its kind in the inspector, or right-click it. A dependency waits for its source, and a context connection only reads it. Dependencies cannot form a cycle. Context connections can. Delete removes the selected tasks and connections. The save button at the end of the breadcrumb over the canvas, Ctrl+S, or Cmd+S on macOS saves. The buttons at the canvas's bottom left zoom in, zoom out, and fit every task on the screen. The minimap at the bottom right shows the whole workflow. Click or drag in it to move the canvas there, and turn the mouse wheel over it to zoom.

The **System**, **Light**, and **Dark** switch at the bottom of the sidebar sets the theme. **System** follows the operating system and is the default. The app remembers the choice per user in `iDevelop/settings.json` under `%APPDATA%` on Windows, `~/Library/Application Support` on macOS, and `$XDG_CONFIG_HOME` or `~/.config` on Linux. A project folder holds no theme setting.

Every color the app sets comes from `src/IDevelop.Desktop/Theme/Tokens.axaml`. `scripts/planweave-tokens.mjs` generates that file from PlanWeave's `oklch` color tokens, converted to sRGB. To change a color, edit the tables in the script and run `node scripts/planweave-tokens.mjs`. The `--check` option fails when the generated file is stale. It also fails when any other `.axaml`, `.xaml`, or `.cs` file under `src/IDevelop.Desktop` contains one of these:

- A hex color in the `#RGB`, `#ARGB`, `#RRGGBB`, or `#AARRGGBB` form. A character reference such as `&#160;` is not a hex color.
- In XAML, an attribute value or element content that is only one of Avalonia's color names, in any letter case, other than `Transparent`. `Black` is also a font weight, so the check skips the value of a `FontWeight` attribute and the value of a setter written `Property="FontWeight" Value="Black"`.
- In C#, `Brushes.` or `Colors.` followed by a name, `Color.Parse`, or a `Color.From` method.

The check matches nothing else. It misses a color name inside a longer value, such as a `BoxShadow`, and `{x:Static Colors.Red}` in XAML. In C# it misses a color made any other way, such as `new Color(...)` or `Brush.Parse("Red")`.

Central package management in [`Directory.Packages.props`](Directory.Packages.props) pins direct dependencies, and the committed `packages.lock.json` files pin transitive ones. After a restore, `node scripts/check-licenses.mjs` prints every package with its SPDX license. It fails on a license outside MIT, Apache-2.0, BSD-2-Clause, and BSD-3-Clause, or on a package without a license expression that has no reviewed exception in the script. It also searches every folder of each package for third-party notice and `COPYING` files. Each notice needs a reviewed entry that records its SHA-256 and names every license in it outside that list, so a changed notice fails until someone reads it again. The script prints those summaries after the table. The SkiaSharp and HarfBuzzSharp native packages share one notice that names MPL-1.1, GPL-2.0, LGPL-2.1, and other licenses for bundled code such as Skia's GIF decoder. CI runs the restore, license check, token check, build, and tests on Linux, Windows, and macOS.

## Run a task with an agent client

Install and sign in to the clients you want iDevelop to run: Claude Code (`claude`), Codex (`codex`), Pi (`pi`), or Antigravity CLI (`agy`). iDevelop finds them on PATH. On Windows it also reads your current user and machine PATH, so it finds a client installed after iDevelop or the terminal that started it. On macOS and Linux it also reads your login shell's PATH, so it finds a client installed through Homebrew, npm, or nvm when the app starts from Finder or a desktop launcher. The **AGENTS** section of the sidebar shows whether each client is ready, and why not. Pi checks sign-in for each provider, so a Pi model whose provider is signed out cannot run while Pi's other models can. The refresh button beside **AGENTS** checks the clients again.

To run a task:

1. Select the task on the canvas or in the sidebar.
2. In the inspector's **Agent** section, choose the client, the model, and the reasoning level. Each list offers only what that client offers on this machine. A model saved on another machine stays visible, marked as not offered here.
3. Choose **Run**. If the task cannot start, the reason appears under the button and in the status line, and nothing starts.

The card's status pill and color follow the task's latest run: **Running**, **Waiting for you**, **Succeeded**, **Failed**, **Cancelled**, or **Interrupted**. The inspector shows the last run's configuration, timing, result, and recent activity. While a task runs, a bar at the bottom of the canvas shows it. **Cancel** stops the client and the processes it started. A run that ends on its own leaves running what the client started on purpose, such as a dev server or a browser it opened for you. On Windows each client runs in a Job Object, so Cancel, leaving the project, or iDevelop closing during a run stops every process the client started. On macOS and Linux, a process that the client started and that outlives it keeps running. Closing the window or opening another folder during a run asks first, and stopping there records the run as interrupted. If iDevelop stops during a run, the next open of the project records the run as interrupted and stops its client if it is still running.

The inspector's **Conversation** setting decides when a task waits for you. **Autonomous** never waits. **May ask** waits when the agent ends a turn with a question, and **Chat** waits after every turn. A waiting task shows the question under **TALK TO THE AGENT**. Your reply continues the same run in the client's own session, **Mark done** finishes it, and **Cancel** ends it. No process runs while a task waits, so the wait survives closing iDevelop. **Open in terminal** copies the client's command for the session while the task waits. The button at the top of the canvas counts the waiting tasks and selects the next one.

A run starts the client in the project folder. Its prompt is the task's title, instructions, and acceptance criteria. Several tasks of a project can run at once, all in the same project folder, so two agents can edit the same files. Each task runs once at a time, across every iDevelop window. The run bar shows the newest running task and how many others run. Each client keeps its own rules for what it may do without asking:

- Claude Code may edit files and is denied commands you have not allowed in its settings.
- Codex may edit files and runs commands in its workspace sandbox.
- Antigravity CLI may edit files, and its commands stay blocked.
- Pi has no permission system. It can edit any file and run any command as you.

On Windows, Pi and npm installs of the other clients are `.cmd` shims that run through `cmd.exe`. iDevelop sets `NoDefaultCurrentDirectoryInExePath` for every client, so `cmd.exe` never runs a program from the project folder by its bare name. The setting reaches the agent's own commands too, so a batch file in the project folder runs as `.\build.cmd`, not `build.cmd`. It also refuses to start a `.cmd` client for a project on a network path, because `cmd.exe` would run it in `C:\Windows` instead.

Each run is a folder `.idp/attempts/<task-id>/<attempt-id>/` in the project. `events.jsonl` is iDevelop's record of the run, `output.jsonl` holds the client's raw output, and `stderr.log` holds its error output. `.idp/.gitignore` keeps these folders out of Git. Workflow files use format `idevelop.workflow/3`, which embeds one copy of each node type the workflow uses and stores each task's type, field values, agent, model, reasoning level, and conversation mode. Opening a format 2 file converts it, says what it converted, and the next save writes format 3.

## License

A license for this project's original files has not yet been selected.
