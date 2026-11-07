# The agent leads the inspector, and every name and count reads in words

The inspector's Agent group is the first group after the header in every node state, and Task follows it. Every name in the left tree shows in full. Every run bar chip says its group in words, and a short bar folds the less urgent groups into one "+N more" chip. This amends ADR 0053 and ADR 0057.

Round 11 of the review found that long instructions pushed the model picker under the fold, that the run bar showed bare counts beside badges, that the left tree cut node names, that Activity did not say which project a row belongs to, and that wires ran on the borders of zoomed-out cards.

## How it works

- **Inspector order.** Agent, Task, Proposal while one is open, Output, Input, and Goes to. A model change never needs a scroll past a long task. The workflow inspector shows the library and no state counts, because the run bar holds them.
- **Left tree.** A node name shows in full and wraps, also while selected, one type size under its workflow's name (issue #108).
- **Run bar.** A chip always says its group in words, as "2 waiting". A tight bar keeps the groups that need the person and folds the others into "+N more", whose tooltip names them. The tightest bar keeps the most urgent group and "+N more". Fit leaves the bar only with the progress, and Follow moves to the overflow menu only at the tightest step. A folded workflow row in the left tree folds its groups the same way.
- **Activity.** While several projects are open, each row has a quiet second line with its project and workflow.
- **Cards.** An idle card shows the empty ring that the lists show. The client letters of a zoomed-out card keep the agent's text size on screen.
- **Wires.** At the tiers where cards draw under their boxes, a detour keeps about 12 px on screen from a card, so it leaves a row gap that the drawn cards make too narrow and goes around.
- **Blueprint editor.** While a blueprint is open, the run bar's place names it and says to Save or Cancel in the inspector.

## Considered options

- **Output first, as ADR 0054 had it.** The open conversation already shows the result, and the model picker is the control a person changes most.
- **Bare counts on a tight bar.** A person had to learn each badge, and the bar changed form with its width.
- **More title lines at a far zoom.** A card at the Title tier holds two lines of 10 px text in the height that a saved layout's rows leave. A third line needs more room between rows, which an Arrange command would give, and the owner has not chosen one.

## Consequences

- Node rows differ in height in the left tree.
- At a far zoom, a long title still ends in an ellipsis. Its distinguishing words stay in view as ADR 0049 keeps them.
