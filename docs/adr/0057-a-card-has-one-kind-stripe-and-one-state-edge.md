# A card has one kind stripe and one state edge, and Output leads the inspector

A card shows its kind with a stripe and its state with an edge and a badge. A person reads one word for what waits on every surface. A node row and an Activity row take one line. This changes parts of [ADR 0053](0053-one-count-one-line-per-row-and-a-running-node-fills-its-band.md), [ADR 0055](0055-a-fit-rests-at-100-percent-and-the-canvas-names-no-port.md), and [ADR 0056](0056-whole-rows-whole-words-and-one-chip-for-the-agent.md). Workflow files and the engine stay the same.

Round 9 of the UI review found too many color layers on a card, a running card that glared and that looked selected, node rows of four lines, a result under the settings, and one state with several names.

## How it works

- **Card.** A card has one surface. The kind has only its stripe, or its tile at the full size. No kind tint, state tint, or selection fill remains.
- **State.** An edge and a badge show a state. The model's line keeps its color in every state.
- **Running.** A running node has a thin strip along its top edge and its spinning badge. The edge stays plain, so only selection draws a ring. The band fill and the glow are gone.
- **Hover.** A card keeps its surface under the pointer and draws one thin accent line.
- **Stale.** A stale edge and badge are quieter than failed and waiting.
- **Zoomed out cards.** The Title tier's card widens 32 canvas units into the gap at its right. A title then keeps more words at the size that stays readable. The title has medium weight. A one-word model falls back to its major version, so "GPT-6" shows with no ellipsis.
- **Name tag.** At the Block tier the one selected node shows its name in a tag over its bar. The tag has a screen size and stays in the canvas.
- **Fit.** A fit keeps 16 px clear of the editor's edges. This replaces the 8 px of ADR 0053.
- **Wires.** A wire is 2 units wide, and a feeding wire 2.5. Lanes stand 22 units apart. A wire's tooltip adds the first line of the result that it carries.
- **Canvas focus.** The canvas's focus line is one thin, quiet line. This replaces the two-pixel ring of ADR 0056.
- **Run Workflow.** It keeps the primary look in every state. With nothing to run, its tooltip says why. This replaces the quiet look of ADR 0053.
- **Generate.** The button shows its word while the bar has room.
- **One word for what waits.** The run bar, the Overview, and the inspector's header say "Waiting" for every wait that needs the person's word. Interrupted stays apart. The tooltip names the reasons. A question and a reply share one badge.
- **Run bar.** The progress and Next keep their words. A tight bar first moves the view controls to the overflow menu, and then gives each group its badge and count. This replaces the order of ADR 0056.
- **Inspector order.** The sections keep one order in every state: Output, Task, Agent, Input, Goes to. Output rests folded beside an open conversation, which shows the result. This replaces the order of ADR 0055.
- **Inspector sections.** A section's title starts at its rows' left edge, and its chevron follows the word. A long title takes up to four lines. A skill list wraps in its field.
- **Failed card.** Change model is the primary action when the failure names credit, balance, quota, billing, or another model. Otherwise Retry is primary.
- **Conversation header.** The title takes one line in medium weight, and its tooltip holds the whole title. This replaces the two lines of ADR 0054.
- **Left rows.** A node row takes one line, also while selected. Its tooltip and the inspector hold the name. A workflow name still shows in full. This replaces the two lines of ADR 0053.
- **Activity.** A row takes one line, and its tooltip says where the node is. While several projects are open, a line under the heading says "All open projects".
- **Selection in the tree.** The shown workflow has no side mark. Its name's weight says that it is shown, and a fill marks the selected row.
- **New workflow.** The Projects heading has a New workflow button for the shown project.
- **Blueprint editor.** The canvas dims while the inspector edits a blueprint.

## Considered options

- **Keeping the kind tint and removing the stripe.** The tint competed with the state colors in a dense graph. A stripe keeps the kind's hue at every zoom with less color.
- **Moving sections by state.** ADR 0055 rejected this. One order with Output first gives the result without a move.
- **A name tag at the Title tier.** The tag would cover the row above. A card there has its title, and the card's tooltip holds the whole name.
- **A fit above 100%.** ADR 0055 rests a fit at 100%, and this round keeps that.

## Consequences

- A saved layout with a column gap under 32 canvas units lets a Title tier card reach its neighbour. The built-in layouts leave more room.
- A kind reads less strongly from far away. The tree and the inspector keep the kind's tile.
- A node name over one line loses its end in the row. This is the owner's to reverse.
- A person sees why a node waits only in the tooltip, the card, or the conversation.
- The rule for Change model reads the failure's words, so an unusual provider message leaves Retry as the primary action.
