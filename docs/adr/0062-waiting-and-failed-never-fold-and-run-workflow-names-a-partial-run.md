# Waiting and failed never fold, and Run Workflow names a partial run

A summary of node states never hides a group behind "+N more". Waiting and failed always keep their words, and a tight place shows each other group as its badge and its number. Run Workflow says in its label when it runs only part of the workflow. A cut title keeps the whole part that tells it from a similar title. This amends ADR 0060 and ADR 0061.

Round 15 of the review found that the run bar and the left tree folded running, interrupted, and even waiting groups into "+N more", so a person needed a tooltip to learn that nodes waited for them. It found that only the tooltip said that Run Workflow skips nodes with a current result, that the Continue and Retry explanation repeated the button names, and that "Move the account statement qu…" lost the words that set it apart.

## How it works

- **State summaries.** The run bar, a folded workflow row, and Activity show one chip for each group, most urgent first. Waiting and failed keep their words everywhere. As the bar narrows, a quiet group (stale) and then running and interrupted show only their badge and number. ADR 0063 withdraws this: every group keeps its word, and a tight bar takes its own row. The narrowest bar moves Zoom in, Zoom out, and then Next to the overflow menu. A folded workflow row with more than two groups shows the others as badge and number, and the row and Activity wrap their counts rather than cut them. "+N more" is gone.
- **Run Workflow.** Its label reads "Run Workflow" when it runs every node or nothing, and "Run 3 of 9" when nodes with a current result keep it. Run Workflow and Stop Workflow share one minimum width, so a run does not move the breadcrumb. This withdraws ADR 0061's fixed label. ADR 0063 withdraws this label: Run Workflow keeps one name, and the run bar names a partial run.
- **Continue and Retry.** An interrupted node's line says "Continue resumes the session. Retry starts a new attempt."
- **Titles.** When a title shares its first words with another title and does not fit, the cut falls in the shared words after the first word ("Move… account statement query"), if the whole differing part then fits. Otherwise the title is cut at its end.
- **Run bar place.** The run bar sits right after the zoom controls at every width.
- **Breadcrumb.** The project's name gives up its width before the workflow's.
- **Shown workflow.** While a node of the shown workflow holds the selection, the workflow row keeps no fill and takes a 2 px accent line on its leading edge.
- **One node name.** Beside an open inspector, the full conversation's header shows only the node's place, because the inspector names the node. The docked conversation keeps its name. ADR 0063 withdraws this: the header names the node in every layout.
- **Conversation input.** The folded task names its first source node: "From Plan the rollout +1".
- **Far model line.** A zoomed out card falls back from "Gemini 3.8" to "Gemini 3" before "Gemini". ADR 0063 withdraws this: a card shows the inspector's model name and ends it in an ellipsis.
- **Inspector.** Each section header has a hairline across the panel above it. The pinned header shows a title in up to two lines (three since ADR 0063).

## Considered options

- **Fold only into "+N more" with a tooltip.** A person had to hover to see that nodes waited.
- **Wider cards for long titles.** Saved layouts keep their positions, so wider cards would close the wire lanes.
- **Keep the run bar centred.** It sat in different places at different widths.

## Consequences

- At the narrowest canvas, Zoom in, Zoom out, and Next are in the overflow menu, and their keys still work.
