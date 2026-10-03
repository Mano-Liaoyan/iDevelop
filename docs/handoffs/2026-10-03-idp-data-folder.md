# Rename the project data folder and plan the restyle

## Task

The user asked for two things. First, rename the folder that holds a project's iDevelop data from `.idevelop` to `.idp`. Second, record that the interface must be restyled to look like PlanWeave's desktop app, using the PlanWeave repository and a screenshot. The user scheduled the restyle for the next step and asked that it not be built in this session.

## Checklist

- [x] 1. `how` over the affected subsystem. Skip. `docs/handoffs/2026-10-03-editable-local-canvas.md` already maps the storage, and `git grep -F .idevelop` found every use.
- [x] 2. `architect` for parallel design exploration. Skip. Renaming one constant adds no boundary or type.
- [x] 3. Write the throughput checkpoint as four todo items.
  - [x] Blocking first steps. n/a: one constant and its paths.
  - [x] Independent workstreams. n/a: one small change.
  - [x] Shared mutable state. One worktree and one writer at a time. The coordinator edited docs only after the delegate finished.
  - [x] Smallest safe decomposition. One delegate made one commit.
- [x] 4. Delegate code-writing to a subagent using your configured feature model. A fresh Opus 5.5 session at xhigh made the rename in `ea95d51`. The coordinator wrote the SDK pin and the documentation.
- [x] 5. Verify on the matching surface. "Inconclusive" or wrong-surface is not a pass. Flag it. Tests pass, and the built app's real window saved into `.idp`.
- [x] 6. Rebase into small, ordered commits. Stack follow-ups. Three commits: the rename, the SDK pin, and the documentation.
- [x] 7. If the design is contested, `interrogate` before shipping. Skip, because nothing was contested.
- [x] 8. Run **Opening a PR**.

## Decisions

- A project's data now lives in `<project>/.idp/`. The file format tag stays `idevelop.workflow/1`, because the format did not change.
- No code migrates an old `.idevelop` folder. The slice merged hours earlier, and the format is unchanged, so renaming an old folder by hand is enough. The README says so.
- `global.json` now pins SDK 10.0.401. Its earlier pin, 10.0.203, was no longer installed on the development machine, and every `dotnet` command refused to start. 10.0.401 is the newest .NET 10 SDK, released on 2026-09-08. The lock files did not change.
- The restyle is the first item of the next step, before shared graph review, so later features are built in the final style. `docs/product-direction.md` records the reference, its visual traits, and the rule to adopt its look without adopting PlanWeave's domain. The screenshot is committed as `docs/design/planweave-canvas-reference.png`, because the original was a temporary file.

## Changed artifacts

- `src/IDevelop.Core/Projects/WorkflowDocument.cs` uses `.idp`. The golden sample moved to `samples/storage-change/.idp/` with identical bytes. Three test files and the README use the new path.
- `global.json` and the README pin and name SDK 10.0.401.
- `docs/product-direction.md` adds the visual design section and a requirement. `docs/context.md` records the new folder, the visual direction, and the next step.

## Commands and observed results

| Command | Result |
| --- | --- |
| `dotnet --list-sdks` | Only 10.0.401 is installed. |
| `dotnet --version` with the old `global.json` | Refused to start because 10.0.203 is missing. |
| `dotnet restore --locked-mode`, `dotnet build -c Release`, `dotnet test -c Release` on 10.0.401 with `AVALONIA_TELEMETRY_OPTOUT=1` | Restore passed with unchanged lock files. The build had 0 warnings. 38 Core and 37 Desktop tests passed. |
| `node scripts/check-licenses.mjs` | Exit 0. |
| `git grep -n -F .idevelop -- ':!docs'` | No output. |
| The real-window UI Automation script against the Release executable | 17 checks passed. The project folder held `.idp/.gitignore` and one file under `.idp/workflows/`, and no `.idevelop` folder. |

## Open issues

- The first build on the new SDK installed an untrusted ASP.NET Core HTTPS development certificate and showed the .NET CLI telemetry banner. Whether `DOTNET_CLI_TELEMETRY_OPTOUT` is set on the machine is unverified.
- Open questions from the canvas handoff remain. They cover what a review connection blocks, the Skia notice terms, local Avalonia build telemetry, external-change detection, and one connection per task pair.

## Next action

Restyle the shell and canvas to the PlanWeave look under the Feature playbook. Study the PlanWeave repository at `8647d015` for exact values first. Then continue with shared graph review.
