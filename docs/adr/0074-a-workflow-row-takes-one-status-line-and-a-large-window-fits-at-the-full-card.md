# A workflow row takes one status line, and a large window fits at the full card

Round 33's reviewers reported the same faults in the left area and the canvas. This record gives the changes.

## How it works

- **Workflow row.** The line under a workflow's name names each group that has nodes, most urgent first, as "1 failed · 2 waiting". It wraps to as many lines as it needs at every panel width and never ends in an ellipsis, so the row shows every count. A line breaks only between two groups, so a count stays with its word, and it drops the separator at the break, so no line ends in one (round 38). The row's tooltip holds every group. This replaces the two groups and "+N" chip of ADR 0053.
- **Cut title.** A title cut on a card keeps its first word and the last words that differ, as "Create… partition 2024-Q3". A line too short for the first word keeps only what differs, as ADR 0065 says.
- **Fit.** A canvas from 1600 px wide fits a graph that can take it at 86%, which the bar calls 215%, and which is the Full tier's card. The zooms from 120% to 210% hold less text on a card, so a fit does not stop in them. A narrower canvas keeps the limit of ADR 0059.
- **Run bar.** Next keeps "Next waiting" until the bar's chips and zoom controls have given up their room. The word goes last.
- **Group labels.** A Nodes group's label takes the regular weight, one level under a section's semibold label.
- **Interrupted node.** The inspector's action row keeps Conversation in this state too, first, as in every other state. Conversation and Continue take the one row, and Retry goes to the More menu.

## Considered options

- **State chips with icons on the workflow row.** Six chips do not fit one line at 272 px, and the counts already show twice on screen.
- **A fit limit that grows with the window.** Zooms above 120% and under 210% change the card to the Compact and Detail forms, whose text is smaller than the Label form's.

## Consequences

- A workflow row grows by a line when its states do not fit on one, so rows in a run can differ in height.
