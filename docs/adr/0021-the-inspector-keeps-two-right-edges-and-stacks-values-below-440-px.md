# The inspector keeps two right edges and stacks values below 440 px

Every inspector view lays its rows out on one grid, `InspectorGrid`, with two right edges. Glyph buttons and rows of buttons end 12 px from the panel's edge, and boxes and paragraphs end 64 px from it. A count is drawn to end where the glyphs' drawing ends. Below 440 px, every value moves under its label. This keeps the shape of layout A from the [D0 record](../handoffs/2026-10-06-d0-design-validation.md#layout-a-keeps-inspector-actions-visible), which the owner chose. It replaces that record's rule "Pickers stack below labels under 320 px" and its two-row-per-property narrow layout. The owner asked for it on 2026-10-09 in issue [#88](https://github.com/Mano-Liaoyan/iDevelop/issues/88).

In #88 the owner found that the Overview's counts did not line up with the Library's "…" buttons and the filter button. They also found that a task's rows wrapped differently, and text crammed together, as the inspector narrowed or widened. The owner is judging the 440 px switch now. If they reject it, the code and this record change together.

## How it works

- **Two right edges.** The trailing column, two 24 px slots 4 px apart, holds a row's revert and info glyphs, a blueprint's Place and More, or a kind's count. Its right edge is the action edge, which the header's More and the filter button share. These also run to the action edge, because buttons are actions:
  - rows of buttons, such as Derive and Save as Blueprint
  - section dividers
  - the hairlines between fields

  Everything else ends at the value edge, in front of the trailing column: pickers, text boxes, the filter box, segmented controls, report cards, the composer, and every paragraph and hint.
- **Counts end where the glyphs' drawing ends.** A 16 px glyph is drawn about 6 px inside its 24 px button's edge (`InspectorGrid.GlyphInkInset`). So an Overview count and a folded section's change count end that far inside the action edge. They draw through `InkAlignedText`, which shifts the text by the space its last glyph keeps clear on its right. So a "1" ends where a "4" ends, and where the "…" and the filter glyph end. The Library's glyphs are drawn at 16 px, as the header's More and the filter's are.
- **One label column and one line.** The label column keeps 88 px at every width. That fits "Conversation", the longest label beside a value, and keeps values beside their labels in a wide panel. Every row centres its glyph, label, short value, and trailing buttons on a 28 px first line. A label over its value sits on a 24 px line, 4 px above the value. A row without a glyph or a tile starts its label at the content edge, where its value starts once it moves under the label.
- **Values stack below 440 px.** The inspector decides from its own width, and every row inherits that, so they all switch at once and switch back when the panel widens. From 440 px, the value column holds a picker that shows the longest model name a client offers beside its label. Today that is Pi's "DeepSeek V4.1 Flash (deepseek)": about 190 px of text and 44 px of the picker's own. Below 440 px, which includes the 320 px default, every labelled value sits under its label, from the content edge to the value edge.
- **Text wraps instead of being cut short.** Titles, labels, values, statuses, activity, chips, notes, and picker choices wrap. A value that needs the full width sits under its label at every width. Examples are the last run's status, agent, and time, Recovery's sentences, and a segmented control. A header's kind line drops its separator where it breaks. The overlay scroll bar is 10 px wide, inside the 12 px inset, so it covers nothing. A card grows with its content, so the panel's own scroll is the only one.

## Considered options

- **One right edge, with revert and info placed between the label and the value, as Godot places them.** The label column would need about 130 px, so every picker would stack at the default width.
- **Keeping the switch at 320 px.** Real model names were cut short beside their labels at the default width, for example "Claude Sonnet 5…".
- **A label column that grows with the panel.** That was the earlier `2*`/`3*` split. At 520 px it pushed values 168 px away from their labels.
- **Aligning counts by their layout boxes.** Each glyph's drawing sits inset in its button, so the counts looked 4 to 7 px further right than the glyphs.

## Consequences

- At the 320 px default, the inspector shows one column: a label line above each value. The panel is taller than layout A's two columns were at that width.
- A picker choice still too long for the stacked width, such as a model name marked "(not ready)", wraps to a second line inside the picker.

Source: issue [#88](https://github.com/Mano-Liaoyan/iDevelop/issues/88), the owner's feedback of 2026-10-09, and pull request [#100](https://github.com/Mano-Liaoyan/iDevelop/pull/100), its Decisions.
