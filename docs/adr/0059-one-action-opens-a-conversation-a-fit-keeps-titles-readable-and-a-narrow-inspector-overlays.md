# One action opens a conversation, a fit keeps titles readable, and a narrow window's inspector takes the left panel's room

A double-click on a card, or Enter on a selected card, opens the node's conversation with the keys in its composer. A fit stays between 90% and 120% of the zoom the bar calls 100%. In a window too narrow for both panels, a selected node's inspector takes its column and the left panel folds to its tab (amended below). A selection ring keeps a gap from the card's edge. This amends ADR 0055.

Round 12 of the review found that reading a node's output took a click on the card and a second click in the inspector, that the conversation opened from the keyboard showed no focus, that a fit of a 50-node workflow went to 61% where titles lost the words that tell them apart, that a small graph in a large window stayed at 100% in a small block, that the minimum window needed an extra action to edit a node, and that the selection ring hid a failed or waiting card's edge.

## How it works

- **Conversation.** A double-click on a card opens its conversation, as Enter does. The card's tooltip says so in its last line. The composer takes the keys when a conversation opens, by any path, and its 2 px focus line shows them. A double-click on the empty canvas still adds a node there.
- **Fit.** A fit never goes under 36% of the editor's zoom, the bar's 90%, where a card shows three title lines. A graph that needs less stops there, centred on the selected node, else a running node, else a node that needs the person, and the person pans or uses the minimap for the rest. A fit never goes over the bar's 120%, the last zoom at which a card keeps the form it has at 100%, so a small graph grows into a large window.
- **Narrow window.** Under a 1000 px window, while the person has not chosen with the inspector's tab, selecting a node opens the inspector in its column at its least width, 280 px, and folds the left panel to its tab. The canvas, its top bar, and its run bar take the width between them, and a fit uses that width. The inspector's tab, or the left panel's tab, Ctrl+B, or Find, gives the room back to the left panel until another node is selected. An open conversation, or a choice with the inspector's tab, keeps the swap off.
- **Selection ring.** The accent ring sits a gap as wide as the line outside the card's edge, so a failed, waiting, or stale card keeps its colour inside it. The pointer draws a faint accent line there, which no state uses.

## Considered options

- **A conversation button on the card.** It adds a control to every card, and a double-click and Enter already reach the same action.
- **Keep the transcript focused when no node waits.** Nothing showed where the keys were, and a reply needed a click first.
- **Fit to the whole graph at any zoom.** At 61% titles end in an ellipsis that drops the words that tell nodes apart.
- **Open the inspector column beside the left panel at the minimum window.** The column takes 280 px from a canvas that has 336 px at least.
- **Lay the inspector over the canvas's right edge.** Round 13 found that it covered the run bar's end, Next waiting, Run Workflow, and the downstream cards. Nothing may lie over other content.

## Consequences

- A fit of a large workflow shows part of the graph. The minimap and the run bar's counts show the rest.
- In a narrow window the left panel shows only its tab while a node's inspector is open. The person opens it with one click, which closes that inspector.
- A panel that opens, closes, or is dragged fits the graph again when a card that showed whole is cut, and keeps the selected card in view.

## Amendment (round 13)

The first form of this record laid a narrow window's inspector over the canvas's right edge with a shadow. It covered the run bar's chips and Next waiting, the top bar's Run Workflow, and the cards downstream of the selection. The inspector now takes its own column, and the left panel gives the room.
