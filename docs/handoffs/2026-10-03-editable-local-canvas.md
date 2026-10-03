# Editable local canvas

## Task

Build the first C# implementation slice. Open a project, create and connect tasks, edit their properties, save, and reopen the graph without a team service. The user chose Avalonia 11.3.22 with NodifyAvalonia 6.6.0 and asked to skip the compatibility test build.

## Checklist

- [x] 1. `how` over the affected subsystem. Skip, because no application code exists. Grounding came from `docs/product-direction.md`, `docs/context.md`, and the NodifyAvalonia 6.6.0 source at tag `v6.6.0` (`af46875`).
- [x] 2. `architect` for parallel design exploration. Phase A skipped as greenfield. Three arena runners each produced a design package, and a fresh cross-judge scored them. See Decisions.
- [x] 3. Write the throughput checkpoint as four todo items.
  - [x] Blocking first steps. The solution skeleton with pinned packages must restore, build, and run one headless test before any feature code.
  - [x] Independent workstreams. n/a: the canvas binds directly to the domain edits and the document, so the layers share one contract. CI and the license script are small and land with the skeleton.
  - [x] Shared mutable state. One worktree, one branch, one writer. Design runners wrote to separate scratch directories. The judge and reviewers are read-only.
  - [x] Smallest safe decomposition. One implementation owner and five sequenced commits. Splitting the code-coupled slice would serialize on the same contract and double review work.
- [x] 4. Delegate code-writing to a subagent using your configured feature model. Three fresh Opus 5.5 sessions at xhigh effort wrote the code: the implementer and two fix rounds. Each ran headless through `scripts/agent.ps1 claude` in accept-edits mode with an explicit Bash allowlist. A first launch in auto permission mode was blocked by the host's safety classifier and stopped before it changed anything.
- [x] 5. Verify on the matching surface. "Inconclusive" or wrong-surface is not a pass. Flag it. Headless tests drive the real `MainWindow`. The coordinator also drove the built executable's real window on Windows through UI Automation and inspected its screenshots. Linux and macOS are covered only by CI.
- [x] 6. Rebase into small, ordered commits. Stack follow-ups. Fixups were autosquashed into seven commits, and `git rebase --exec` built and tested each one.
- [x] 7. If the design is contested, `interrogate` before shipping. Skip, because the design was not contested. Two runners converged on it independently, and the cross-judge's synthesis matched it.
- [x] 8. Run **Opening a PR**.

## Decisions

- The user chose Avalonia 11.3.22 with NodifyAvalonia 6.6.0 and skipped a compatibility test build. NodifyAvalonia's newest release targets Avalonia 11, and its Avalonia 12 support exists only in unmerged pull requests.
- Three design runners worked from one brief. Candidate 1 was seeded with "edits as data", candidate 2 with "mutable aggregate", and candidate 3 was unseeded. Candidates 1 and 3 independently reached the same shape. A fresh cross-judge scored candidate 1 at 28, candidate 3 at 26, and candidate 2 at 20.
- Candidate 3 is the base. An immutable `Workflow` holds separate `Tasks`, `Connections`, and `Positions` collections. A closed `WorkflowEdit` union goes through one pure `Apply` that owns every graph rule. A move reuses the semantic collections, so a later approval can ignore layout by construction. Candidate 1 kept positions inside each task. The judge's recommended synthesis, candidate 1 with candidate 3's layout split, is the same artifact.
- Grafts from candidate 1. One open command accepts any existing folder and writes nothing until the first save. The app opens a folder named on its command line. Ports derive their connected state from the graph. Saves use unique temp names. More than one workflow file is refused.
- Grafts from candidate 2. Connection kinds change through commands from inspector buttons and an edge context menu, not a two-way combo box. The build treats non-exhaustive enum switches as errors.
- Fixes from the judge. Selecting a connection clears the task selection, because NodifyAvalonia 6.6.0 does not. Drops count only on connectors. Layout parsing is strict.
- Dependency and review connections are acyclic together. Context connections may form cycles. All three runners derived this from the product direction. It is an open question for the user.
- Rejected. CommunityToolkit.Mvvm, because setters submit edits and store nothing. Asynchronous save with external-change detection, which is deferred. Separate storage assemblies. The Inter font, because SIL OFL 1.1 is outside the permissive list.

## Review triage, round 1

A fresh Opus 5.5 session at xhigh effort reviewed the five commits read-only and requested changes. Each finding was assessed on its merits.

| Finding | Severity | Decision |
| --- | --- | --- |
| CI fails at the license step on `xunit.abstractions` 2.0.3 | High | Fix. The coordinator read both linked license texts, and both are Apache-2.0. |
| The license script trusts nuspec expressions and misses bundled notices | Medium | Fix. SkiaSharp and HarfBuzzSharp notice files include Skia's GIF decoder under MPL 1.1, GPL 2.0, or LGPL 2.1. Notice files now need a reviewed entry. |
| A `null` entry in a workflow file crashes the app | Medium | Fix with strict-parse errors. |
| A second workflow file can lock the user out after a save | Low | Fix. Save refuses beside another workflow file. |
| Opening another folder discards unsaved edits | Low | Fix with a Save, Don't save, Cancel prompt, used on close too. |
| The README run command edits the golden sample | Low | Fix. |
| A UTF-8 BOM breaks opening | Low | Fix. |
| The license script ignores `downloadDependencies` | Low | Dismiss for now. No runtime identifier is set, so the restore graph is complete. |
| Some synthesis behaviors lack tests | Low | Fix the selection-order, rejected-kind, and line-break gaps. |

The coordinator's real-window run added three items. A second "Add task" overlapped the first node and hid its output connector. The inspector was blank with nothing selected. `Avalonia.BuildServices` sends anonymous build telemetry, so CI opts out and the README documents the opt-out.

## Review triage, round 2

A second fresh Opus 5.5 review covered only the fix round and approved it. All of its low findings were taken as fixes. The placement footprint failed for wide nodes, so nodes get a maximum width. The notice check searched only root files with two name shapes, so it now searches every folder and pins each reviewed notice by SHA-256. A second close request could stack a second prompt. The folder-picker seam was public. Some dialog paths lacked tests, and the dialog lacked initial focus. The README put the telemetry opt-out after the build commands. The byte order mark literal was invisible in source.

A Comment Sicko pass ran in its own detached worktree, because the second review was reading the feature worktree at the time. It deleted 34 comments and trimmed 3, and it kept 20 that describe external dependency behavior or a public contract. Its rename flags became fix items. A deleted comment about where the Delete key binding sits became a test.

Every build in this session before the second fix round ran without `AVALONIA_TELEMETRY_OPTOUT`, so Avalonia's anonymous build telemetry was sent from the development machine. Later builds set the variable.

## Changed artifacts

- `iDevelop.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, and the committed `packages.lock.json` files pin the SDK and every package.
- `src/IDevelop.Core` holds the workflow model, its edit rules, the file format, and atomic save. It references no UI package.
- `src/IDevelop.Desktop` holds the Avalonia shell, the NodifyAvalonia canvas, the inspector, and the unsaved-changes dialog.
- `tests/IDevelop.Core.Tests` and `tests/IDevelop.Desktop.Tests` hold 38 and 37 tests. `samples/storage-change` is the golden workflow file.
- `scripts/check-licenses.mjs` checks every restored package's license and every bundled notice by hash.
- `.github/workflows/dotnet.yml` restores in locked mode, checks licenses, builds, and tests on Linux, Windows, and macOS, with Avalonia telemetry turned off.
- `README.md`, `AGENTS.md`, and `docs/context.md` describe the slice. This record and `2026-10-03-next-step.md` record the task.

## Commands and observed results

| Command | Result |
| --- | --- |
| `GIT_SEQUENCE_EDITOR=true git rebase --exec '<restore, build, test>' main` with `AVALONIA_TELEMETRY_OPTOUT=1` | Each of the seven commits restored in locked mode, built in Release with 0 warnings, and passed its tests. The tip had 38 Core and 37 Desktop tests passing. |
| `node scripts/check-licenses.mjs` | Exit 0. Two reviewed license exceptions and three reviewed notice groups. |
| `node scripts/pstack.mjs check` | PASS for 49 skills and the model configuration. |
| `pwsh scratchpad/verify/real-window.ps1` against the Release executable | 17 checks passed. It added tasks, typed English and Chinese text and two-line instructions, saved, checked the saved JSON, cancelled and then discarded through the unsaved-changes prompt, and reopened. Screenshots showed non-overlapping nodes, a trimmed long title, and the inspector hint. |

The real-window script lived in the session scratchpad and is not committed. It drives the window through UI Automation patterns only, so it never moves the mouse, and it does not cover dragging connections. Headless tests cover the drags with real pointer input.

## Open issues

- What a review connection blocks is a product decision. The code treats review like dependency for cycles. Changing it is one arm of `ConnectionKindRules.Blocks`.
- The Skia notice that Avalonia's native renderer ships names terms outside the permissive list. Examples are the GIF decoder's choice of MPL-1.1, GPL-2.0, or LGPL-2.1, and libmicrohttpd under LGPL-2.1 or eCos. Whether every listed component is linked into the shipped binaries is unverified. A distributed build needs a license decision and the notice file.
- Local builds still send Avalonia's build telemetry unless a developer sets `AVALONIA_TELEMETRY_OPTOUT=1`. The repository could turn it off for every build by overriding the `AvaloniaStats` target, but that choice belongs to the user.
- Save does not detect a workflow file changed on disk since it was loaded. One connection per ordered task pair rules out a dependency and a review between the same two tasks.
- Undo, agent and model settings, IME composition, screen-reader output, and 500-task rendering are unverified or out of scope.

## Next action

Watch the pull request's CI on Linux and macOS, and fix anything it finds. After the user settles the open product questions, merge and start the shared graph review milestone.
