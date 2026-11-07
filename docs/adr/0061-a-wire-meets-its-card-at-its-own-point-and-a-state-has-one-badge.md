# A wire meets its card at its own point, and a state has one badge

Wires that share a port meet the card at their own points on the port's side. A selected card keeps the size of the other cards. Each state has one badge and one tone on every surface. Activity folds while the left tree is long. A disabled control keeps a readable label. Run Workflow keeps its words in every state. This amends ADR 0045, ADR 0058, and ADR 0060.

Round 14 of the review found that wires met in one point at a busy port, that a selected card at a fit grew to nine lines and broke the rows, that the inspector showed an interrupted node with the waiting glyph and color while its card showed the neutral pause badge, that Activity took a third of the panel beside a 50-node tree, that the disabled Run Workflow label read at 1.35:1, and that "Run 1 of 8" read as one node.

## How it works

- **Port points.** The wires of one port take points spread over the card's edge, 12 canvas units apart at the Full tier and wider by the tier's wire scale, in the order of the rows of their other ends. The wire whose other end lies nearest the port's row keeps the port's own point, so a wire between two cards on one row stays straight. The points keep 10 units from the card's top and bottom. A wire away from the socket starts or ends at the card's edge. The angled fans of ADR 0045 are gone.
- **Selected card.** A selected card keeps its tier's title limit. The inspector and the card's peek hold the whole title. ADR 0060's growing card is withdrawn.
- **Fit.** A fit includes the wires, so an arc past the cards stays in view. A smaller editor that cuts a card that showed whole fits again, also when the person moved the view.
- **One badge.** The inspector's header, its input and output rows, Find, the left tree, and the conversation's status use the card's badge: a disc in the state's color with its glyph. An interrupted node takes the neutral tone everywhere, as its gray badge does.
- **Activity.** Activity starts folded while the tree is taller than half of the panel, as it does in a short panel.
- **Disabled controls.** A disabled button keeps full opacity. Its label takes the muted text color, at 4.5:1 or more in both themes, and a disabled primary button takes the quiet fill. A disabled menu item does the same.
- **Run Workflow.** It always reads "Run Workflow". Its tooltip says how many nodes it runs, and the run bar says how far the workflow is.

## Considered options

- **Move each row of zoomed out cards down by the tier's reach, so wires keep the saved gap between rows.** It moved cards under a fit and under an open proposal's ghosts. Left for a later round.
- **Keep the fans and add port points.** Two spreads on one wire bent it twice near the port.
- **A count beside Run Workflow.** The run bar already shows the progress, and a changing width moved the toolbar.

## Consequences

- At a fit, a long wire that passes between two rows of zoomed out cards still runs close to their edges where the saved layout leaves a small gap.
