# iDevelop project context

## Purpose

Build a graphical interface for coordinating multiple coding agents, including Codex, Pi, Claude Code, and Antigravity CLI. Shared project records should let agents continue each other's work. The product should eventually support generating a usable multi-model configuration from a natural-language request and manually editing it afterward.

## Current scope

The repository contains the PStack development environment, product research, and the C# desktop app on Avalonia 11.3.22 and NodifyAvalonia 6.6.0. The app opens a project folder, edits tasks and typed connections on a node canvas, and saves the workflow to `.idp/workflows/<workflow-id>.json` inside that folder without a server. It looks like PlanWeave's desktop app in a light theme and a dark theme. Each task names its agent client, model, and reasoning setting, and runs on its own with Claude Code, Codex, Pi, or Antigravity CLI in the project folder. Workflow scheduling, team storage, and the synchronization protocol remain open.

## Confirmed product direction

- The primary interface is a Miro or Unreal Blueprint-style node canvas, not a kanban board. AI generates a draft workflow that users can edit, including task relationships, agent, model, and supported reasoning settings.
- Users review generated workflows before execution.
- Both standalone solo mode and real-time team collaboration are first-release requirements. Solo mode needs no server installation or connection.
- Team mode uses an optional self-hosted synchronization service. All coding agents run on members' local machines. The service shares workflow state and does not execute agents.
- Multiple projects, workflow hierarchy navigation, dependency blocking, GitHub issues and pull-request review, and portable Markdown session records belong to the intended product.
- Provider integrations should use subscriptions or coding plans where supported, with API access also available. Provider policy and protocol compatibility must be assessed separately.
- Windows, macOS, and Linux are required. The user prefers a responsive modern UI without a JavaScript or TypeScript application stack.
- Use C# and Avalonia. The user is familiar with both and explicitly declined further framework comparisons. Use BAndysc's `NodifyAvalonia` as the starting node-editor component. Keep the local runner and optional synchronization service in C# and .NET.
- Prefer permissive dependencies such as MIT, Apache-2.0, and BSD for commercial distribution and company use without mandatory framework fees. Preserve the option of a proprietary product. No product license has been chosen.
- The interface must look like PlanWeave's desktop app: a sidebar with a project tree, rounded task cards, and floating canvas controls, in a light theme and a dark theme the user can switch between. Cards take PlanWeave's status colors once tasks have a status. It stays in Avalonia and NodifyAvalonia. The [product direction](product-direction.md#the-interface-follows-the-planweave-look) records the reference screenshot and what to adopt.

The [product direction](product-direction.md) records the selected stack, proposed runtime design, and acceptance cases. Framework selection is settled. Runtime design details remain provisional.

## Working decisions

- The project's working language is English, as defined in `AGENTS.md`.
- PStack is installed within this project from the official `cursor/plugins/pstack` source.
- The official upstream is a Git submodule at `.pstack/upstream`. Sparse checkout excludes the other plugin directories. Upgrades are explicit, reviewed changes to its recorded commit.
- All clients follow the same PStack workflow and shared handoff convention.
- Development model assignments are explicit in `.pstack/models.json`. Until the app prototype works, Claude Opus 5.5 does all frontend and backend implementation, reviews, judgment, and exploration. Each review runs in a fresh Opus session that did not write the change. After the prototype works, the user will choose each part's owner inside the app. A pending Opus route blocks work without substitution.
- Development invocations use explicit effort below max. Opus runs at xhigh. The unselected Astra and Gemini entries keep xhigh and high. The ceiling overrides upstream skill defaults. Claude Opus 5.5 passed a subscription-authenticated request with explicit xhigh through Claude Code 2.1.288. Gemini CLI was uninstalled after it stopped serving individual Google accounts. Antigravity CLI 1.2.16 (`agy`) replaces it. A signed-in request with `--model gemini-3.8-flash --effort high` reached Gemini 3.8 Flash (High). Project configuration does not change an existing chat's app-level setting.
- Other projects retain their existing skills and settings. Isolation changes belong to this checkout.
- Concurrent code writers use separate worktrees. Durable decisions belong in this file, the product direction, and the few handoff records they link. Git history keeps everything else. Private chat transcripts never become records.
- An immutable `Workflow` value holds tasks, connections, and layout in separate collections. One pure `Workflow.Apply` owns every graph rule, and the file loader replays edits through it. A move never changes the semantic collections, so a later approval can ignore layout. The [canvas handoff](handoffs/2026-10-03-editable-local-canvas.md) records the design and its alternatives.
- Dependency and review connections are acyclic together, and context connections may form cycles. This is provisional until the user confirms what a review connection blocks, which the workflow execution phase settles.
- Central package management and committed lock files pin every package. `scripts/check-licenses.mjs` fails on a license outside MIT, Apache-2.0, BSD-2-Clause, and BSD-3-Clause, and on a bundled notice file without a reviewed entry.
- The SkiaSharp and HarfBuzzSharp native packages that Avalonia renders through ship one third-party notice. It names terms outside the permissive list, including Skia's GIF decoder under MPL-1.1, GPL-2.0, or LGPL-2.1. Distributing a build needs a license decision on those terms.
- `Avalonia.BuildServices` sends anonymous build telemetry. CI sets `AVALONIA_TELEMETRY_OPTOUT=1`, and the README explains the local opt-out.
- `Application.RequestedThemeVariant` is the only runtime theme state. The app writes `iDevelop/settings.json` in the per-user application data folder only when the user picks a theme. The [restyle record](handoffs/2026-10-04-planweave-restyle.md) records the design and its alternatives.
- The app's own colors come from `src/IDevelop.Desktop/Theme/Tokens.axaml`, which `scripts/planweave-tokens.mjs` generates from PlanWeave's color tokens. Its `--check` mode runs in CI. It fails on a stale file, and on the color forms the README lists elsewhere in the desktop project's XAML and C# files. It does not catch every way to make a color.
- A task's run edits the project folder itself. Several tasks of a project folder can run at once, and each task runs at most once at a time, across every app instance. An operating-system lock on `.idp/attempts/<task-id>/run.lock` enforces it. The user asked for parallel runs on 2026-10-04, after phase 3 first shipped with one run per folder. Git worktrees and branches wait for workflow execution, when several tasks run at once. The [execution record](handoffs/2026-10-04-single-task-execution.md) records the design and its alternatives.
- Each client runs in its own documented non-interactive mode with a JSON event stream, and the prompt goes over stdin. The client's own event stream decides success, because Pi exits with code 0 after a failed turn. Each attempt is an append-only event log at `.idp/attempts/<task-id>/<attempt-id>/events.jsonl`, which one pure reducer folds into the attempt's record. Git ignores that folder.
- The workflow file format is `idevelop.workflow/2`, which stores each task's `execution` (client, model, and reasoning). The app reads and writes only version 2. The user dropped version 1 on 2026-10-04, because no version 1 files remain.
- The app finds clients on PATH, plus the current machine and user PATH from the registry on Windows and the login shell's PATH on macOS and Linux. It reads them again at each refresh of the AGENTS section. Each client's own commands decide its readiness and its models. Claude Code has no model list command, so iDevelop carries its model list. No per-user client setting exists.
- A run may edit files in the project folder. Commands follow each client's non-interactive rules. Claude Code denies commands it has not been allowed, Codex runs them in its workspace sandbox, Antigravity CLI blocks them, and Pi has no permission system. On Windows each client runs in a Job Object, so Cancel, leaving the project, or closing iDevelop during a run stops every process the client started. A run that ends on its own leaves running what the client started on purpose, such as a dev server, on every platform.
- iDevelop works around these NodifyAvalonia 6.6.0 defects, so a Nodify upgrade should retest each one. Connections draw over the cards. The minimap does not follow a moved task, and its wheel zooms about 0.2% per notch. The animated pan does not update the bound viewport location. Connections never raise the context menu request. Commands on buttons outside the editor never reach it. The editor's key gestures, including Ctrl+A, still do not fire.

## Next product step

The editable local canvas, the PlanWeave restyle, and single-task execution are complete. CI builds them and passes their headless tests on Linux, Windows, and macOS. On Windows, `scripts/check-real-window.ps1` checks the built window. The [execution record](handoffs/2026-10-04-single-task-execution.md) lists the real runs of all four clients.

Workflow execution is next. A workflow runs as a dependency graph, with linked execution across its tasks. That phase also settles what a review connection blocks, and when runs need their own Git worktrees and branches. Team synchronization comes after it and stays a first-release requirement. The [delivery order](product-direction.md#delivery-order) records each phase's completion condition and the proposals that wait for the user.
