# Run Workflow keeps one name, and every state keeps its word

Run Workflow always reads "Run Workflow". The run bar says in a plain line when it runs only part of the workflow. Every group of node states keeps its word in the run bar and in a folded workflow row at every width. A card shows the model's name as the inspector's Model field shows it. The left tree lists at most 12 node rows of a large workflow until the person asks for all of them. This amends ADR 0062.

Round 16 of the review found that "Run 1 of 8" read as progress and made the button change its name, that the run bar and the left tree showed interrupted, running, and stale groups only as a badge and a number, that a card read "Gemini 3" for "Gemini 3.8 Flash" and "Sonnet 5" for "Claude Sonnet 5.5", and that one expanded 50-node workflow filled the left panel.

## How it works

- **Run Workflow.** Its label is "Run Workflow" in every state. When nodes with a current result keep it, the run bar adds a muted line, for example "Runs 3 nodes without a current result". The tooltip still says what a click does. This withdraws ADR 0062's "Run 3 of 9".
- **Run bar.** The bar always shows "n/m done" and each group in its words. As the canvas narrows, the view's own controls and then Follow move to the overflow menu. If the words still do not fit, the bar takes its own row across the whole canvas above the zoom controls, and its groups wrap to a second line. No group shows as a bare badge and number, and no group hides behind a tooltip. Fit, Zoom in, Zoom out, and Next stay in the bar.
- **Folded workflow row.** It shows the run bar's groups in their words and wraps them.
- **Card model.** The card's model text is the model's name, the same string as the inspector's Model field. A card that is too narrow ends it in an ellipsis. No cut drops a version or a variant. The tooltip adds the level. This withdraws ADR 0062's far model line.
- **Left tree.** A workflow of more than 12 nodes lists first the nodes that need the person or run (waiting, failed, interrupted, running), then the other nodes up to 12 rows, each part in reading order. A row "Show all N nodes" lists every node, and "Show fewer nodes" goes back. The selected node always has its row. Names still wrap in full (issue #108).
- **Hover.** The pointer over a card draws a neutral line outside its edge. Accent marks selection and focus only. An inspector field under the pointer shows a 1 px neutral edge and a stronger fill.
- **Titles at a fit.** The Label tier's title takes 6 more canvas units on each side, so more titles read whole. A title that shares its first words with another title still keeps its differing part (ADR 0062).

## Considered options

- **A count chip on Run Workflow.** It made the button wider and repeated the run bar's progress.
- **Badge and number for quiet groups on a narrow bar.** A person had to learn the badges.
- **A shortened model name that keeps the version.** Any rule of its own can differ from the inspector's name.
- **A status filter above the tree.** It adds a control. Ordering the rows by need fixes the long list without one.

## Consequences

- On a narrow canvas the bottom band can be two or three rows high, so the canvas above it is shorter.
- While the workflow runs, node rows of a large workflow move as their states change.
