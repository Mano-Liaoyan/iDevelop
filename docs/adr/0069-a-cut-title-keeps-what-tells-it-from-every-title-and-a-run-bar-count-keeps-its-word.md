# A cut title keeps what tells it from every title, and a run bar count keeps its word

A card title that does not fit keeps the words that tell it from every other title in the workflow, not only from a title with a shared start. Every run bar count keeps its word at every width. A folded tree group's chevron points down while the group lists the selected row. A docked conversation frames its node's branch at a zoom where titles read. A cut model name keeps its last word. This amends ADR 0065, ADR 0066, and ADR 0068.

ADR 0070 amends the run bar's steps: the zoom controls give way before the bar takes its own row.

Round 22 found that overview titles such as "Mirror the…" and "Create the…" read alike, that a running workflow's run bar showed bare numbers beside badges, that a folded Done group pointed its chevron right while it listed a row, that the docked conversation left a 50-node workflow at 36% with title fragments, that "GPT-6.1 Sol" and "GPT-6.1 Terra" both read "GPT-6.1…", that spread wires ran along card tops, and that the hover line was hard to see.

## How it works

- **Cut titles.** `TitleParts` gives each title a `TitleKey`: the fewest first words that no other title in the workflow starts with, the fewest last words that no other title ends with, and the shortest run of inner words that no other title holds. When the whole title does not fit, `EllipsisText` shows the first of these that fits: the title's start cut with an ellipsis, if the start holds the first words that differ; an ellipsis and the end, with as many words before the differing last words as fit, as "…partition 2024-Q3"; or the differing inner words between two ellipses, as "…the refund…". Only when none fits does the title end in a plain cut. Every shown title of the review fixture's 50-node workflow is unique at each tier from the full card to the Block tier's entry. This replaces ADR 0065's rule that only a shared start of three or more words keeps the end.
- **Run bar words.** No step of the run bar takes a count's word. When the words do not fit beside the zoom controls after the view controls, Follow, and Next's long name give way, the bar takes its own row, and then wraps its groups to a second line. This withdraws ADR 0065's badge-and-count chips.
- **Group chevron.** A group header's chevron points down while any row of the group shows under it, the selected row of a folded group included, and right only when no row shows. The "+N more" row takes the node rows' text size, so it reads as a row of the group.
- **Docked branch.** When the conversation docks under a view whose zoom is under the readable zoom (90% on the bar), the canvas frames the node and its direct predecessors and successors closer, up to 100%. A refit that the dock's resize posted gives way to that frame. Otherwise the ADR 0065 rule stays: a view that shows the branch stays, and a frame never zooms closer than the view was.
- **Model names.** A zoomed out card's model name that does not fit keeps its last word after an ellipsis, as "GPT… Terra", when at least three characters of its start fit. Otherwise it ends in the ellipsis. This amends ADR 0066's end cut.
- **Wire lanes.** A wire's lane keeps 12 canvas units from a card's top and bottom edges, times the tier's wire scale. A lane that would come closer leaves the wire where the router put it.
- **Hover.** The hover line keeps its one screen pixel, in the full muted text color.

## Considered options

- **A legend for the run bar's badges.** It adds an element and still asks the person to decode the badges.
- **Unique text by character cuts.** A cut inside a word reads as a typing error and still collides at the smallest sizes.

## Consequences

- At the Block tier past its entry zoom, a title may still end in a plain cut that another title shares. The tooltip and the inspector keep the whole title.
- A run bar on a narrow canvas can take two lines above the zoom controls.
