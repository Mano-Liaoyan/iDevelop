# Next step after the data folder rename

## Task

Answer what the project should do next. This is a read-only investigation. It changes no application code, dependencies, or setup files.

## Investigation checklist

- [x] Route through the **how** skill. For motivation questions, also route through the **why** skill. Skip the explainer delegate, because the answer comes from `docs/context.md`, `docs/product-direction.md`, and two handoff records the coordinator read in full. No motivation question was asked.
- [x] Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only investigation`.
- [x] Produce the `how`-shaped output (Overview / Key Concepts / How It Works / Where Things Live / Gotchas), or a recommendation with a tradeoffs table if the request is a decision between alternatives. Produced a recommendation with a table that maps each PlanWeave element to an iDevelop feature.
- [x] Apply the **unslop** skill to the reply.

## Findings

The next step is already recorded. `docs/context.md` and `2026-10-03-idp-data-folder.md` both name the PlanWeave restyle under the Feature playbook, followed by shared graph review. The user scheduled that order. `main` matches `origin/main` at `7b71940`, and the working tree was clean.

The reference screenshot shows more controls than iDevelop has features. The product direction says a control appears only when iDevelop has the matching feature. `TaskDefinition` holds a title, instructions, and acceptance criteria. It has no status and no agent. `WorkflowDocument` opens one workflow per project. Undo is out of scope in the canvas handoff, and nothing executes. The restyle therefore covers these parts:

| PlanWeave element | iDevelop today | In the restyle |
| --- | --- | --- |
| Light theme, typography, spacing, corner radii | Dark Fluent placeholder | Yes |
| Rounded card with title and prompt preview | Title and instructions exist | Yes |
| Status pill and status-colored fill | No task status | No. Every card uses the white planned style |
| Agent picker | No agent field | No |
| Block stack | PlanWeave's own domain | No |
| Orthogonal connections with arrowheads | Three connection kinds | Yes |
| Sidebar with project tree and count badges | One project and one workflow | Yes, for the project and its tasks |
| Statistics, Todo, Search, Notifications, Settings, Reset layout | None | No |
| Breadcrumb | Project and workflow names | Yes |
| Undo and redo buttons | Undo out of scope | No |
| Components panel | One "Add task" command | No panel. "New task" moves into the sidebar |
| Zoom and fit controls, minimap | Not shown | Yes |
| Run bar | No execution | No |

`Nodify.dll` in NodifyAvalonia 6.6.0 contains the type and member names `StepConnection`, `CircuitConnection`, `Minimap`, `ZoomIn`, `ZoomOut`, and `FitToScreen`. Their presence is inferred from the binary's strings. Their behavior on Avalonia 11.3.22 is unverified.

PlanWeave's `packages/desktop/src/renderer/index.css` at `8647d015` imports `@fontsource-variable/geist`, shadcn tokens, and Tailwind. Its colors are `oklch` values, and its radii derive from one `--radius` token. The Geist repository reports `OFL-1.1`, which is outside the project's allowed list of MIT, Apache-2.0, BSD-2-Clause, and BSD-3-Clause.

## Commands and observed results

- `git fetch --prune`, then `git log main..origin/main` printed nothing.
- `git branch -r --merged origin/main` listed `origin/claude/adoring-cannon-kke4h8` and `origin/feat/editable-local-canvas`, so both are fully merged.
- A string search of `~/.nuget/packages/nodifyavalonia/6.6.0/lib/netstandard2.0/Nodify.dll` found each name listed above.
- `gh api` read PlanWeave's file tree and `index.css` at `8647d015`, and the license of `vercel/geist-font`.

No build, test, or UI run happened.

## Open issues

- Bundling Geist needs the user to accept OFL-1.1 for fonts. The alternative is the platform's system font.
- What a review connection blocks, the Skia notice terms, and local Avalonia build telemetry remain open from the canvas handoff. None of them blocks the restyle.
- The two merged remote branches can be deleted only with the user's approval.

## Next action

Run the Feature playbook for the restyle with the scope in the table. Start with a script that converts PlanWeave's `oklch` tokens to Avalonia resources, then deliver the theme, cards, connections, shell, and floating controls as separate commits, each checked by the tests and the real-window UI Automation script.
