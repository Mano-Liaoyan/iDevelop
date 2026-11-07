# Fit shows the whole graph, and a cut title leads with the part that differs

Fit shows the whole graph at any zoom down to the editor's least zoom. A card title that does not fit and shares a long start with a sibling title starts with an ellipsis and ends whole, as in "…partition 2024-Q3". A docked conversation keeps its node's branch in view. A port names what passes through it only while the pointer is over the port or its wire. A tree group with more than 8 nodes starts folded, except "Needs you". A short inspector keeps its header to three lines. A folded Output section shows its result's own summary. This amends ADR 0059, ADR 0062, and ADR 0064.

Round 18 of the review found that Fit stopped at 90% on the 50-node workflow and cut columns at both canvas edges, that the backfill cards read "Backfill ledger partition 2025-…" three times, that the docked conversation cut the selected node's neighbors, that a connection had no local description, that a 27-node group filled the left tree, that the recovery note pushed the Agent section down at the minimum window, and that the folded Output section showed only metadata.

## How it works

- **Fit.** The fit zoom has no readable floor. It is never under the editor's least zoom, 10%, and never over 120%. A large graph takes the tier that its zoom gives. This withdraws ADR 0059's readable floor for Fit and Frame.
- **Cut titles.** `TitleParts` marks where each title's differing part starts when the title shares a start of at least three words with another title. When the whole title does not fit, the card shows an ellipsis and then the longest end of the title that holds the whole differing part and fits. The full card and the zoomed out card use this one rule in `EllipsisText`. The name tag, the card's peek, and the inspector show the whole title. This replaces ADR 0062's cut in the shared words ("Backfill… 2024-Q3") and ADR 0064's end-only cut for the zoomed out card.
- **Docked conversation.** When the conversation docks, the canvas keeps the selected node, or else the conversation's node, and its direct predecessors and successors whole in view. A view that shows them stays. Otherwise the view frames them and never zooms closer than it was.
- **Ports.** The pointer over a port shows a small label beside it, outside its card, with the port's meaning, such as "Input: Design". The pointer over a wire shows the wire's description at its output port. No label is static. The label replaces the port's and the wire's tooltips.
- **Left tree.** A group with more than 8 nodes starts folded, except "Needs you", which always starts open. Its header keeps its count. This replaces ADR 0064's rule that only "Done" folds, above 5 nodes.
- **Short inspector.** Under 700 px of inspector height, the header shows the title in one line, the state, and the actions. The state's sentence, such as "Closed mid-turn. Continue resumes the session.", moves to the state's tooltip.
- **Card agent line.** At the Label tier the client's letters and the model keep one line, and a model name that does not fit ends in an ellipsis, so every card's title keeps three lines. This withdraws ADR 0064's wrapped model name.
- **Run bar.** Where the run bar's words do not fit beside the zoom controls after the view controls and Follow move to the overflow menu, each chip shows its badge and its count, and its tooltip keeps the words. Only a bar that still does not fit takes its own row. This amends ADR 0063's rule that every chip keeps its words.
- **Output.** A folded Output section shows the first line of the latest result's summary. A result without a report keeps its heading and commit.

## Considered options

- **A simple card at a low zoom with a minimap open by default.** The existing tiers already reduce a card to its title and state at a low zoom, so Fit needs only to stop at the whole graph.
- **Static labels on every port.** They overlap wires and neighbor cards in a dense graph.
- **A compact list mode for the tree.** Folding groups keeps every node one click away without a new control.

## Consequences

- Fit on a 50-node workflow at the default window shows all columns at about 25%, at the Title tier.
- A title that differs only in its last word reads by that word on the canvas, and its shared start reads in the tooltip and the inspector.
