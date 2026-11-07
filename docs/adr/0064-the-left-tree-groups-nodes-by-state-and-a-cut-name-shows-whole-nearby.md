# The left tree groups nodes by state, and a cut name shows whole nearby

An expanded workflow in the left tree lists every node under a folding group for its state. A zoomed out card wraps its model name to a second line rather than cut it, and keeps a title's ellipsis at its end. The name tag shows the selected card's whole title where the card cuts it. The conversation's task strip shows the whole name of the node that gave the input. This amends ADR 0062 and ADR 0063.

Round 17 of the review found that a large workflow showed only 12 of its nodes until the person chose "Show all 50 nodes", that zoomed out cards cut model names such as "GPT-6.1 Sol" and "Qwen 4 Max", that a selected card at a fit cut its title with no way to read it on the canvas, that a cut in the middle of a title ("Move… revenue dashboard query") read as a rendering fault, that "2023-" and "Q4" broke across lines, and that the task strip cut the source node's name.

## How it works

- **Left tree.** An expanded workflow lists its nodes in four groups, in this order: "Needs you" (waiting, failed, and interrupted nodes), "Running", "Not started" (idle and stale nodes, which have no current result), and "Done". Each group that holds a node has a header with its name and its count. A click on the header, Enter, or Space folds or opens the group, and Right and Left open and fold it. "Done" starts folded when it holds more than 5 nodes, and every other group starts open. A fold that the person sets stays. Selecting a node opens its group, so the selected node always has its row. Inside a group the nodes keep reading order. Names still wrap in full (issue #108). This withdraws ADR 0063's 12-row limit and its "Show all N nodes" row.
- **Card model at a fit.** The card keeps its width, because a wider card would touch its neighbor at the 300-unit column step that new nodes and saved layouts use. At the Label tier, where a fit rests, a model name that does not fit on one line wraps to a second line at a space. The title and the agent's lines close up to 29 units, so a three-line title and a two-line model name both fit in the card.
- **Name tag.** At the Label and Title tiers, when the selected card's title ends in an ellipsis, the name tag shows the whole title in up to 260 px, wrapped. It stands under the card, over it, right of it, or left of it: the first place where it covers no other card. At the Block tier it still names the selected card. This extends ADR 0060's tag.
- **Zoomed out titles.** A zoomed out card's title is cut at its end only, and a hyphenated token such as "2023-Q4" stays on one line. This withdraws ADR 0062's cut in the shared words for the zoomed out card. The full card keeps it.
- **Task strip.** The "From" part after the hairline shows the first input node's whole name and wraps to more lines rather than cut it.

## Considered options

- **A wider card.** About 60 more units fit every model name in one line, but cards at a 300-unit step then touch.
- **A compact list mode, or a status filter.** Each adds a control. Groups that fold keep every node one click away without one.
- **A name tag that always stands under the card.** In a dense grid the gap under a card is too small, and the tag covered the next row.

## Consequences

- A small workflow shows one header over its nodes, such as "Not started 3".
- While the workflow runs, nodes move between groups as their states change.
- A zoomed out card with a three-line title and a two-line model name has no space between the two.
