# One count on every surface, one line for each left row, and a running node fills its band

Every surface now gives one number for one group of nodes. A workflow name in the left tree shows in full, and a node row takes at most two lines. Run Workflow keeps its words at every width and has no count. A running node fills its title band. This keeps the workflow name rule of issue #108 and of ADR 0046, caps node rows at two lines, and replaces the Run Workflow reach of ADR 0052.

Round 6 of the review found that the left row said 3 waiting while the run bar said 2, that a node with a long name took four lines, that the run control lost its name beside a fraction that looked like progress, that a running card looked like a selected card, and that the cards at Fit cut their titles in an eight-node workflow.

## How it works

- **One count.** `StateCounts` is the one definition. Waiting holds the nodes that need the person's word. Interrupted holds the nodes that iDevelop's closing stopped, because those wait for nobody's word. The left row, Activity, the folded project row, the run bar, and the inspector overview read this definition. The left row's groups are the run bar's groups.
- **Run Workflow.** The button reads "Run Workflow" at every window size and panel width, and the breadcrumb gives up its width first. It shows no count. Its tooltip says what it runs. With nothing to run it keeps its words in a quiet look, and its tooltip says why. Stop Workflow also keeps its words.
- **Progress.** One format, "5/9 done", shows at every bar width.
- **A tight run bar.** It first sums the reasons to wait, then moves its view controls to the overflow menu, and then names what needs the person and sums the rest in one "+3" chip, whose tooltip names them. Zoom stays in the bar. A chip never shows a bare count.
- **A running node.** Its title band fills with color at every zoom, and its title takes the color's contrast. No other state and no selection fills the band, so a running node is the loudest item on the canvas also with no selection.
- **Selection.** The ring sits outside the card, so a state's edge stays whole inside it.
- **Fit.** A fit keeps 8 px clear of the editor's edges and not 24 px, and it counts the height that the cards draw at the zoom. From 36% a far card takes three title lines, so an eight-node workflow reads its titles at 1280 by 800. Saved positions do not change.
- **Wires.** A detour takes the middle of the gap that the drawn cards leave. A curve keeps 14 canvas units from a card's top and bottom where a shorter reach allows it.
- **Left rows.** A workflow name shows in full and wraps at every panel width. A node row takes at most two lines, also while selected, and ends in an ellipsis. Its tooltip holds the whole name. A project name still wraps. A folded workflow row adds one line with the two most urgent groups and a "+N" chip, each with a quiet dot. An open workflow's node rows show the states one by one, so its row has no such line. The row no longer shows the running node's command, because Activity and the conversation show it.
- **Activity.** Rows use the card's badge, and the short list shows three rows. A row's second line says where the node is, with the project while several are open. The reason takes the state's color, so Approval has one color.
- **Heading position.** Activity and Projects start at one left edge, and Activity's chevron follows its word.
- **Empty left area.** It shows a heading, one sentence, and Open folder.
- **Find.** The key hint sits beside the result count, under the box.

## Considered options

- **One line for every left row.** It breaks issue #108: the owner asked for workflow names in full.
- **Node names in full.** A long name took four lines in the panel that exists to scan nodes. Two lines hold most of a name, the tooltip keeps all of it, and Find searches it.
- **Keeping interrupted in waiting.** The run bar already held it apart, and the difference is what a person acts on.
- **Taking a blue ring for a running node and a new color for selection.** The tokens are pinned, and a ring is still a ring. A filled band differs in form, so it also reads at a low zoom.
- **Disabling Run Workflow when nothing can run.** The command stays as it is, so behavior does not change. Only its look says that nothing waits for it.

## Consequences

- A long workflow name makes its row taller, so workflow rows differ in height. Issue #108 asks for this.
- A node name over two lines loses its end in the row. This is the owner's to reverse.
- The third title line takes 22 canvas units from each row gap at the far tier, so a wire passes closer to the cards there. An Arrange command would give more room, and the owner has not chosen one.
