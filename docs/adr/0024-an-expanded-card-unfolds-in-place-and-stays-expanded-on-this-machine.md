# An expanded card unfolds in place and stays expanded on this machine

A card on the canvas can expand. An expanded card unfolds in place to 320 px wide and as tall as its details, and the cards around it slide aside on screen while it stays open. A collapsed card's tooltip is a quick read-only peek of the same details. An expanded card has its own editable pickers for the client, the model, and the reasoning level, and it answers its agent the conversation's way. Expanded cards stay expanded after a restart, remembered for each workflow on this machine only. The owner chose this on 2026-10-09 in issue [#94](https://github.com/Mano-Liaoyan/iDevelop/issues/94), from three working prototypes: option A, unfold in place, with option B's hover peek. They asked for it because "each node on the canvas should show more content".

The workspace and execution plan that the owner approved on 2026-10-06 said: "Expansion must preserve node positions and connections." It also said: "The current attempt's effective settings remain visible." This record refines the first line. The workflow's saved positions are preserved. Expanding a card never changes them, never marks the workflow unsaved, and never writes its file. Only the view moves cards aside, and the cards slide back when the card closes.

## How it works

- **The disclosure.** A card's chevron sits on its last line. The state glyph sits on the title's line, in the same 16 px column it always had, so the title keeps its full width. The chevron points right while the card is collapsed and down while it is expanded, as the sidebar's rows do. Expand All and Collapse All act on the cards of the shown workflow only, and a card that is added later starts collapsed. It is one glyph button on top of the zoom controls, since it changes only this machine's view, and its tooltip's first line names it. It shows with the minimap, which is taller than the four buttons, so the bottom row keeps its height, and the top row keeps its room for the breadcrumb's names ([ADR 0023](0023-the-canvas-keeps-336-px-and-its-controls-follow-its-own-size.md)). A compact or short canvas leaves it out of the zoom controls: there one expanded card fills the canvas, and each card's chevron still expands it. The canvas's right-click menu, the Add popover, also offers Expand All or Collapse All among its actions whenever the workflow has a card, so it can be reached at every canvas size. The owner chose this on 2026-10-11 in issue #94.
- **The expanded card.** The 64 px header stays as [ADR 0019](0019-a-planner-chooses-each-new-task-s-agent.md) describes it: the tile, the title, the client or the status, then the model and level. The state's tint stays on the header. Under it, three sections sit under hairlines:
  - **Now.** Its heading and text follow the node's state, as the card's status line names it:
    - Running: the agent's latest message.
    - Input required: the agent's question, or its last reply, with an answer box.
    - Completed: the latest result's first paragraph.
    - Stale: which part of its basis changed, in the state's warning colour, as the inspector says it once. When the engine's reason it cannot run names what to run first, the card shows that sentence here.
    - Failed: why it ended.
    - Waits: the nodes it starts after, each with its status. A listed node that waits for one node counts it, as its card does when the name does not fit, so the listed title keeps its room.
    - Otherwise, the task's first field.

    An approval and a planner's proposal point to the inspector, where the person answers them.
  - **Agent.** The client, model, and reasoning pickers sit in that order, each a full-width row. While the node runs or waits for the person, a line under them says that a change stops the node and makes it wait for the person's Run ([ADR 0034](0034-a-result-goes-stale-when-its-basis-changes.md)). Once the pickers name another agent than the latest result's, the line also names the one the latest result used, such as Codex · GPT-5.5 · high, so the current settings stay visible. A press on a picker stays with the picker, so it opens its list and never selects or drags the card. A wheel over a closed picker zooms the canvas, as it does over the rest of the card, and never changes the choice.
  - **Actions.** Open conversation is on the left. On the right is the node's Run, Stop while it runs, and Retry once it has ended, as the card menu and the inspector also offer them ([#83](https://github.com/Mano-Liaoyan/iDevelop/issues/83)). Run works as the inspector's Run ([ADR 0018](0018-every-ready-node-starts-at-once.md)), and when it would start nothing, the line under it gives the engine's reason, such as "Runs after "A". Run "A" first.", unless the Now section already says it.

  Every section's glyphs share one 16 px column under the kind tile. Everything else starts after that column and ends 12 px from the card's edge, under the state glyph. Text wraps whole and is never cut short. No place for GitHub issues, pull requests, or comments is shown until those features exist.
- **The peek.** A collapsed card's tooltip shows the title whole, the type and agent, the Now section's heading, text, and the tasks it waits for by their whole names, and why the task cannot start, if it cannot and the Now section does not say it already. It takes no input. A renamed or expanded card shows none.
- **Neighbours slide aside on screen only.** A column is the cards whose left edges lie within half a collapsed card of the column's first card. A column that holds a wider card moves every later column right by the extra 80 px. An expanded card then pushes down every card below it that lies under it where both show, in its column or not, such as a card set like a stair step, so each keeps its saved gap to the card above. The ports stay on the header's middle line, so a card that unfolds keeps its wires' rows, and wires route around each card where and as large as it shows. An open proposal's ghost cards and wires follow too: a fill sits on its task's card where it shows, and a new task moves as its planner shows, wider while the planner is expanded. A wire dropped from a card starts at its port where the card shows it, a drop or a double-click on an expanded card's body is on the card, not on empty canvas, and a task chosen in the sidebar comes into view as large as its card shows.
- **One way to answer.** The card reads its node through the same conversation that the conversation view opens. The answer box shares the conversation's draft, and Send or Ctrl+Enter sends it the conversation's way: a reply to a waiting node continues its session. Keys typed in the answer box, and on the pickers, stay with them and never reach the canvas's shortcuts.
- **Remembered on this machine.** The window's session file, beside the theme preference, lists each open workflow's expanded cards. The next start expands them again. The workflow's file never holds them, so team synchronization will not share one person's view.

## Considered options

- **Option B: the graph never moves, and pinned notes stand in a margin lane right of the graph.** The notes sat far from their cards, and at a 900 px window a card and its note never showed together. Its peek joined option A.
- **Option C: every expanded card is one size, with Now and Agent tabs, and the rows open like a spreadsheet's.** It hid the agent behind a tab, a long question had to scroll, and short states left empty space.
- **Expanded cards that overlap their neighbours, as D0's prototype drew them.** The owner judges by alignment and calm, and an overlap hides the neighbours.
- **A read-only agent line on the card, with changes left to the inspector.** The owner chose editable pickers.
- **Expand All beside Run Workflow and Generate in the top row.** Even as a glyph it cut the breadcrumb's workflow name at the 900×600 window, and with an open proposal at the 1280×800 window, which ADR 0023's rules forbid.
- **Remembering expansion in the workflow's file.** It is one person's view, and it would mark the workflow unsaved and travel with team synchronization.

## Consequences

- Expand All makes a graph much taller. The six-task sample fits a 1600×1000 window at about 63% zoom, where an expanded card's text is small.
- At the 900×600 window the canvas is about 340 px wide, so one expanded card fills it, and Expand All is not among the zoom controls there. The canvas's right-click menu offers it.
- The minimap still draws every card at its saved position and collapsed size. A new card's free spot, and the canvas's other placements, follow the saved positions, so the layout pushes a new card aside when it lands under an expanded one.
- A drag moves a card's saved position by as far as the pointer moves the card from where it shows. The layout then places the card again from its new saved position, so a card dragged while a neighbour is expanded can settle a little away from where it was dropped.

Source: issue [#94](https://github.com/Mano-Liaoyan/iDevelop/issues/94) and the owner's decisions of 2026-10-09.
