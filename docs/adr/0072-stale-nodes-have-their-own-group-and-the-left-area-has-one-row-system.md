# Stale nodes have their own group, and the left area has one row system

The Nodes section has a Stale group, after Running and before Not started. Its name takes the canvas's stale color. A workflow with no node shows one line of help in the section. The rows of the left area have one height and one text size. Every fold chevron leads its row. The left area shows one selection fill for a node. This amends the group list of ADR 0071 and the failed node's header action of ADR 0070.

Round 27 found that a stale node stood under "Not started" while the canvas and the run bar named it stale, that an empty workflow's Nodes section was blank, that workflow rows and node rows used two row systems, that the panel had three chevron treatments, that a line showed under the pinned project only, and that one waiting node had a blue fill in Activity, on its workflow row, and in Nodes.

## How it works

- **Stale group.** `NodeGroupKind.Stale` holds each stale node. The order is Needs you, Running, Stale, Not started, Done. The header's name uses the warning color that a stale card's edge and badge use.
- **Empty workflow.** Under the Nodes heading, one line says "No nodes yet. Press N to add one." The Projects section keeps its share of ADR 0071.
- **Rows.** Project, workflow, node, and group header rows are 26 px high for one line. Workflow and node names are 13 px. Names of projects, workflows, and nodes start at one x position, and a child row no longer starts before its parent's name. A name still shows in full and wraps (issue #108).
- **Chevrons.** Project and group chevrons are the one 12 px glyph, and each leads its row at the panel's content edge. Activity's chevron ends its heading row, so the three section labels share one left edge (ADR 0071).
- **Pinned project.** The pinned project row has no line under it. Rows rest on a whole row under it, so it needs no line.
- **Activity row.** An Activity row has a hover fill and no selection fill. The Nodes section shows the selected node.
- **Nodes edges.** A row that the top edge of the Nodes list would cut hides under the panel's color, as a row at the bottom edge does.
- **Whole rows.** Projects ends on a whole row when it scrolls, and the room left over goes to Nodes. The Nodes list rests on a row's top, as the Projects tree does, so neither section shows a blank band at its edge.
- **Inspector chevrons.** An inspector section's chevron leads its title, as every other fold chevron does.
- **Failed node.** While the conversation shows a failed node's card, the card holds the primary Retry. The inspector header keeps Retry as a quiet action.

## Considered options

- **Put stale nodes under Needs you.** A stale node waits for nobody's word, and Needs you means that the person must act before the run can go on.
- **A sticky group header in Nodes.** It adds an element that covers the list, so it stays open.

## Consequences

- A workflow can show five group headers.
- At the 240 px panel width node names have less room and wrap sooner.
