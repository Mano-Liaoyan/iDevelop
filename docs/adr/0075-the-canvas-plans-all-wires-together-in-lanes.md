# The canvas plans all wires together, in lanes through the free channels

The canvas plans all its wires in one pass, and each wire takes its own lane in the free channels between rows and columns. This supersedes the "Port points" part of ADR 0061 and the lane rules of ADR 0045. It changes only how wires draw. Graph data, the engine, and saved positions do not change.

Round 36 of the review found that wires shared a gap and overlapped, that a long wire ran on a card's border, and that detours looped around adjacent cards. Each wire found its route alone, and a later step moved the wires apart. That step could not send a wire elsewhere when a gap was full.

## How it works

- **One plan.** `WireRouter` plans all wires of a canvas. It picks each wire's points on the card edges, finds each route past the cards, gives each route its lanes, and turns each route into a curve.
- **Port points.** The wires of one port take separate points on the card's edge, 12 canvas units apart at the Full tier and wider by the tier's wire scale. They take the order of the rows that the wires head for, which a detour can change from the far end's row. The wire nearest the port's row keeps the port. A wire at its own point has a small socket in the column of the port's socket.
- **Channels and lanes.** A channel is the free space beside a run: between two columns for a run up or down, and between two rows for a run across. Runs that overlap in a channel take separate lanes in its middle, 16 canvas units apart at the Full tier and never less than 6, both times the wire scale.
- **Crossings.** The lanes of a channel take the order that crosses the fewest wires. The order is stable where a swap gains nothing.
- **Full channels.** A channel too narrow for its lanes sends its longest wire elsewhere. That wire keeps out of the other narrow gaps between rows, so it takes open space. A wire that would grow by more than 900 canvas units stays, and the channel's lanes share its room. A wire that feeds a running node never leaves.
- **Cards.** A wire keeps off the whole rectangle of every card that it does not join, in every tier. A curve keeps closer to its route where a wide curve would touch a card.
- **Active path.** A wire that feeds a running node draws over the other wires.
- **Speed.** Port changes during a drag ask for one plan after layout. A plan keeps every route that a changed card cannot reach.

## Considered options

- **Start and end every wire at the port's center.** Wires of one port then share one point and overlap near it.
- **Move rows apart at far zoom tiers.** Saved positions must not change, and ADR 0061 rejected the display-only form.
- **Route with a congestion cost inside the path search.** The search would run more often. Closing a full channel for one wire gives the same result for the layouts that the canvas has.

## Consequences

- At the Title and Block tiers, a gap between rows is about 32 canvas units. One lane fits in its middle, which leaves less clear space beside the wire than at nearer tiers.
- A layout with more wires than a channel holds at the least spacing compresses the lanes of that channel.
