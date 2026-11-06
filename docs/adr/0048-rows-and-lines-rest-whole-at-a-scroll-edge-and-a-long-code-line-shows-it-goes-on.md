# Rows and lines rest whole at a scroll edge, a long code line shows that it goes on, and the left area drops its progress lines

A scroll edge never cuts a line of text or a row, a long code line keeps its scroll bar in view, and a workflow's row ends in a small ring and its counts. The coordinator of the UI review set these decisions on 2026-11-06, and they change parts of [ADR 0046](0046-the-left-area-reads-in-one-line-a-person-opens-what-they-need-and-the-inspector-holds-a-nodes-facts.md). Names still wrap in full (issue #108). This record changes only what the app shows: workflow files and the engine stay the same.

Round 3 of the review found a code line cut with no sign that it goes on, a line of text cut at the top of the transcript, a tooltip over the words being typed, and a row cut at the left area's footer. It also found full-width progress lines that read as underlines, and node rows as large as their workflow's.

## How it works

- **Code and tables.** A block whose content is wider than the column keeps its horizontal scroll bar in view. A block that fits has none. This replaces "a code block's scroll bar shows under the pointer".
- **Transcript.** At rest, the view's top edge lies between two lines. A scroll that stops in a line moves on past it in the way it went, so a small step still moves. A drag rests when it ends. The room under the last row lets the end of the view rest on a whole line too.
- **Tree.** A cover in the panel's color hides a row that an edge would cut, until the row comes in whole. The project and the open workflow of the rows under the top edge stay pinned.
- **Composer's tooltips.** They open along the composer's bar, so they never cover the words being typed.
- **A workflow's row.** Its end shows what runs, what waits, and what failed, each with a count, and a stale count only without them. Without any of these it shows a small ring and the completion count. The ring replaces the thin line under the row.
- **A node's row.** Its name is 12 px under a 13 px workflow, and it has the inspector's hollow mark before its first run.
- **Activity.** The short list keeps a failure in view, and a row names its project while more than one is open.
- **Find.** The results float under the box, as wide as the box, and a name wraps in full. This replaces the 440 px list over the canvas.
- **No project.** The canvas holds Open Folder alone, and the left area lists the folders the person closed.
- **Attention card.** A wait for words is one line on the composer, with no outline of its own and no typing hint. On a wait for a reply the action says "Accept as result", and its line says that it completes the node.
- **While the inspector shows the node.** The conversation leaves out the Task strip, Show in inspector, and a result card with no commit.
- **A ghost card.** It takes the card's far form at each zoom. A ghost needs no move of its own, because the canvas's transform moves it with the cards.

## Considered options

- **A fade over the top of the transcript.** It still cuts a line, at the fade's edge, and a fade over text breaks ADR 0046.
- **Snapping the tree's scroll to rows.** A cover needs no change to the scroll position, and it also holds at the tree's bottom edge, which depends on the panel's height.
- **Capping a name at two lines.** The owner asked for every name in full.

## Consequences

- A person who scrolls the transcript by a few pixels sees it move by a whole line.
- A workflow with facts shows no progress at its row's end. Its tooltip keeps the whole count.
