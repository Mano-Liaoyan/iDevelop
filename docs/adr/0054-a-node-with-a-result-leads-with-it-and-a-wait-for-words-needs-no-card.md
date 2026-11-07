# A node with a result leads with it, and a wait for words needs no card

The inspector puts what a person needs first in each state, and it keeps the node's title and result beside the open conversation. The conversation offers a message as the result only when the message can be one. This changes parts of [ADR 0050](0050-the-inspector-leads-with-what-a-node-gave-and-the-left-area-shows-the-shown-workflows-nodes.md), [ADR 0051](0051-the-left-tree-follows-the-window-and-an-inspector-section-keeps-its-place.md), and [ADR 0052](0052-a-card-keeps-its-agent-and-the-open-conversation-holds-what-it-shows.md). Workflow files and the engine stay the same.

Round 6 of the UI review found that the result action competed with an open question, that the model picker sat under the fold, and that the inspector lost the node's title and result while the conversation was open.

## How it works

- **Result action.** While the agent's last message ends in a question, the app does not offer it as the result on any surface. Otherwise the conversation's action says "Use this message as the result", and the header and the card say "Use as result".
- **Wait for words.** A wait with no action and no words of its own has no card. The message asks, and the composer answers. A short transcript then sits on the composer.
- **One term.** A card says Reply or Question, the words that Activity uses.
- **Result card.** It copies the commit's id, and a changed file opens from the node's folder. The diff still opens in place.
- **Code.** A long line continues on an indented line, past its source line's own indent, so each source line shows where it starts. This replaces the comma breaks of ADR 0052.
- **Conversation header.** A title takes two lines, and the folded task counts the inputs that the agent read.
- **Composer.** While the inspector is open, the model chip only names the model. Send's tooltip says only what is not plain.
- **Activity.** A jump to a node opens its conversation in the dock, so its graph stays in view. A conversation that is already open keeps its layout.
- **Section order.** A node with a result shows Output, Agent, Input, and Task. A node with no result shows Agent, Output, Input, and Task. This replaces one order for every state.
- **Inspector header.** It keeps the tile and the title beside the open conversation, and Output keeps the result there.
- **Row height.** A row's first line is 24 px, its label column 84 px, and a value 320 px at most.
- **Fields.** The filter and the rows' fields share one fill and one hover tint. A multiline field reads four lines at rest.
- **Revert arrow.** It stands right after the label in every row.
- **Overrides.** A section counts "2 overrides", a word that a run's result cannot claim.
- **Scroll bar.** The sections' scroll bar stays in view while they overflow.
- **Go to.** It is gone, because Agent now sits near the top. Expand and Collapse all sections move to the header's More menu.
- **Library.** It manages blueprints. A row has no add button, and Place sits in its More menu. The canvas's Add popover adds nodes.
- **Blueprint editor.** The template opens as Preview, with each empty field as a mark. Edit shows the source. Syntax help and version notes move to Template help and the caption's tooltip.
- **Zoom.** The bar calls a zoom of 0.4 "100%", the size at which a card reads and a workflow fits. Only the label changes, so the tiers keep their real zooms.
- **Ports.** A socket sits outside the card's edge, and a wire ends at its outer edge. A card names Input and Output while the zoom leaves it room.
- **Client marks.** A card uses CC, Cx, Pi, and AG in place of one letter.

## Considered options

- **Moving the agent's settings into the header.** The section already holds them, and a second place would repeat them.
- **Asking the engine whether a message is a question.** The engine has no such fact. A last message that ends in a question mark is a plain rule that a person can check.
- **Hiding the filter behind a glyph.** It saves one row, but a filter that stays in view keeps Godot's and Blender's pattern.
- **Scrolling long code lines.** ADR 0049 rejected a block that cuts a character.

## Consequences

- A person who wants to end a node on a message that ends in a question must first reply.
- Selected code text holds the continuation indents. Copy gives the source.
- A section can change place when a node gets its first result.
- The bar's zoom percentage no longer equals the canvas's scale.
- A far card names its ports only in the tooltip.
