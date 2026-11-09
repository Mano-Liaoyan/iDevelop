# The left area reads in one line, Find floats, panels close, and the inspector holds a node's facts

The left area's rows take one line where the name fits, Find floats over the window, each side panel closes, and while the inspector shows a node the conversation leaves that node's facts to it. The owner handed the defaults of the design brief to the coordinator of the UI review on 2026-11-06, and the coordinator set these decisions. Names still wrap in full (issue #108), and the app shows no durations. This record changes what the app shows and one optional field of the per-user session file: workflow files and the engine stay the same.

Round 2 of the review found that the conversation repeated the inspector, that a question showed twice, that Ctrl+J landed on a failed node, that a card could hide under a floating control, and that the left area's two-line rows, stacked bars, and Find took the space of what a person came to see.

## How it works

- **A workflow's row.** One line: the name, and at its end one fact. That fact is what failed, else what waits, else what runs, else what is stale, as a glyph and a count. Without one it is the completion count, such as 7/8. A thin neutral line under the row shows progress, and the tooltip holds the words. An accent dot marks unsaved work. A project's row sums its counts only while it is folded.
- **Disclosure.** Every chevron sits at the left of its row, so the right edge holds only facts and all facts end on one edge. A node's row uses the same kind tile as the inspector. The left area is 272 px wide by default, so a typical name fits beside its fact.
- **Activity.** What waits comes first, then what failed, then what runs, so the row that needs a reply stays in view at the smallest window. The heading shows the waiting and failed counts while the list is folded too.
- **Find.** The results float from under the box at 440 px, over the tree and the canvas. A click elsewhere closes them.
- **Panels.** A tab on each panel's edge, Ctrl+B for the left area and Ctrl+Alt+B for the inspector, closes a panel, and the tab stays at the window's edge to bring it back. Below a 1000 px window the inspector closes until the person opens it. Alt+M, Show in inspector, and Ctrl+K open what they need.
- **No project.** The left area offers Open folder and the folders the person closed, which the session file keeps under `recent`.
- **Conversation header.** One line holds the title, where the node is, and the workflow's actions. The status and the Input and Goes to strip show only while the inspector is closed. The result has one summary, its card in the transcript.
- **Result card.** It reads the commit with Git, and shows the files changed with their added and deleted lines. The list opens from the card.
- **Attention card.** The card never repeats the agent's words, which stand just above it. On a wait for a reply its action says "Use agent's message", and a line names the nodes that would read it, so it cannot read as approving the draft.
- **Keys and focus.** Ctrl+J stops only on nodes that wait, and Ctrl+Shift+J on nodes that failed. The composer takes the keys when the conversation opens only while the node waits for the person's words, and only then does it show the focus line.
- **Edges.** No fade covers text. A row or a line ends at a hairline. The canvas's top and bottom bars sit in solid bands, so a card meets the band's edge and never a control. A long code span stands as its own block, and a code block's scroll bar shows under the pointer.

## Considered options

- **Capping a name at two lines.** It keeps the rhythm, but the owner asked for every name in full.
- **Keeping the stacked state bar.** It shows every state, but it repeats the counts and its segments are too thin to read.
- **Offering numbered choices for a question in prose.** Parsing an agent's sentences would invent choices it did not offer, so numbered choices stay for a client's structured question.
- **Drawing the reserved space only in Fit and Reveal.** A pan would still put cards under the controls, so the bands take the space for good.

## Consequences

- The canvas is 16 px narrower at the default window, so its compact width moves from 700 to 680 px.
- A band covers the canvas under the bars. The bars no longer float over cards there, and the minimap still floats over the bottom corner, where Reveal moves a card clear of it.
- A person opens a completed node's conversation and must click or press F6 to reach the composer.
