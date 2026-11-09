# Whole rows, whole words, and one chip for the agent

A scroll container never shows a cut row without a sign, a title never breaks inside a word, and a node's agent stays in view wherever text reads. This changes parts of [ADR 0055](0055-a-fit-rests-at-100-percent-and-the-canvas-names-no-port.md): its Zoomed out cards, Counts, Left area, and Composer lines. Workflow files and the engine stay the same.

Round 8 of the UI review found rows cut under the tree's pinned rows, an inspector foot with no sign of more content, titles broken inside a word, and cards and a composer that did not name the model.

## How it works

- **Tree.** The tree rests on a whole row under its pinned rows, and it goes on in the direction it moved. At the end of its range a cover hides a row that the pinned rows would cut.
- **Scroll bars.** The left tree, the inspector, and the transcript show the scroll bar at full strength.
- **Inspector foot.** While more waits below, the foot shows a down chevron. A short row that would touch the foot hides under it, so the last row in view is whole.
- **Titles.** A card, conversation, inspector, or tree title breaks only between words. A word wider than a line ends in the ellipsis.
- **Smallest cards with text.** The Title tier keeps the agent's line and the badge inside the card. The view sets the text size from the zoom, so the text stays at 10 px on screen, and a closer zoom keeps three title lines.
- **Run bar.** The chips keep their words. A tight bar gives up the progress, which the tree row and the tooltip hold, before a chip loses its words.
- **Composer.** The model chip shows the model and the level in every layout. It also holds Open in terminal and the client's limitations, so the composer has no separate More button.
- **A waiting question.** The message that a node waits on wears the waiting state's frame, and the line over it says "Waiting for your reply".
- **One outline.** A selected card has one outline. The canvas's two-pixel ring shows the keyboard's focus.
- **State marks.** Every list uses the state badge. Idle is an empty ring, and a folded workflow row uses the badge in place of a dot.
- **Selection.** The selected node's row holds the one selected fill. Its workflow keeps its mark and its name's weight.
- **Inspector sections.** A section header has no band. Type weight and spacing carry the sections.
