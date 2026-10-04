# Restyle the app to the PlanWeave look

## Task

Restyle the shell and canvas to look like PlanWeave's desktop app. The user accepted the scope in `2026-10-03-restyle-next-step.md` and added a requirement: a light theme and a dark theme that the user can switch between. The coordinator read "两个可以切换" as that pair, because PlanWeave's stylesheet defines both token sets. The user accepted the platform system font in place of Geist.

## Checklist

- [x] 1. `how` over the affected subsystem. Two fresh read-only Opus 5.5 explorers at xhigh traced PlanWeave's visual values at `8647d015` and the NodifyAvalonia 6.6.0 and Avalonia 11.3.22 facilities. The coordinator read the desktop project directly, because it is under 900 lines.
- [x] 2. `architect` for parallel design exploration. Two fresh Opus 5.5 runners at xhigh produced design packages from opposing seeds. A fresh cross-judge scored them 26 to 23 for candidate A. See Decisions.
- [x] 3. Write the throughput checkpoint as four todo items.
  - [x] Blocking first steps. The token generator, the generated color file, and the theme switch land first, because every later commit styles through their brushes.
  - [x] Independent workstreams. n/a: the cards, connections, and floating controls share `WorkflowCanvasView.axaml`, and the shell commit moves the inspector out of it. The test helper `Shell.cs` changes in three commits.
  - [x] Shared mutable state. One worktree, one branch, one writer. Design runners and the judge were read-only. The coordinator edits only this record while the implementer runs.
  - [x] Smallest safe decomposition. One implementation owner with five sequenced commits, because the commits touch the same XAML and test helper files.
- [x] 4. Delegate code-writing to a subagent using your configured feature model. A fresh Opus 5.5 session at xhigh wrote the five commits through `scripts/agent.ps1 claude` in accept-edits mode, limited to dotnet, git, node, and read-only shell commands. A second fresh session made the first fix round.
- [x] 5. Verify on the matching surface. "Inconclusive" or wrong-surface is not a pass. Flag it. The coordinator drove the built Release executable's real window on Windows through UI Automation and inspected screenshots in both themes. That run found the two theme defects the headless tests missed. After the fix round, `scripts/check-real-window.ps1` passed all 31 checks. The title band's drag and double-click, and the macOS and Linux windows, are unverified. See Open issues.
- [x] 6. Rebase into small, ordered commits. Stack follow-ups. The fix round folded each fix into the commit that introduced the problem. The coordinator then checked out each of the five commits in a separate detached worktree and ran the full gate on it. The real-window script and the documentation are two stacked commits.
- [x] 7. If the design is contested, `interrogate` before shipping. Skip, because the design was not contested. The cross-judge and the coordinator picked the same base and grafts independently.
- [x] 8. Run **Opening a PR**. Pull request 4 is open and ready, with every check passing. Its description carries no attribution line, as the user prefers.

## Decisions

- The two design runners worked from one brief with opposing seeds. Candidate A kept the theme inside Avalonia with no new C# types. Candidate B modeled an explicit appearance preference in a view model and restyled `nodify:Node`. A fresh cross-judge scored A 26 and B 23, with A ahead on single source of truth, reader load, and verifiability. The coordinator reached the same pick before reading the judge.
- A is the base. `Application.RequestedThemeVariant` is the only runtime theme state. The disk form is `{"theme":"system"|"light"|"dark"}` in `iDevelop/settings.json` under the per-user application data folder, parsed at the boundary. Every color comes from `Theme/Tokens.axaml`, which `scripts/planweave-tokens.mjs` generates from PlanWeave's `oklch` tokens. Its `--check` mode fails on a stale file or a color literal elsewhere. Connection kinds map to colors through style classes.
- Grafts from B. The title bands collapse where the OS ignores the extended title bar, which is Linux. macOS stores the preference under `~/Library/Application Support`. The 24 px dot grid stays, checked by rendered pixels.
- The coordinator's graft. Choosing a task in the sidebar brings its card into view.
- Rejected from B. The appearance view model and preferences type, because they duplicate the theme state. The 320 px card and wider side columns, because they move about ten test fixtures and push the pan test's press point outside the editor at 1280 px.
- Both runners chose a System, Light, and Dark switch with System as the first-run default. The user asked for two switchable themes. System is a third choice that follows the OS. The user can ask for a two-way switch instead.
- The card stays 240 px wide in a 260 px container instead of PlanWeave's 320 px. iDevelop's card holds only a title and an instructions preview.
- Connection kinds keep distinct colors, because PlanWeave has one edge type and colors edges by source node. Dependency uses PlanWeave's blue and review its orange in both themes. Context uses the theme's muted text color and a dash.
- The implementer found that NodifyAvalonia 6.6.0 never applies its rule that draws connections under the cards, because its connection host reports its style key as `ItemsControl`. One style puts the connection layer below the cards. This bug predates the restyle.
- NodifyAvalonia 6.6.0 also never refreshes a minimap item after a task moves. A class handler on `MinimapItem.LocationProperty` re-measures the minimap panel.

## Review triage, round 1

A fresh Opus 5.5 session at xhigh reviewed the five commits read-only and requested changes. The coordinator had already found the first two on the real window.

| Finding | Severity | Decision |
| --- | --- | --- |
| Selecting a theme segment through UI Automation checks it but applies nothing. The theme followed the Click command, and `IsChecked` was a one-way mirror | High | Fix. The checked state becomes the input |
| The first launch writes `{"theme":"system"}`, every launch rewrites the file, and each headless test left a file behind. The test runs had left 1,018 folders under `%TEMP%\idevelop-tests` | Medium | Fix. Write only when the user picks a theme |
| Pressing or dragging a partly visible card pans the canvas, and the pan can add to the drag | Medium | Fix. Only a sidebar choice moves the canvas |
| The minimap wheel zooms about 0.2% per notch in either direction, a NodifyAvalonia 6.6.0 bug | Medium | Fix with one zoom step per notch |
| Tab moves from New task to the theme switch before the project and tasks | Low | Fix with a sidebar in visual order |
| No headless test proves the startup read | Low | Dismiss. The committed real-window check proves it across a real restart |
| The item container's clip setter has no basis in the Nodify source | Low | Remove unless a test fails without it |
| The token check misses named colors and C# color literals, and the README overclaims | Low | Fix both |
| Zoom buttons never show as unavailable | Low | Dismiss. Fit on an empty canvas does nothing harmful |
| Three pixel assertions only check that a color differs | Low | Fix with exact colors |
| The status pill can cover the breadcrumb in a narrow canvas column | Low | Fix. No overlap at the minimum window size |

## Fix round 1

A fresh Opus 5.5 session at xhigh made every accepted fix and folded it into its commit. Each new test failed before its fix.

- The theme segments carry their variant in `Tag`, and an `IsCheckedChanged` handler calls `App.Choose`. A pointer click, the keyboard, and UI Automation's select all apply the theme. Arrow keys do not move the check inside the group.
- `App.Choose` returns early when the theme is already current, and it is the only writer of the file. A launch never writes it.
- The sidebar list moves the canvas only while it has keyboard focus. Pressing, dragging, and rubber-band selecting a partly hidden card, and the select-all command, leave the viewport at (0, 0). The pan is instant, because NodifyAvalonia's animated pan never updates the view model's viewport location.
- A tunnel wheel handler on the minimap zooms one step per notch in the wheel's direction.
- The sidebar is a grid in visual order, so Tab goes from New task to the folder button and the tasks before the theme switch.
- The container clip setter stays, because the selection ring test fails without it.
- The token check scans only `.axaml`, `.xaml`, and `.cs` files and also flags Avalonia's named colors other than `Transparent` and C# color literals.
- The status pill sits under the breadcrumb, wraps to at most 10 lines, and shows the whole message in its tooltip. A test at the 900 by 600 minimum with a 4,640-character message checks that it intersects neither the breadcrumb, the zoom panel, nor the minimap.
- A headless test's temporary folder uses its app's preferences folder as its root, so five full test runs left nothing under `%TEMP%\idevelop-tests`.

## Review triage, round 2

A second fresh Opus 5.5 session reviewed the fix round, the real-window script, and the documentation. It confirmed every round 1 fix, found no regression, and requested small changes.

| Finding | Severity | Decision |
| --- | --- | --- |
| Choosing the already selected task in the sidebar does nothing, so an off-screen selected card stays hidden | Medium | Fix with a test |
| `docs/context.md` reported CI results for a branch CI had not run | Low | Fix. The sentence was removed, and CI results are recorded below once the pull request ran |
| The real-window script deleted existing project folders under the output folder | Low | Fix. Each run writes to a new timestamped folder |
| A killed run lost the user's theme preference, and paths with brackets broke | Low | Fix. A backup beside the file is restored at the end or at the next start, and paths are literal |
| Two script checks claimed more than they tested | Low | Fix. A hand-written file is compared byte for byte after a launch, and the third launch checks that no file exists |
| A relative output folder broke the screenshots | Low | Fix. The script resolves it first |
| The token check missed short hex colors, flagged `FontWeight="Black"`, and the README overclaimed | Low | Fix both and narrow the wording |
| The context listed too few NodifyAvalonia workarounds | Low | Fix. It lists every one, so an upgrade can retest them |
| The handoff records were untracked | Low | Fix. They are in the documentation commit |
| Commit 4's pill comment named controls from commit 5 | Low | Fix |
| The context still required status-colored cards | Low | Fix. Status colors wait for a task status |
| The early return in `App.Choose` might be untested | Low | Fix with an assertion that fails without it |

The coordinator fixed the script and the documentation. A fresh Opus 5.5 session made the code fixes, and each new test failed before its fix.

- Tapping a sidebar row, or pressing Space or Enter on it, brings that row's card into view even when it is already selected. The handler uses the row the event came from, so a tap in the gap between rows moves nothing.
- The token check matches the four hex forms, skips character references such as `&#160;`, and skips font weight values. The README lists exactly what it matches and what it misses.
- `The_switch_marks_the_theme_set_in_code` asserts the file still reads `{"theme":"dark"}` after code sets System. It was the only test that failed with the early return removed.

The round 2 code fixes and the coordinator's script and documentation fixes were not reviewed a third time. Each has a test or a real-window run that fails without it.

## Changed artifacts

- `scripts/planweave-tokens.mjs` holds PlanWeave's `oklch` tokens pinned to `8647d015`, converts them to sRGB, and writes `src/IDevelop.Desktop/Theme/Tokens.axaml`. `--check` runs in CI.
- `src/IDevelop.Desktop/App.axaml` merges Nodify's switching `Theme.axaml` and the tokens. `App.axaml.cs` reads, applies, and writes the theme preference. `Program.cs` chooses the preference folder per platform.
- `Theme/Controls.axaml` styles the shell controls by class. `Theme/Icons.axaml` holds six icons drawn for iDevelop.
- `MainWindow.axaml` lays out the title bands, the sidebar, the canvas column with the breadcrumb and status pill, and the inspector. `Canvas/WorkflowCanvasView.axaml` holds the card template, the step connections, the dot grid, the zoom panel, and the minimap. `ConnectionKindStyles.cs` is deleted.
- `tests/IDevelop.Desktop.Tests` grows from 37 to 75 tests, including `ThemeTests.cs`.
- `scripts/check-real-window.ps1` drives the built window on Windows. `README.md`, `docs/context.md`, and `docs/product-direction.md` describe the restyle.

## Commands and observed results

| Command | Result |
| --- | --- |
| Each of the seven commits after fix round 2, checked out in a detached worktree, with `dotnet restore --locked-mode`, `dotnet build -c Release`, `dotnet test -c Release`, `node scripts/check-licenses.mjs`, and `node scripts/planweave-tokens.mjs --check`, with `AVALONIA_TELEMETRY_OPTOUT=1` | Every commit passed with 0 warnings and left nothing under `%TEMP%\idevelop-tests`. Core stayed at 38 tests. Desktop had 50, 52, 56, 70, 75, 75, and 75. |
| `scripts/check-real-window.ps1` against the Release build of the final tip | 32 checks passed. A first launch wrote no preference, UI Automation selection applied and saved each theme, Dark survived a restart, a launch left a hand-written preference file byte for byte, and the script left no preference file because none existed before. |
| The same script after planting a backup as a killed run would leave it, with a relative output folder | 32 checks passed. The planted preference was restored, no backup was left, and the screenshots landed in the resolved folder. |
| Every headless test run in this task after fix round 1 | Nothing was left under `%TEMP%\idevelop-tests`. |
| CI runs 37169024908 and 37169024911 on `cb2b75d`, for the push and the pull request | Both passed on Linux, Windows, and macOS. The Linux and macOS logs show 38 Core and 75 Desktop tests passing and a current token file, so the exact pixel colors hold off Windows too. |
| The coordinator's first real-window run against the round-1 build | Failed at "the card shows the typed title" because a `Panel` has no UI Automation peer, which the script then worked around. It then found the two theme defects. |
| `node oklch-tokens.mjs` over PlanWeave's `index.css` | `oklch(0.922 0 0)` converted to `#E5E5E5` and `oklch(0.145 0 0)` to `#0A0A0A`, which match shadcn's neutral palette. |

## Open issues

- The title band's drag, double-click to maximize, and Aero Snap on Windows are unverified, because the script never moves the mouse. The macOS traffic lights over the sidebar band and the collapsed bands on Linux are unverified, because no such machine was available. CI passed the headless tests on both.
- In the dark theme, the minimap's viewport window has weak contrast against its mask.
- Arrow keys do not move the check inside the theme switch. Space and UI Automation do.
- NodifyAvalonia 6.6.0's editor key gestures, including Ctrl+A, never fire. This predates the restyle.
- The open questions from the canvas handoff remain: what a review connection blocks, the Skia notice terms, local Avalonia build telemetry, external-change detection, and one connection per task pair.
- Two fully merged remote branches, `claude/adoring-cannon-kke4h8` and `feat/editable-local-canvas`, can be deleted only with the user's approval.

## Next action

Review and merge the pull request. Then start the shared graph review milestone.
