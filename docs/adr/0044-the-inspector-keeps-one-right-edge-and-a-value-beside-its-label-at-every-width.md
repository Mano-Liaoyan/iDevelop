# The inspector keeps one right edge and a value beside its label at every width

[ADR 0045](0045-wires-curve-and-show-execution-and-a-node-kind-stays-quiet.md) supersedes its section order, its header line, a stacked row's arrow, and its rule for long values.

The inspector has one right edge, and a labelled value sits beside its label at every panel width. Output follows Input. This record supersedes [ADR 0043](0043-the-inspector-pins-a-nodes-state-and-stacks-values-below-300-px.md), and its pinned header, its sections, and its Tab order stay.

Round 1 of the UI review found that filter, fields, result card, and section band ended at four different edges, that the 280 px minimum nearly doubled the Agent section's height, that a node's result sat under every setting, and that a count of changes could name no row.

## How it works

- **Columns.** The columns are a 14 px glyph, a 6 px gap, a 76 px label, the value, and a slot for what a row adds after its value. Boxes, paragraphs, and buttons end 12 px from the panel's edge. A section's band reaches 4 px further.
- **Revert arrows.** A changed value's arrow takes the place of the row's glyph. Over a stacked value it ends the label's line. The Agent section's count is the number of rows that show an arrow, so a change with no default to go back to does not count.
- **Header.** The status pill and the actions share one line when they fit. A title that does not fit two lines ends in an ellipsis, and its tooltip holds it whole. State detail keeps to two lines and its More link. While the conversation is open, its action is the current one and not primary, and a waiting node's detail hides, because the conversation asks it in full. A failure's reason always shows.
- **Waiting nodes.** Retry leaves the row of a node that waits for a reply or a question, and stays in More.
- **Sections.** The order is Input, Output, Task, Agent. The Agent section's conversation field is named Mode, and a read-only System prompt row shows the blueprint's template. With nothing selected, the overview counts states, and the blueprint library lists the kinds.
- **Long values.** A long text field fits its text from 52 px to 124 px, scrolls inside, and grows to 232 px with the focus.

## Considered options

- **Keeping two right edges.** A reserved slot gives each arrow its own column, but every box then ends 24 px early, also with no arrow in the panel.
- **Stacking at the 280 px minimum.** It gives a long model name the panel's width, but the Agent section nearly doubles in height.
- **Keeping Output last.** It follows a node's model from input to output, but reading a result, a main task, then needs a scroll past every setting.

## Consequences

- A changed row loses its glyph while the arrow shows.
- A workflow's kind counts are gone from the overview.
- The filter still moves when the selection changes, because the header's height depends on the node's state.
