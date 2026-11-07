# One action opens a conversation, a fit keeps titles readable, and a narrow window's inspector lies over the canvas

A double-click on a card, or Enter on a selected card, opens the node's conversation with the keys in its composer. A fit stays between 90% and 120% of the zoom the bar calls 100%. In a window too narrow for the inspector's column, a selected node's inspector lies over the canvas's right edge. A selection ring keeps a gap from the card's edge. This amends ADR 0055.

Round 12 of the review found that reading a node's output took a click on the card and a second click in the inspector, that the conversation opened from the keyboard showed no focus, that a fit of a 50-node workflow went to 61% where titles lost the words that tell them apart, that a small graph in a large window stayed at 100% in a small block, that the minimum window needed an extra action to edit a node, and that the selection ring hid a failed or waiting card's edge.

## How it works

- **Conversation.** A double-click on a card opens its conversation, as Enter does. The card's tooltip says so in its last line. The composer takes the keys when a conversation opens, by any path, and its 2 px focus line shows them. A double-click on the empty canvas still adds a node there.
- **Fit.** A fit never goes under 36% of the editor's zoom, the bar's 90%, where a card shows three title lines. A graph that needs less stops there, centred on the selected node, else a running node, else a node that needs the person, and the person pans or uses the minimap for the rest. A fit never goes over the bar's 120%, the last zoom at which a card keeps the form it has at 100%, so a small graph grows into a large window.
- **Narrow window.** Under a 1000 px window, while the person has not chosen with the inspector's tab, selecting a node lays its inspector, 280 px wide, over the canvas's right edge with a shadow. The tab beside it closes it until another node is selected. The canvas keeps the selected card clear of it. An open conversation, or a choice with the tab, keeps it off.
- **Selection ring.** The accent ring sits a gap as wide as the line outside the card's edge, so a failed, waiting, or stale card keeps its colour inside it. The pointer draws a faint accent line there, which no state uses.

## Considered options

- **A conversation button on the card.** It adds a control to every card, and a double-click and Enter already reach the same action.
- **Keep the transcript focused when no node waits.** Nothing showed where the keys were, and a reply needed a click first.
- **Fit to the whole graph at any zoom.** At 61% titles end in an ellipsis that drops the words that tell nodes apart.
- **Open the inspector column at the minimum window.** The column takes 280 px from a canvas that has 336 px at least.

## Consequences

- A fit of a large workflow shows part of the graph. The minimap and the run bar's counts show the rest.
- In a narrow window the overlay covers the canvas's right part, the run bar's right end included, until the tab closes it.
