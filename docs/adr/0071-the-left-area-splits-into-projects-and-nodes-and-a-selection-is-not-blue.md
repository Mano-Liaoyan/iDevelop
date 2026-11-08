# The left area splits into Projects and Nodes, and a selection is not blue

The left area has two sections, as VS Code's Explorer and Outline do. Projects lists the projects and their workflows. Nodes lists the shown workflow's nodes by state. An overview card keeps room for its title's descenders over its chip row and never grows into the card under it. A selected card and its wires take a strong neutral color, so blue means a run only. The inspector title shows in full. This supersedes the tree's node rows of ADR 0063, ADR 0064, ADR 0066, ADR 0067, ADR 0068, ADR 0069, and ADR 0070, and amends ADR 0068's Title tier lines.

Round 24 found that the chip row cut the last title line of overview cards at 61%, so "2024-Q2" read as "2024-O2", that the selection ring and a running node's wire had one blue, that the inspector title cut its distinguishing end, and that a long Done group filled the left tree and pushed the other projects out of view. Round 17 found the opposite fault, a tree that hid nodes, so a section of its own for the nodes answers both.

## How it works

- **Projects.** The section lists each project and its workflows, as before, with no node rows. A workflow row keeps its full name (issue #108), its most urgent state chip, and its progress. A workflow row does not fold, and the session file no longer keeps a list of expanded workflows. The section takes its rows' height up to half of the room under Activity, and it scrolls past that. The project whose rows meet the section's top edge stays pinned there.
- **Nodes.** Under a rule and the heading "Nodes", the section lists the shown workflow's nodes under the state groups of ADR 0064: Needs you, Running, Not started, and Done. Every group starts open. A header folds its group, and a selection in a folded group opens it. Names show in full and wrap. The section has its own scroll and takes the rest of the room, at least 120 px. It scrolls the selected node's row into view. With no workflow shown, Projects takes all the room.
- **Keys.** Up and Down go through Projects and then Nodes. Left on a node or a group header goes to the shown workflow's row.
- **Overview card.** A Title tier card's title draws its descenders past its tight lines. Under the last title line the card keeps 0.45 of the text size, about 4 px on screen, then the 36-unit chip row and 5 units under it. The card takes the most title lines, up to three, whose height leaves a 6 px gap to the card under it at the workflow's least row distance. At the fixture's rows, 190 canvas units apart, a card takes two lines at 61% and three lines at 69% and above. The unique-ending rule of ADR 0069 picks the words that show.
- **Selection.** A selected card's ring and its highlighted wires use the strong text color, dark in the light theme and light in the dark theme. Blue marks a running node and a wire that a node reads now.
- **Inspector title.** The header shows the whole title and wraps it without a line limit. This replaces ADR 0070's two lines.

## Considered options

- **A splitter between the sections.** A fixed split at half needs no new control and no saved width. A splitter stays open for a later round.
- **Clamp every overview title to two lines.** The 69% and larger overview zooms have room for three lines, and the third line keeps more titles whole.
- **Keep the selection blue and mark a run another way.** Blue already means running on cards, wires, and the run bar, so the selection gives way.

## Consequences

- A workflow with many nodes no longer pushes the other projects out of view, and every node is one scroll away in its own section.
- At 61% more overview titles end in an ellipsis than before, and the unique-ending rule keeps them apart.
- The Nodes section shows the nodes of one workflow. To see another workflow's nodes, the person shows that workflow.
