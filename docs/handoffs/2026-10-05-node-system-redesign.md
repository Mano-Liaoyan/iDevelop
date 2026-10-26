# Node system redesign

## Task

On 2026-10-05 the user asked for a new node editor design. The user wanted the function of n8n, Unreal Engine Blueprints, and Godot, with a UI that follows Apple's design philosophy. The user's points were these:

- The canvas menu should offer every node type and other quick actions, so adding a node needs no side panel.
- Cards should carry little text and vivid colors from a tested palette, preferably Apple's, with a logo for each node type and each menu action, and colors for each state and role.
- The right panel should borrow from Godot's inspector.
- A button in the workflow should let a person describe the work to an agent, which then generates the whole workflow.

Pull requests [#20](https://github.com/Mano-Liaoyan/iDevelop/pull/20) to [#30](https://github.com/Mano-Liaoyan/iDevelop/pull/30) built it.

## Decisions

| # | Decision | Reason | Rejected |
| --- | --- | --- | --- |
| D1 | Keep PlanWeave's neutral surfaces, text, and borders. Add Apple's system colors for kinds, states, and the accent. | The user asked for vivid, coordinated color and named Apple's palette. Keeping the surfaces leaves the shell as it was. | Replacing every surface color. |
| D2 | Use the current HIG values. Light strokes and glyphs use the increased-contrast values. Text stays neutral except destructive menu text (`#D70015`) and links (`#0040DD`). White text sits only on `#1E6EF4` and `#E9152D`. | The increased-contrast values reach 4.5:1 only on pure white, so they suit 3:1 strokes and glyphs but not text. `planweave-tokens.mjs --check` holds each listed pair to its WCAG minimum in both themes. | Colored subtitle text, at 4.34:1 on menus. White on `#0088FF`, at 3.52:1. |
| D3 | Icons come from Microsoft's Fluent UI System Icons, MIT, pinned to commit `a563cf9166f4f91aa617557ed272612b7f0a2f72`, 16 px assets only. | MIT is inside the license policy, and the set has a glyph for every kind, state, and action. | Lucide (ISC, outside the license list). SF Symbols (licensed for Apple platforms only). Phosphor (MIT) is closer to SF Symbols in stroke and stays an open issue. |
| D4 | One add surface, the Add popover. Right-click, double-click, or N on empty canvas opens it, and so do the sidebar's Add Node button, a wire dropped on empty canvas, and Insert Node on a connection. | n8n and Unreal both send every entry point to one searchable picker. | A picker per entry point. |
| D5 | The palette is the Library section of the inspector shown when nothing is selected, with Place, Derive, Edit, and Reload. | Blueprint management needs a lasting place, and a popover closes. | Deleting the palette. |
| D6 | The inspector takes Godot's structure: a header, a filter, foldable sections, two-column rows, and revert arrows, on Apple surfaces. | The user named Godot's inspector. | |
| D7 | Generate Workflow places a built-in Plan in Chat mode with the description as its goal, runs it, and lands the result as the usual proposal. Nothing joins the workflow without Accept. | The [node model record](2026-10-04-node-model.md#planning-produces-graph-edits-the-person-approves) gives this button a Plan node in Chat mode, and the product direction requires review before agents edit code. | A separate generator outside the proposal path. |
| D8 | `Proposal.Accept` takes an optional fallback agent. The Proposal section shows "Fall back to the planner's agent", on only for a planner that Generate placed in this window. While it is on, a new node that has no usable agent of its own takes the planner's. | Without it, a generated node with no usable choice reads "Choose an agent". The user confirmed the box on 2026-10-06. | Always falling back, which overrides a blueprint's agent. Never falling back. |
| D9 | `WorkflowDocument` keeps undo and redo. Each applied edit is one step, and typing in one field or title folds into one step. A save ends a run of typing, and undoing back to the saved workflow reads as saved. | Context-menu Delete, Replace With, and Insert Node are fast and destructive. The workflow is immutable, so the history is a stack of references. | |
| D10 | The card is 240 by 64. The kind lives in a 32 px tile, and the state lives in the ring, a tint, a glyph, and the subtitle lines. The field preview, the review summary, and the full start problem live in the tooltip and the inspector. | The user asked for less text and more color. | |
| D11 | A connection takes the stroke of its source node's kind. A dependency is solid, and a context connection is dashed. Selecting nodes dims the wires that do not touch them. | PlanWeave colors edges by source, which shows what flows where. | One blue for every dependency. |
| D12 | A placed node is titled "New task". | It is a task in the product's words, and the tile already names the kind. | Naming it after its kind. |
| D13 | A planner with an open proposal takes the Proposing role: an accent ring, the sparkle glyph, and "Proposal · N tasks", with no waiting pulse. A waiting Chat planner offers Accept and Finish. | Chat mode ends every turn waiting, so a generated planner would otherwise pulse as waiting while its proposal is the thing to answer. | |
| D14 | Review is mint, a read-only agent has a gray tile, and no kind takes teal. | Pink sits 7.3 to 7.9 CIEDE2000 from the Failed red, below the roughly 10 at which people tell colors apart at a glance, so a Review in pink read as failed. Teal sits 8.7 from cyan. The owner chose pink for the Merge node on 2026-10-19 ([ADR 0033](../adr/0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)). | Pink for Review, teal for read-only agents. |

The kinds and their hues are Implement (indigo, Code glyph), Plan (cyan, Task List), Architect (purple, Ruler), Review (mint, Glasses), Approval (brown, Person Available), Merge (pink), and Read-only agent (gray, Document Search). `NodeKinds.Of` takes a blueprint's kind from its built-in id, then from its `DerivedFrom` chain, then from its work. Blue, green, red, orange, and yellow belong to the accent and the states, so no kind borrows a state's meaning.

The build settled these further points:

- Canvas keys live in a handled `KeyDown` in the canvas view, because NodifyAvalonia's editor gestures never fire. N opens the popover on key release, because on key press its letter reached the search box. Alt+click on a port uses Nodify's own connector gesture, which does fire.
- The undo keys run in the window's `KeyDown`, after the focused control, so a focused text box keeps its own Ctrl+Z.
- The Add popover lives in the window's overlay layer, not a native popup, so UI Automation finds its rows under the main window.
- Context menus have no shadow. A context menu is its own popup window, and a bare X11 server paints a shadow's margin as an opaque band.
- The Generate sheet is a window-level region above the column splitters, so its scrim covers Save and the splitters.
- Tests check a running or waiting ring by its brush key or style class, because the pulse makes its pixels depend on time.
- The dark Plan and Review tiles are `#1894C2` and `#009B89`. Apple's dark cyan and mint are too light to carry a white glyph, so the generator mixes each light tile toward Apple's dark hue, in linear light, until it is as bright as the other dark tiles. Both keep the cyan and mint hues, and the white glyph on them measures 3.47:1, above the 3:1 that the token check requires.

Left out on purpose, because each needs a product decision or a format change: disabling a node, pinned data, sticky notes, groups, subflows, reroute knots, copy and paste, favorites, recents, and inspector history.

## Open issues

1. Phosphor (MIT) is closer to SF Symbols in stroke than Fluent. Switching is a manifest and generator change.
2. Generate's button keeps its tooltip while it reads "Planning…" or "Review Proposal". Focus does not return to the canvas when the sheet closes. If the planner's view model is recreated, for example by Delete and then Undo, the button stops following it.
3. A window manager that moves windows on Alt+drag takes Alt+click on a port before the app sees it. The node menu's Disconnect still works.
4. The undo history has no limit.
5. The Windows real-window steps, and the macOS key hints, which Avalonia's native formatter writes, have not been run.
