# The left tree follows the window, and an inspector section keeps its place

The left tree opens to what the window shows, and only one workflow lists its nodes. The inspector keeps one section order, and a long field opens in place. The coordinator of the UI review set these decisions on 2026-11-06, and they change parts of [ADR 0046](0046-the-left-area-reads-in-one-line-a-person-opens-what-they-need-and-the-inspector-holds-a-nodes-facts.md), [ADR 0048](0048-rows-and-lines-rest-whole-at-a-scroll-edge-and-a-long-code-line-shows-it-goes-on.md), and [ADR 0050](0050-the-inspector-leads-with-what-a-node-gave-and-the-left-area-shows-the-shown-workflows-nodes.md). This record changes only what the app shows: workflow files and the engine stay the same.

Round 5 of the review found that the shown workflow could sit collapsed or below the fold, that two workflows looked active, that one long name took four rows, that a blank band sat under the pinned rows, and that Activity took half of a small window. It also found step numbers that read as a broken order, a task field that ended in an ellipsis with no way to read the rest, sections that moved with state, and an overview whose counts did not match the run bar.

## How it works

- **Reveal.** When a workflow shows, the tree opens its project and its list, folds every other workflow, and scrolls to its row. A node chosen from Activity, Find, the keys, or the canvas gets the same treatment. The tree stays still while the row is in view, so a click never moves the row under the pointer. A long workflow also opens. This replaces the 12-node limit.
- **Node names.** A node's row takes two lines with an ellipsis, and the tooltip and the selected row hold the whole name. This changes issue #108's rule for node rows only. Workflow and project rows keep full names.
- **Step numbers.** They are gone from the tree. The tree keeps its reading order.
- **A workflow's row.** The completion count ends the first line in every state. A line under the name says what failed, waits, runs, and is stale, in a glyph and words. The ring is gone.
- **Selection.** The shown workflow's row is filled. While a node of it is selected, the node holds the selected fill and the workflow a quiet fill. The edge bar is gone.
- **State glyphs.** Approval and Interrupted use the glyphs and colors of a card's badge on every row.
- **Activity.** The heading counts what runs too, so the counts add up to the list. Below a 700 px panel the list starts folded to the heading, and the heading opens it. A row's reason and workflow have a dot between them.
- **Pinned rows.** A row slides under the pinned rows, flush with their edge, so the top cover is gone. The pinned workflow keeps its completion count.
- **Find.** The results take the panel under the box, down to the footer. This replaces the 440 px float.
- **No project.** The canvas holds Open folder, and the left area holds only the recent folders. The Projects heading holds Open folder as a glyph.
- **Footer.** One dot shows the worst agent status, and the flyout names each client.
- **Section order.** Output, Input, Task, Agent for every node and state. A Proposal stays first. This replaces the two orders of ADR 0050.
- **Go to.** The control beside the filter goes to a section and still folds them all, which gives direct access to Agent.
- **Long fields.** A field reads in up to three lines. More opens the whole reading in place, and Less closes it. This replaces "the More link is gone".
- **Result row.** It always holds the summary. While the conversation is open, the summary keeps to one line.
- **Revert.** The arrow has one column on every row, just before a box's chevron, and its tooltip names what it restores.
- **Overview.** Its rows use the run bar's words, with one row for each reason to wait, so Interrupted is not counted as Waiting.
- **Status.** A waiting node's status reads "Input required · Reply", in sentence case.
- **Header actions.** Conversation comes first in every state.
- **Focus.** The ring is 2 px on the control's own edge, and a text box shows the same 2 px.
- **Closed panels.** A closed panel's tab names the panel.

## Considered options

- **One-line node rows.** It gives the steadiest rows, but a similar pair of names would need a hover to tell apart.
- **Hiding the result row while the conversation is open.** It repeats less, but the row was then a box with a heading only.
- **Keeping the output-first order only for nodes with a result.** The sections then move under the person between nodes.
- **Removing the step numbers' cause by sorting.** It mixes the branches of a graph.

## Consequences

- A node's Task section starts lower than before for a node with no result.
- A workflow's row can take a second or third line while nodes need attention, so rows differ in height.
- Find no longer widens past the left area, so a long name wraps.
- A row can show cut at the pinned rows' edge while it slides under them.
