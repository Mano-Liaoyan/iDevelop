# A selected card shows its whole title, and the run bar names its states before a view setting

At the zoom where a fit rests, a selected card grows to show its whole title, and the cards under it move down. Wire lanes and port fans widen as much as the tier thickens the wires. The run bar moves Follow to its overflow menu before it folds a state into "+N more". Run Workflow says how many nodes it runs when that is not all of them. A shown workflow whose node is selected has no fill. This amends ADR 0051 and ADR 0058.

Round 13 of the review found that a selected card at a fit still ended its title in an ellipsis, that wires into and out of one port ran too close to tell apart at a fit, that the run bar hid the interrupted and stale counts behind "+3 more" while Follow kept its place, that Run Workflow did not say that it runs only part of the workflow, and that the shown workflow's quiet fill looked the same as a row under the pointer.

## How it works

- **Selected card.** At the Label tier, where every fit rests (ADR 0059), the selected card's title has no line limit. The card grows by the lines it needs, and the card layout of ADR 0024 moves the cards under it down, so it covers none of them. Its last line and its badge keep to its bottom. Other tiers keep their limits.
- **Header titles.** The inspector's and the conversation's title wraps to three lines beside its tile, then ends in an ellipsis. The tooltip holds the whole title.
- **Wires.** A lane is 22 canvas units at the Full tier. At other tiers, lanes and fans are larger by the tier's wire scale, so they keep their distance on screen as the wire draws thicker.
- **Run bar.** Its steps are: the view's controls to the overflow menu, Follow to the overflow menu, the less urgent groups into "+N more", only the most urgent group, and then Next as a glyph.
- **Run Workflow.** It reads "Run Workflow" when it runs every node or nothing, and "Run 1 of 8" when some nodes keep their result. The tooltip still says that those nodes keep it.
- **Left tree.** While a node of the shown workflow is selected, the workflow's row keeps its weight and its open chevron and has no fill. The node holds the one accent fill. A row under the pointer keeps its neutral fill.

## Considered options

- **Show the selected card's whole title beside the graph.** The inspector already shows it, and the card is where the person looks.
- **Let the selected card's title grow over the cards under it.** Nothing may cover other content.
- **A second row for the run bar.** It takes canvas height at every width to fix a narrow case.
- **A quieter accent fill for the shown workflow.** Two rows with accent fills compete for the selection.

## Consequences

- Selecting a card at a fit can move the cards under it. They go back when the selection moves.
- Follow is in the overflow menu on a narrower canvas than before.
