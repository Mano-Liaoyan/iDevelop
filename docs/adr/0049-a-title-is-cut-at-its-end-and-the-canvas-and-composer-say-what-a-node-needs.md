# A title is cut at its end, a wire goes around a card as it draws, and the canvas and the composer say what a node needs

A title that does not fit ends in an ellipsis on every surface, a wire keeps clear of a card at the height the card draws, the run bar and a zoomed out card name why a node waits, and the composer shows one main action for each state. The coordinator of the UI review set these decisions on 2026-11-06, and they change parts of [ADR 0045](0045-wires-curve-and-show-execution-and-a-node-kind-stays-quiet.md), [ADR 0047](0047-the-canvas-docks-its-bars-and-a-zoomed-out-card-keeps-one-form.md), and [ADR 0048](0048-rows-and-lines-rest-whole-at-a-scroll-edge-and-a-long-code-line-shows-it-goes-on.md). This record changes only what the app shows: workflow files and the engine stay the same.

Round 4 of the review found a conversation title cut at a word's end with no ellipsis, cards whose title began with an ellipsis on a line of its own, wires that ran under a card's last line, a waiting count whose glyph matched no card, and a completion action beside the composer that took as much weight as a reply.

## How it works

- **Titles.** Wrapped text with a line limit uses `EllipsisText`, because Avalonia's own trimming adds no ellipsis to a cut at a word's end. A title starts with its first word and is cut in one place only. The part that tells it from a similar title stays only when a start of the title fits before it.
- **Conversation header.** The title has one line, with the place under it in the main layout.
- **Breadcrumb.** A workflow's name takes the width it needs, and it gives up width only to the bar's other controls.
- **A zoomed out card.** Its stripe runs its whole height, a kind's band sits behind its title, and text keeps clear of its edges and its ports. Under 36% the badge sits on the card's corner, so it has one owner.
- **A zoomed out card's last line.** It names what a waiting node needs, or says Failed or Stale. Other nodes show the model, cut at a word to the version where the line is short.
- **Wires.** A wire goes around a collapsed card at the height it draws in the zoom tier, and it finds its path again at a tier's threshold. A straight run keeps 10 px from a card, and a curve 8 px. Wires draw heavier, and a dimmed wire keeps opacity 0.6.
- **Selection.** A selected card and its wires take the accent, as a selection does in the left area. This replaces the neutral line of ADR 0045. Running keeps its glow and its thick edge, and the canvas takes a frame while a node runs.
- **Run bar.** Each reason to wait is a chip with the glyph its cards show. A bar that lacks room for its words shows its chips' counts and Next's glyph. Under 520 px the view controls move to an overflow menu, and the minimap goes away only there.
- **A workflow's row.** It shows one state, the one that most needs the person. The run bar holds every count. This replaces the row's list of counts in ADR 0048.
- **Composer.** More holds Open in terminal and what the client can do, so the header has no info glyph. While a turn runs and nothing is written, Stop is the only main action. A draft shows Send and a labelled Stop and send.
- **Attention card.** A wait for words says what to do on one line. On a wait for a reply the action says "Use this reply as the result", stays quiet, and goes away while the person writes. This replaces "Accept as result" in ADR 0048.
- **Task.** The first turn shows also while the inspector is open, and opened, it names the nodes the agent read.
- **Code.** A long line wraps, so a block never cuts a character. This replaces the scroll bar of ADR 0048. A block with a named language colors keywords, strings, and comments.
- **Result card.** Up to three changed files show open.
- **Empty workflow.** The card holds Generate Workflow and an Add a node button.
- **Blueprint form.** A check box shows the preview, on by default, under the template.

## Considered options

- **Keeping the first three words of a title and the differing part at both ends.** It drew a lone ellipsis on a small card.
- **A Run label with no scope on a narrow canvas.** It reads as a node's Run, so the label is "Run all".
- **Durations on a running card.** The log has no time data, so the card has none.

## Consequences

- A very long word still breaks inside the word on the smallest card.
- A selected running card shows two blues: its edge's and the selection's.
