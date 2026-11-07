# The canvas has no name tag, and a large tree group stays folded

The canvas no longer shows a name tag for the selected card at any tier. A tree group with more than 8 nodes stays folded when the canvas selects a node in it. A Label tier card fits its whole model name. A folded workflow row in the tree shows a badge and a count for each urgent state. This amends ADR 0057, ADR 0063, ADR 0064, and ADR 0065.

Round 19 of the review found that the name tag covered its own card and the title of the next card, that the "Done" group of 21 nodes filled the left tree when its node was selected, that cards at the default zoom cut model names such as "GPT-6.1 Sol" and "Sonnet 5.5", that the state words under folded workflow rows wrapped to two or three lines, that Run Workflow and Send got lighter under the pointer in the light theme, and that the inspector title took three lines.

## How it works

- **No name tag.** The canvas shows no tag with the selected card's title, at the Block, Title, and Label tiers. A cut title shows whole in the card's tooltip and in the inspector. The card's tooltip stands 8 px over the card, so it covers neither the card's text nor its selection ring. This withdraws ADR 0057's tag at the Block tier and ADR 0064's tag at the Label and Title tiers.
- **Large tree groups.** Selecting a node on the canvas opens its tree group only when the group holds 8 nodes or fewer. A larger group stays folded until the person opens it, and the tree then shows the selected node's row as selected. Showing a node from Activity or Find opens its group, because the person asked for its row. While the selected node has no row, the tree shows no selected row and the canvas keeps the selection. This amends ADR 0064's rule that selecting a node opens its group. [ADR 0067](0067-every-card-keeps-its-title-and-the-tree-shows-the-selected-nodes-row.md) withdraws this rule: a selection opens its group at any size.
- **Model names at the Label tier.** The client's letters take 24 canvas units and the model name 25, and the line ends 5 units before the state badge. Names such as "GPT-6.1 Sol", "Sonnet 5.5", and "Qwen 4 Max" fit whole. A model name that does not fit ends at a whole word and an ellipsis, as "GPT-6.1…". The Title tier keeps the cut by character, because there the first word often does not fit.
- **Workflow rows.** A folded workflow row shows, under its name, one badge and one count for each urgent state. The state words stay in Activity, in the chip's tooltip, and in its accessible name. This amends ADR 0063's rule that a row says its states in words.
- **Current workflow.** The 2 px accent line of the workflow that holds the selected node stands just before the workflow's name, after its chevron, and the name keeps its place.
- **Primary hover.** Run Workflow, the inspector's primary action, and Send darken their fill under the pointer in both themes, and their label stays white.
- **Inspector title.** The inspector's header shows the title in up to two lines. The tooltip holds the whole title.
- **Prose measure.** Conversation prose keeps a measure of 520 px, about 75 characters. Code and tables keep the column's width.

## Considered options

- **A name tag that always stands over the card.** In a dense grid the gap over a card is too small, and the tag covered the row above.
- **A wider card.** It fits every model name on one line, but cards at the 300-unit column step then touch (ADR 0064).
- **A compact list of the first done nodes.** It adds a control. A folded group keeps every node one click away.

## Consequences

- A selected node in a large folded group has no row in the tree until the person opens the group.
- At 61% on the 50-node workflow, a title that shares a long start with a sibling still shows its differing part, as in "…partition 2023-Q4" (ADR 0065).
