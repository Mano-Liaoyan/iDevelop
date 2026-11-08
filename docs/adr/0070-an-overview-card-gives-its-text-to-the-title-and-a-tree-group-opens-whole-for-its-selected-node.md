# An overview card gives its text to the title, and a tree group opens whole for its selected node

A Title tier card does not show its model. A tree group that holds the selected or open node lists all its rows. The run band keeps one row because the zoom controls give way first. A folded workflow row shows one state chip. This amends ADR 0068 and ADR 0069.

ADR 0071 moves the tree groups to the Nodes section, shows the inspector title in full, and shows a workflow row's state chip at all times.

ADR 0072 makes the Retry in a failed node's inspector header a quiet action while the conversation shows the node's card.

Round 23 found that overview cards showed cut titles and cut model names, that a large Done group hid twenty nodes behind a "+N more" row, that the run band took two rows, and that workflow rows had different heights.

## How it works

- **Overview card.** A Title tier card (zoom 0.23 to 0.36) shows no model. Its last line keeps the client's badge and the state's badge. The title keeps up to three lines at the 9 px floor and the unique-ending rule of ADR 0069. The model shows from the Label tier up, in the card's tooltip, and in the inspector. This amends ADR 0068's agent's line.
- **Model at 100%.** At the Label tier the model's text is 24 canvas units, one step under the title's, and the line ends 52 units from the card's right edge. A name such as "GPT-6.1 Terra" fits whole.
- **Tree group.** A group that holds the selected or open node opens and lists all its rows, as a file tree does in VS Code and Linear. The tree scrolls the selected row into view. Other groups keep the fold rule: a group of more than 8 nodes, other than "Needs you", starts folded. The header still folds the whole group. This withdraws ADR 0068's "+N more" row.
- **Run band.** When the run bar does not fit beside the zoom controls, Fit, Zoom in, and Zoom out go to the overflow menu before the bar takes its own row. The zoom's value stays in the band. On its own row, the zoom controls drop the rule that ends them. Every count keeps its word (ADR 0069).
- **Workflow row.** A folded workflow row shows the most urgent state's badge and, while nodes need the person, the count of all of them on the name's first line, before the progress. The chip's tooltip and name list every state in the run bar's words. This replaces ADR 0068's counts that went under a long name.
- **Failed node.** The inspector header of a failed node keeps Retry as its primary action while the node's conversation is open, as an interrupted node keeps Continue.
- **Inspector title.** The header shows the title in up to two lines at every inspector height. The tooltip and the title box hold the whole title.
- **Fixed field.** A picker that the client does not let the person change, such as Skills for Antigravity CLI, shows its value in the muted text. The row's info glyph says why.
- **No project.** With no project open, the canvas holds the one Open folder button. The left panel lists the recent projects and has no Open folder row.

## Considered options

- **Keep the "+N more" row.** It hides the rows that a person browses, and its chevron state does not agree with its rows.
- **Cut the run bar's words.** ADR 0069 keeps every count's word.
- **Show every state chip inline.** On a narrow panel the chips leave the name too little room.

## Consequences

- A large group that holds the selection can fill the tree. The tree scrolls, and the pinned project and workflow rows keep the context.
- At the Title tier the card's last line has free room after the client's badge.
- A person who wants all the states of a folded workflow reads the chip's tooltip, the run bar, or the open tree.
