# The inspector

The inspector edits what is selected. With nothing selected, it shows the workflow's kinds and the blueprint library. Every view shares one set of columns. A row's icon or kind tile comes first, then its label, its value, and its revert, info, or library buttons or its count. The buttons and the glyphs end 12 px from the panel's right edge, the overview's counts end where the glyphs' drawing ends, and the boxes and paragraphs end 64 px from it, in both themes and at every width. The label column keeps one width, so values start at one place at every width, and every row centres on a 28 px first line. Text wraps rather than ending in an ellipsis. Under 440 px, which includes the 320 px default, every value moves below its label, so the longest model names fit their pickers.

## Sub-features

- `inspector-empty` shows the project's header, the `Overview` counts, and the `Library` with `PlaceBlueprint` and `BlueprintMore` on each blueprint.
- `inspector-node` shows the selected task's header and the `SectionTask`, `SectionAgent`, `SectionRun`, `SectionConversation`, and `SectionBlueprint` sections. Each labelled row leads with an icon or a kind tile.
- `inspector-connection` shows a connection's `From`, `To`, and `Kind`. Only a canvas click selects a connection, so `The_workflow_the_task_and_the_connection_views_share_two_right_edges` covers it headlessly.
- `inspector-edges` ends the header's `InspectorMore`, the filter's `InspectorTools`, every row's trailing buttons, and rows of buttons at one right edge, and the filter box, pickers, text boxes, and paragraphs at a second. Each kind's `KindCount` ends 6 px before the first edge, where the glyphs' drawing ends.
- `inspector-filter` keeps the rows whose label matches the text in `InspectorFilter`, and their sections.
- `inspector-folds` folds every section from `CollapseAllSections` and opens them from `ExpandAllSections`. A folded section counts its changed values in its header.
- `inspector-reset` shows `Revert<Field>` only while a field differs from its blueprint's default.
- `inspector-library` keeps Derive and Edit in the blueprint's More menu as `DeriveBlueprint` and `EditBlueprint`. Built-in blueprints offer only Derive.
- `inspector-narrow` stacks every labelled value under its label in an inspector narrower than 440 px, the 320 px default included. Only a splitter drag changes the inspector's width, so `A_picker_sits_under_its_label_only_in_an_inspector_narrower_than_440`, `Under_440_every_labelled_value_sits_under_its_label_from_the_content_edge_to_the_value_edge`, and `A_panel_widened_again_puts_its_values_back_beside_their_labels` cover it headlessly.

## How to get to it (user POV)

- Open a project with nothing selected.
- Choose a task in the sidebar or on the canvas, or click a connection on the canvas.
- Type in `Filter properties`, or choose `Expand All` or `Collapse All` from the button after it.
- Choose a section's header to fold or open it.
- Choose `+` or `…` beside a blueprint in the `Library`.
- Drag the splitter before the inspector to widen or narrow it, between 280 and 520 px.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, with nothing selected.
- The light theme, chosen with `Select-Element (Find-ById $s.Window 'ThemeLight')`.
- Run each bullet in one call, so its variables stay set. Define the edge helper first in each call with `function Right($id) { (Find-ById $s.Window $id).Current.BoundingRectangle.Right }`.

- **Empty selection.** Run `Assert-Step $s ($null -ne (Find-ById $s.Window 'SectionLibrary')) 'the inspector shows the library'`, `Assert-Step $s ($null -ne (Find-ById $s.Window 'WorkflowKinds')) 'the overview counts the kinds'`, `Assert-Step $s ((Right 'PlaceBlueprint') -lt (Right 'BlueprintMore') -and [math]::Abs((Right 'BlueprintMore') - (Right 'InspectorTools')) -lt 1) 'More ends at the same edge as the filter button'`, and `Assert-Step $s ((Right 'KindCount') -lt (Right 'BlueprintMore')) 'the counts end inside the library buttons' edge, where the glyphs' drawing ends'`. Run `Save-Evidence $s 'inspector-empty-light'`. The screenshot shows the counts' digits ending where the drawn `…` and filter glyphs end, the filter box ending at the value edge, and the kinds and the blueprints at one row height.
- **Node.** Run `Select-Element (Get-SidebarTasks $s.Window)[1]`. At the 320 px default the pickers sit under their labels. Then run `Assert-Step $s ([math]::Abs((Right 'TaskClient') - (Right 'InspectorFilter')) -lt 1) 'the client picker ends where the filter box ends'`, `Assert-Step $s ([math]::Abs((Right 'TaskReasoning') - (Right 'TaskInstructions')) -lt 1) 'the pickers and the text boxes share the value edge'`, and `Assert-Step $s ([math]::Abs((Right 'InspectorMore') - (Right 'InspectorTools')) -lt 1) 'More and the filter button share the action edge'`. Run `Save-Evidence $s 'inspector-node-light'`.
- **Reset.** The sample's instructions differ from the blueprint's empty default. Run `$was = Get-Value (Find-ById $s.Window 'TaskInstructions')`, `Assert-Step $s ($null -ne (Find-ById $s.Window 'RevertInstructions' 2)) 'a changed field offers Revert'`, and `Invoke-Element (Find-ById $s.Window 'RevertInstructions')`. Run `Assert-Step $s ((Get-Value (Find-ById $s.Window 'TaskInstructions')) -eq '') 'Revert puts the blueprint default back'` and `Assert-Step $s ($null -eq (Find-ById $s.Window 'RevertInstructions' 1)) 'Revert hides once the field is at its default'`. Then run `Set-Text (Find-ById $s.Window 'TaskInstructions') $was`.
- **Filter.** Run `Set-Text (Find-ById $s.Window 'InspectorFilter') 'mod'`. Run `Assert-Step $s ($null -ne (Find-ById $s.Window 'TaskModel' 2)) 'the filter keeps Model'` and `Assert-Step $s ($null -eq (Find-ById $s.Window 'TaskClient' 1)) 'the filter hides Client'`. Run `Save-Evidence $s 'inspector-filter-light'` and `Set-Text (Find-ById $s.Window 'InspectorFilter') ''`.
- **Folds.** Run `Invoke-Element (Find-ById $s.Window 'InspectorTools')` and `Invoke-Element (Find-InProcessWindows $s.Process 'CollapseAllSections')`. Run `Assert-Step $s ($null -eq (Find-ById $s.Window 'TaskClient' 1)) 'Collapse All hides the rows'` and `Save-Evidence $s 'inspector-folds-light'`. The screenshot shows each section's chevron pointing to the leading edge. Then run `Invoke-Element (Find-ById $s.Window 'InspectorTools')`, `Invoke-Element (Find-InProcessWindows $s.Process 'ExpandAllSections')`, and `Assert-Step $s ($null -ne (Find-ById $s.Window 'TaskClient' 2)) 'Expand All shows the rows again'`.
- **Library menu.** Clear the selection by restarting the session with `Stop-IDevelop` and `$s = Start-IDevelop`. Run `Invoke-Element (Find-ById $s.Window 'BlueprintMore')` and `Invoke-Element (Find-InProcessWindows $s.Process 'DeriveBlueprint')`. Run `Assert-Step $s ($null -ne (Find-ById $s.Window 'BlueprintEditor')) 'Derive opens the blueprint editor'` and `Invoke-Element (Find-ById $s.Window 'CancelBlueprint')`.
- **Dark.** Restart the session with `Stop-IDevelop` and `$s = Start-IDevelop`, so nothing is selected. Run `Select-Element (Find-ById $s.Window 'ThemeDark')`, then repeat the empty selection and node bullets with `-dark` in place of `-light` in their evidence names.

## Gotchas

- UI Automation reports bounds in physical pixels, so the bullets compare two controls' edges rather than an inset in px. At 125% scaling the 12 px and 64 px insets read as 15 and 80.
- UI Automation cannot drag the splitter or click a connection, so the narrow layout and the connection view need the named headless tests or a witnessed manual step.
- A hidden row leaves the UI Automation tree, so `Find-ById` with a short timeout returning `$null` proves the filter or a fold hid it.
- The More menu opens as its own window. Find its entries with `Find-InProcessWindows`, not `Find-ById $s.Window`.
- `InspectorLayoutTests` and `InspectorTests` cover the edges, the counts' ink, the label column, the row line, the header height, the narrow layout, wrapping instead of ellipses, the longest model name of each client, the scroll bar, the filter, the folds, and the reverts headlessly on every platform. The approval, recovery, updated inputs, and proposal tests also check their views at 280, 320, and 520 px.
