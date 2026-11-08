# A workflow row takes one status line, and a large window fits at the full card

Round 33's reviewers reported the same faults in the left area and the canvas. This record gives the changes.

## How it works

- **Workflow row.** The line under a workflow's name names its most urgent group and counts the nodes of the other groups, as "1 failed · 5 more". It stays on one line at every panel width and ends in an ellipsis if it must. The row's tooltip holds every group, and the run bar and Activity say them. This replaces the wrapped line of ADR 0053 and ADR 0058.
- **Activity.** While a node waits or failed, a long tree no longer folds Activity, so the Reply path starts from a visible list. A panel under 700 px still folds it.
- **Cut title.** A title cut on a card keeps its first word and the last words that differ, as "Create… partition 2024-Q3". A line too short for the first word keeps only what differs, as ADR 0065 says.
- **Fit.** A canvas from 1600 px wide fits a graph that can take it at 86%, which the bar calls 215%, and which is the Full tier's card. The zooms from 120% to 210% hold less text on a card, so a fit does not stop in them. A narrower canvas keeps the limit of ADR 0059.
- **Run bar.** Next keeps "Next waiting" until the bar's chips and zoom controls have given up their room. The word goes last.
- **Group labels.** A Nodes group's label takes the regular weight, one level under a section's semibold label.
- **Interrupted node.** The inspector's action row keeps Conversation in this state too, first, as in every other state.

## Considered options

- **State chips with icons on the workflow row.** Six chips do not fit one line at 272 px, and the counts already show twice on screen.
- **A fit limit that grows with the window.** Zooms above 120% and under 210% change the card to the Compact and Detail forms, whose text is smaller than the Label form's.

## Consequences

- A workflow row no longer lists each state. The person reads them in the tooltip, the run bar, or Activity.
- At 272 px wide and a tall window, Activity takes three rows of the room that the tree had.
