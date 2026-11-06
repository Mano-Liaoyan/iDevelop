# The canvas docks its bars, and a zoomed out card keeps one form

The bars and the minimap sit beside the node editor, not over it, so no control can cover a card. This replaces the floating controls of [ADR 0023](0023-the-canvas-keeps-336-px-and-its-controls-follow-its-own-size.md) and changes the zoom tiers of [ADR 0045](0045-wires-curve-and-show-execution-and-a-node-kind-stays-quiet.md).

Reviews found cards under the top bar, the run bar, and the minimap after Find, reveal, and panel changes. Margins in Fit and reveal do not help a view that the person moved, so the fix is in the layout.

## How it works

- **Docked bands.** A solid band over the editor holds the breadcrumb, Undo, Redo, Save, Add, Generate, and Run Workflow, and the status under them. A solid band under the editor holds the zoom controls and the run bar. The editor clips its cards and wires between them.
- **The minimap.** It is off by default. The person opens it from the bottom band, and it docks at the canvas's right, so the editor gets narrower while it shows.
- **Fit.** A fit takes the whole graph, down to the editor's least zoom of 10%, and keeps a card 24 px inside the editor. A view lands on whole pixels, so a card's edges stay sharp. A card that Find opens moves the view into the gaps between its neighbours where it can.
- **The run bar.** Its counts have words: failed, waiting, running, and stale. Waiting holds every reason a node waits, as the inspector's overview does, and its tooltip tells them apart. Next says what it selects. With no run, the band says how many nodes the workflow has.
- **Width.** Below 800 px Add and Generate show glyphs, so the workflow's name keeps its width. Below 760 px the progress shortens to "5/9". Below 680 px Run Workflow says Run. Below 520 px the words replace the chips.
- **One zoomed out form.** Under the full card, a card is a stripe in its kind's hue and its title. From 36% a last line under the title holds the kind's tile, the model and level, or the model alone where both do not fit, and the state's badge at its end. That line draws past the 64 px box that the layout sees, so no neighbour or wire moves. Under 36% the title takes two lines and the badge sits above the card. Under 23% a card is a block in its state's fill.

## Consequences

A fit can show a large graph at the title tier or the block tier, where the old floor of 40% cut it. Ctrl+K and a click bring back a readable zoom.
