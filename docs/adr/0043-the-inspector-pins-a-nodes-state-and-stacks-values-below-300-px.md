# The inspector pins a node's state and stacks values below 300 px

Superseded by [ADR 0044](0044-the-inspector-keeps-one-right-edge-and-a-value-beside-its-label-at-every-width.md).

The inspector puts what a node needs now in a pinned header, and it shows a node's model in its sections: Input, Task, Agent, and Output. At the 320 px default, values sit beside their labels. Only the 280 px minimum stacks them. This record supersedes [ADR 0021](0021-the-inspector-keeps-two-right-edges-and-stacks-values-below-440-px.md).

Round 0 of the UI review found that a node's status started 757 px down an 800 px window, that Open conversation took 71 Tab presses, and that the inspector showed no input at all. Each picker row was 56 px high, because the 320 px default stacked every value. The model is in [ADR 0031](0031-a-node-is-an-abstraction-of-an-agent.md): a node is an agent with input, instructions, an agent, and output.

## How it works

- **Pinned header.** The header does not scroll. It holds a 24 px tile, the title in up to two lines, the kind line, the status pill, the node's actions, and its state detail or the reason it cannot start. `NodeActions` gives the actions for each state, with the likeliest next step first. An action the row leaves out stays in More. While a proposal is open, the row has no primary button, because accepting is the one primary action.
- **Sections.** A Proposal section comes first while a proposal is open. Input lists each incoming connection. Output holds the results, the findings, and each outgoing connection. Run, Blueprint, and Talk to the Agent are gone: their status and actions are in the header, the blueprint's facts and commands are in the kind line and More, and the conversation view holds the only composer.
- **Columns.** The columns are a 14 px glyph, a 6 px gap, a 76 px label, the value, and a 24 px trailing slot. Values start 108 px from the panel's edge at every width from 300 px. Two right edges stay: actions end 12 px from the panel's edge, and boxes and paragraphs end 36 px from it.
- **Notes.** A row's note is in its label's tooltip and help text, so the trailing slot holds only a revert arrow, a state glyph, a count, or a row's actions.
- **Long values.** A closed picker ends a long name in an ellipsis. Its tooltip and its list, which opens at the box's own width and wraps each name, hold the whole name. A long text field rests at 88 px and scrolls inside, and with the focus it grows to 232 px.
- **Tab order.** The filter comes before the header in the tree, and the actions come first in the header, so the first action is two Tab stops from the filter.

## Considered options

- **Keeping the 440 px switch.** It keeps every model name whole in the closed picker, but five picker rows then take 280 px at the default width.
- **Keeping the Run section and moving it up.** Status and actions would still scroll away.
- **Keeping a composer in the inspector.** It repeats the conversation view in a narrower panel.

## Consequences

- A long model name is cut short in the closed picker at the default width.
- A long title shows two lines until the title has the focus. Its tooltip holds the rest.
- The filter's height on screen depends on the header's height, so it moves when the selection changes.
