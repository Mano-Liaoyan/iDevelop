# Every card keeps its title, and the tree shows the selected node's row

A card shows its title at every zoom tier. The Block tier no longer turns a card into a state block without a name. The left tree opens the group of the selected or open node at any size. The inspector title takes up to four lines, and a short inspector keeps one short line that says what Continue and Retry do. This amends ADR 0045, ADR 0047, ADR 0065, and ADR 0066. ADR 0068 amends its tree reveal for a large group and its Title tier lines.

Ten review rounds found that cards at the Block tier, such as the docked conversation's canvas at 38%, were unlabeled state blocks, and that titles at 61% were cut. Round 20 also found that the open node had no row in the tree when its group was large, that group headers and node rows stood at one indent, that the inspector title cut its distinguishing end, and that the interrupted header at the minimum window did not say what Continue and Retry do.

## How it works

- **Title floor.** A Title or Block tier card's text is never under 9 px on screen. Where the zoom would make it smaller, the tier makes the text larger in canvas units and gives up other card parts first: the agent's line, then the badge. The Title tier keeps its agent's line and badge, and its floor is now 9 px, so a title at 61% keeps more words.
- **Block tier.** Under a zoom of 0.23 a card keeps its kind stripe and its title in one or two lines at 9 px. It has no agent's line and no badge. A band in the state's fill along the card's bottom edge says the state. The card draws 158 canvas units high, as a Title tier card does, past its 64 px layout box, and the wires go around it. The title keeps two lines down to a zoom of 0.14 and one line under it. A title that shares a long start with a sibling shows its differing end first, as in "…partition 2024-Q3" (ADR 0065). The docked conversation's canvas uses the same tiers.
- **Tree reveal.** Selecting a node, on the canvas or by opening its conversation, opens its tree group at any size, and the tree shows its row as selected. The group opens once for each selection, so a group that the person folds stays folded until the selection changes or the node moves to another group. This withdraws ADR 0066's rule that a group of more than 8 nodes stays folded for a canvas selection. A group still starts folded above 8 nodes (ADR 0065).
- **Tree levels.** A group header stands one level before its node rows: a node's tile starts under the group's name.
- **Inspector title.** The header shows the title in up to four lines, then an ellipsis. A short inspector shows two. The tooltip holds the whole title. This amends ADR 0066's two lines.
- **Short inspector.** Under 700 px of inspector height, an interrupted node's header keeps one muted line, "Continue resumes. Retry starts again.". Its tooltip holds the whole sentence. This amends ADR 0065's rule that the state's sentence moves to the tooltip.
- **Run bar.** Before a chip gives up its words, Next says only "Next". Its tooltip and accessible name keep what it selects. This amends ADR 0065's order of steps.

## Considered options

- **A state fill with the title on it at the Block tier.** Dark text on the failed and running fills does not keep its contrast.
- **The badge on the card's corner at the Block tier.** It stands over the card above it in a dense graph.
- **Three title lines at the Title tier.** The agent's line still fits there, and a node is an agent wherever its words read.

## Consequences

- At the Block tier a title has room for about six characters a line. The inspector, the tooltip, and the tree show the whole title.
- Selecting a node in a large group lengthens the tree. The person folds the group again with one click.
