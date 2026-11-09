# The inspector leads with what a node gave, and the left area lists the shown workflow's nodes

An inspector field is a quiet property that reads as text at rest, a node with a result shows its Output first, and the left area lists the nodes of the shown workflow and marks their dependency order. The coordinator of the UI review set these decisions on 2026-11-06, and they change parts of [ADR 0044](0044-the-inspector-keeps-one-right-edge-and-a-value-beside-its-label-at-every-width.md), [ADR 0045](0045-wires-curve-and-show-execution-and-a-node-kind-stays-quiet.md), [ADR 0046](0046-the-left-area-reads-in-one-line-a-person-opens-what-they-need-and-the-inspector-holds-a-nodes-facts.md), and [ADR 0048](0048-rows-and-lines-rest-whole-at-a-scroll-edge-and-a-long-code-line-shows-it-goes-on.md). Names in the left area still wrap in full (issue #108), and the app shows no durations. This record changes only what the app shows: workflow files and the engine stay the same.

Round 4 of the review found that a completed node's result sat under its setup, that a long field stopped in the middle of a sentence above a More link, that a changed model had no way back, that a value without a choice looked like a broken control, and that the Activity list hid a question at the smallest window. It also found that the tab on a panel's edge lay on the scroll bar, that a pinned row could not fold, and that the node list did not show the dependency order.

## How it works

- **Section order.** A node without a result shows Task, Agent, Input, Output. A node with a result shows Output, Input, Task, Agent. Tab follows the order that shows.
- **Result in the conversation.** While the node's conversation is open, its result row keeps only the heading, because the conversation holds the summary.
- **Header.** The status and the kind share the line under the title. The blueprint's version moves to the help glyph's tooltip.
- **Labels and arrows.** A property's label starts at the content edge with no glyph. A revert arrow ends the label's column, so all values keep one left edge and one right edge. A row with a tile, such as a linked node, keeps its tile.
- **Revert.** Every row that the count includes has an arrow. The arrow goes back to the blueprint's default, or to the saved value where no default applies.
- **Fields.** At rest a field is a quiet fill with no outline. A long field reads as text with no Markdown marks in up to five lines, with an ellipsis. A click shows the source, and the More link is gone.
- **Value without a choice.** It is plain text with an info glyph and the reason in its tooltip.
- **Prompt.** It is a disclosure, not a picker.
- **Bottom edge.** A short row that the list's bottom edge would cut hides under a cover until it comes in whole. This replaces the fade.
- **No project.** The inspector and its tab hide until a project opens, and the left area offers Open folder.
- **Overview.** A state's row selects the first node in that state.
- **Activity.** A row's second line holds the reason and the workflow in one line, cut at its end. The tooltip holds the project. The list shows four rows: the first row of each need, and always the first failure. All window sizes use this list.
- **Tree.** Project rows stand 8 px apart. A workflow's name has a plain weight until it is shown. A completed workflow's ring takes the complete color. Every node row has a state glyph, also for Idle.
- **Shown workflow.** A workflow lists its nodes when it is shown, and the list closes when another workflow takes its place. A workflow of more than 12 nodes stays closed, so the other projects stay in view. This replaces "a person opens what they need" for the shown workflow.
- **Step numbers.** A node's row shows its step: 1 with no dependency, otherwise one after the last node it depends on. Nodes that can run side by side share a number.
- **Running workflow.** Its row shows what the first running node does now under the name.
- **Pinned rows.** A pinned project and a pinned workflow have a chevron that folds them.
- **Panel tabs.** A tab sits wholly on the canvas's side of the seam, so a panel's scroll bar never runs under it.
- **Find.** The results float at 440 px over the tree and the canvas. This replaces the width of the box in ADR 0048.
- **A hyphenated name.** A token such as 2023-Q4 stays on one line where a name wraps.
- **Header actions.** The Projects heading holds only Open folder. New workflow stays on a project's row.

## Considered options

- **Rendering full Markdown at rest.** A clamp then cuts a block in the middle, so a field reads as plain text.
- **Folding Task and Agent on a completed node.** It hides the fields that a person changes before a retry.
- **Sorting the node list by step.** It mixes the branches of a graph, so the list keeps its reading order and shows the number.
- **A last-activity time on a workflow's row.** The log has no time data.
- **Capping a name at two lines.** The owner asked for every name in full.

## Consequences

- A very long node name still takes up to four lines in the left area.
- Step numbers do not always rise down the list, because the list follows each branch.
- Reading text loses inline code's font until the field has focus.
- The Activity list is one row longer than before at small windows.
