# The canvas keeps 336 px, and its controls follow its own size

The canvas never gets narrower than 336 px, however wide the sidebar and the inspector are. The controls that float over it decide from the canvas's own size. Below 640 px, its buttons show only their glyphs and the minimap hides. Its top row and its bottom row each keep their controls apart, and the run bar never leaves the bottom edge. The conversation view's header, run strip, and composer move their controls under their text rather than cut either short. iDevelop's artifact instructions in a run task's first message fold into a one-line note. The owner asked for this on 2026-10-09 in issue [#97](https://github.com/Mano-Liaoyan/iDevelop/issues/97), with the feedback that the layout broke at narrow and wide widths and should feel aligned, symmetric, and calm.

In #97 the owner found that, at a narrow canvas, Generate sat on the breadcrumb and the waiting pill slid under it. Run Workflow and Review Proposal were pushed out of view. The run bar stacked above the minimap, and the two hid the lower cards. Ghost cards kept their size when the canvas zoomed out. With the 900 px window and the inspector at its widest, the canvas was 138 px wide. In the conversation view, the attempt picker and the Send button were cut off, and the first message showed the artifact instructions as one dense block. The owner is judging these rules now. If they reject one, the code and this record change together.

## How it works

- **The canvas keeps 336 px.** The splitters stop where the canvas would get narrower. A narrower window takes the width back from the inspector first, down to its 280 px minimum, and then from the sidebar, down to 200 px. A wider window gives each panel back the width the person chose. At the 900 px minimum window, 336 px keeps both panels at their default widths, and the inspector widens to 322 px at most. Above the docked conversation, the canvas keeps 280 px of height in the same way.
- **One width decides compact.** The canvas is compact below 640 px, which a 1280 px window with both panels at their default widths stays above. Run Workflow, Generate in each of its states, Resume, Stop Workflow, and a task's Cancel then show only their glyphs, in 32 px squares. An open proposal's glyph carries its accent dot. The first line of each such button's tooltip names it. The breadcrumb shows only the workflow's name, without the project's name or "Unsaved changes", which the sidebar shows. The minimap hides.
- **The top row.** The breadcrumb sits at the left and Run Workflow and Generate at the right, 12 px from the canvas's edges, all centred on one 36 px line. The breadcrumb gives up width first, its project's name before its workflow's name, and a cut name keeps its tooltip. The waiting pill centres on the canvas and moves aside only as far as it must to keep 12 px from both neighbours. When the row has no room for it, it sits 8 px under the buttons, its right edge on theirs. The status sits under the breadcrumb and never under the pill.
- **The bottom row.** The zoom controls sit at the left and the minimap at the right, and every bottom control ends 12 px above the canvas's bottom edge. The run bars centre on the canvas and move aside only as far as they must to keep 12 px from either corner. They never get wider than the room between the corners, and their activity wraps inside. They never move up. The minimap also hides on a canvas shorter than 480 px, as with the conversation docked under it.
- **Cards stay clear.** Fit to view keeps the cards 24 px below the lowest control of the top row and 24 px above the run bars. Ghost cards and ghost wires take the canvas's zoom as the cards do. The card that starts an empty workflow narrows with the canvas and keeps 24 px from its sides.
- **Conversation rows move controls under their text.** In the header, the status and the attempt picker sit beside the title while it keeps 160 px, and otherwise under it, from the title's start. The layout and close buttons stay at the first line's end, and a long attempt wraps inside its picker. In the run strip, Resume and Stop Workflow move under the status when it would get less than 200 px. In the composer, the hint takes its own line above the buttons when it would get less than 200 px. The buttons end where the composer's box ends and wrap onto further lines, with Send last.
- **Artifact instructions fold.** A run task's first prompt ends with iDevelop's instruction to declare artifacts in the attempt's outbox. The message shows the task's own text and, under it, the line "Artifact instructions from iDevelop", which shows the instruction on a click. The prompt the agent receives does not change.

## Considered options

- **Moving the run bar above the corners on a narrow canvas.** This was the earlier rule. It stacked the bar over the minimap, and together they hid the lower cards.
- **Putting Run Workflow and Generate behind a "…" menu on a narrow canvas.** The workflow's main commands would need two clicks.
- **Collapsing the canvas's buttons when the breadcrumb would be cut.** The top row would change with each workflow's name. A fixed width gives each canvas size one look. The conversation's rows decide by their content instead, because they wrap like text.
- **Letting the panels take the canvas freely.** At the 900 px window, the canvas got 138 px.

## Consequences

- At the 900 px window, the inspector widens to 322 px instead of 520 px. Its values stack under their labels there, as [ADR 0021](0021-the-inspector-keeps-two-right-edges-and-stacks-values-below-440-px.md) describes.
- A compact canvas's breadcrumb leaves out the project's name.
- A fit zooms out further while a run bar shows.
- NodifyAvalonia 6.6.0 undoes the editor's zoom on each decorator, so the ghosts drop that transform. A Nodify upgrade should retest it.

Source: issue [#97](https://github.com/Mano-Liaoyan/iDevelop/issues/97) and its comments, and the owner's feedback of 2026-10-09.
