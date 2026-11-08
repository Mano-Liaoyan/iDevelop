# A zoomed out card grows to three title lines, and a large group lists only the selected row

A Title tier card takes up to three title lines at the 9 px floor and grows in height to hold them. A large tree group stays folded when a node in it is selected or opened, and lists only that node's row and a "+N more" row. A node's status uses the tree's state words, and a few rows and headers keep to one line. This amends ADR 0060, ADR 0066, and ADR 0067.

ADR 0070 withdraws the "+N more" row and the Title tier card's model text.

ADR 0071 replaces the Title tier card's line rule: the title keeps room for its descenders over the chip row, and the card takes fewer lines where the row gap is short. ADR 0071 also supersedes the large tree group's fold.

Round 21 found that titles at 61% were cut to similar starts, that the eight-node workflow beside the widest inspector cut its titles at 69%, and that a selection in a large Done group filled the left tree and pushed the other workflows and projects out of view.

## How it works

- **Three title lines.** A Title tier card (zoom 0.23 to 0.36) shows up to three title lines over its agent's line. The text size steps by one canvas unit and keeps 9 px on screen. The lines stand 1.04 of the text size apart, the title starts 2 canvas units under the card's top, the agent's line follows the last title line, and 5 canvas units under it hold its descenders.
- **The card grows.** The card's height holds the title lines and the agent's line, and it is never shorter than the tier's 158 canvas units. The card takes three lines only where the card under it, at the least row distance in the workflow, keeps a 6 px gap on screen. Otherwise it takes the most lines that keep that gap. The wires and the fit use the card's drawn height.
- **Text margins.** The title and the agent's line start 14 canvas units past the kind stripe, as at the Block tier, so a line holds more words.
- **A large group in the tree.** When the selected or open node is in a folded group of more than 8 nodes, the group stays folded. Under its header the tree lists that node's row and then a "+N more" row, such as "+20 more". The "+N more" row, a click on the header, Enter, Space, or Right opens the group. A group of 8 nodes or fewer still opens for a selection, once for each selection (ADR 0067).

- **One state word.** The inspector, the card, and the tree say "Not started" for an idle node and "Done" for a completed node, the names of the tree's groups. Other states keep their words.
- **Conversation header.** The conversation's header shows the title on one line with an ellipsis. Its tooltip and the inspector hold the whole title.
- **Workflow row.** The 2 px accent line of the workflow whose node is selected stands at the row's left edge, before the chevron, and not next to the name. A folded workflow's state counts stand after its name on the name's line when the whole name fits beside them, and under the name otherwise.
- **Breadcrumb.** The project's name, at most 120 px, keeps its width, and the workflow's name gives up width first.
- **Blueprint editing.** While the inspector edits a blueprint, Add and Generate are off, because they change the workflow under the editor.

## Considered options

- **Hide the agent's line for a third title line.** A node is an agent wherever its words read (ADR 0067).
- **Wider cards.** The column gutters hold the wires, and they are already narrow where a node fans out.
- **Open the large group fully.** That is ADR 0067's rule, and it pushes the rest of the tree out of view.

## Consequences

- In the fixture's 50-node workflow, 45 of 50 titles show in full at 61% and 46 at 69%. The 8-node workflow shows all 8 titles in full at both zooms.
- At the Block tier the fixture's rows leave no room for a third line, so Block tier titles keep one or two lines.
- A folded group's one row can be the selected row, so the list shows the selection without the group's other rows.
