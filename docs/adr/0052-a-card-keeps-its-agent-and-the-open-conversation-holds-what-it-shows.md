# A card keeps its agent, Run Workflow says what it runs, and the open conversation holds what it shows

A card names its agent in every state and at every zoom that shows text. The canvas and the run bar use one word for each action at every width. While a conversation is open, the inspector shows only what the conversation does not. The coordinator of the UI review set these decisions on 2026-11-06, and they change parts of [ADR 0049](0049-a-title-is-cut-at-its-end-and-the-canvas-and-composer-say-what-a-node-needs.md), [ADR 0050](0050-the-inspector-leads-with-what-a-node-gave-and-the-left-area-shows-the-shown-workflows-nodes.md), and [ADR 0051](0051-the-left-tree-follows-the-window-and-an-inspector-section-keeps-its-place.md). This record changes only what the app shows and reads: workflow files and the engine stay the same.

Round 5 of the review found that state replaced the model on a card, that a far card lost its last line, that Run Workflow did not say that it skips current results, and that one action had three names. It also found that the reply's action read as a hint and went away while a person typed, that the conversation and the inspector said the same things side by side, and that a commit's file rows held no diff.

## How it works

- **A card's last line.** At full size it holds the client, then the model and level, then the state in a word at the end. The agent keeps its place in every state. An interrupted node's status is "Interrupted", on every surface.
- **A far card.** The stripe says the kind, so a node with an agent gives the tile's place to its client's letter: C, X, P, or A. The model follows, and the state's badge ends the line inside the card. This holds in every tier with text, so the Title tier takes a last line too, and its card draws 158 canvas px high. The last choice of a short line is the model's family, so a line never ends in an ellipsis for a name that a word can hold.
- **A far title.** A cut before the part that differs falls between words: "Backfill… 2024-Q4".
- **Run Workflow.** It keeps its name at every width, and "Run all" is gone. A compact canvas shows its glyph, and its tooltip names it. While some nodes keep current results, the button adds its reach, such as "3 of 9", at every width.
- **Stop Workflow.** It replaces "Stop All", so the pair reads as one.
- **Add and Generate.** They are glyphs at every width.
- **Run bar.** With too little room it first sums what waits for the person, then leaves its other chips their counts. Failed and waiting keep their words, and Next keeps its label at every width.
- **Frames.** The run frame around the canvas is gone, because the bar and the cards say that a node runs. The canvas's focus is a 2 px line, its only frame.
- **The running wire.** It keeps a wire's weight, with no glow, and moving arrows show the flow.
- **The reply's action.** "Accept the agent's message as the result" is a real button in one place in every state of the banner, also while a person types.
- **Beside the open conversation.** The inspector's header gives up its tile, title, and the actions that the conversation holds. Its Task section rests folded and opens on a click. Its Output section lists only where the result goes. This replaces "the row always holds the summary" of ADR 0051.
- **A result's files.** One changed file has no summary line. Each file row opens onto its diff, read from the commit, and a small change in a single file starts open.
- **Code.** A long line also wraps after a comma, so a line wraps between its values and not in a name.
- **Find.** A view whose titles read keeps its zoom, so a found card stays among its neighbours.
- **Long fields.** A field reads in up to two lines. This replaces the three lines of ADR 0051.
- **Inspector width.** A value's box stops at 420 px, and the label column takes the rest.
- **Blueprint editor.** Edit and Preview share one place, one click apart.
- **Edge tabs.** An open panel's tab shows under the pointer or the keyboard's focus. A closed panel's tab stays.

## Considered options

- **Keeping the kind's tile on a far card and naming the client in words.** The line has no room for both at a Fit zoom.
- **Keeping the status word on a far card.** The badge says the state, and the word was what took the model's place.
- **A note beside Run Workflow.** The top band has no room for it on a narrow canvas.
- **Hiding the inspector while the conversation is open.** The Agent section and the routing are what a person needs beside the conversation.
- **Horizontal scrolling for code.** The scroll bar hid the line's end, which review round 3 found.

## Consequences

- A far card's client letter needs its peek or its tooltip to read as a name.
- A person who wants to edit a task beside the conversation opens the Task section first.
- An open panel's tab is not visible until the pointer reaches it.
