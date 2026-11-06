# Wires curve and show execution, and a node kind stays quiet

A wire is a curve that shows what happens on it, and only execution state is loud on the canvas. Fit stops at a zoom where titles read, and the Agent section comes first in the inspector. This record supersedes the color part of [ADR 0033](0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md) and, in [ADR 0044](0044-the-inspector-keeps-one-right-edge-and-a-value-beside-its-label-at-every-width.md), the section order, the place of a stacked row's revert arrow, the two-line header, and the long text rule.

Round 2 of the UI review found that orthogonal wires shared trunks, so a person could not see which source fed which target. A completed Merge node looked like a failure. The states that need a person read alike. At Fit, a large workflow cut every title to a few characters. The coordinator of the review set the decisions below on 2026-11-06. They change only what the app shows: workflow files and the engine stay the same.

## How it works

- **Wires.** A wire is a curve, as in Blender and Unreal. Its orthogonal route stays as the plan that says which gaps it takes past the cards, and each up or down run of the route becomes an S curve. Where the curve would touch a card, it keeps closer to the route.
- **No trunks.** Every wire on a run takes its own lane, also wires from one output or into one input. Wires that share a port leave it or reach it at their own angles. A wire at rest has no arrowhead.
- **Wire color.** A wire's color shows execution state, not a node's kind. It is quiet while idle or complete. The wire into a running node is thick, blue, glowing, and carries moving arrows. An outdated wire is amber and a stopped wire red. A pin takes no color of its own.
- **Node kind.** A kind's hue stays quiet on a healthy node: a tile, a 10% band, and a thin stripe. Merge is olive, because no Apple color sits clear of every state's hue. Red belongs only to failure.
- **Loudness.** Running is the loudest state and the only one that moves. Then come Failed and what waits for the person, then Stale, Completed, and Idle.
- **What waits.** A question or reply is orange, an approval purple, and an interruption gray with a pause, in the ring, the tint, and the badge. Each badge is one disc with one bold glyph. The run bar shows the same badges with their counts, so it is also their legend.
- **Selection and focus.** A selected card has one line on its edge. While the canvas has the keyboard's focus, the selected card also takes the app's one focus ring. Every focusable control shows that ring, pickers and text boxes too.
- **Zoom.** A new tier from 60% keeps the tile, the title, and the status line. At 100% a title takes two lines.
- **Fit.** Fit stops at 40%, where a title still reads in two lines. A larger workflow stays around the selected node, else a running node, else a node that needs the person, else its start. The view stops at the graph's edge, and the rest is for pan and the minimap.
- **Titles.** When titles share a start, a cut title keeps the word that differs, such as "Backfill ledger… 2025-Q1".
- **Inspector.** The order is Agent, Input, Output, Task, after Proposal while a proposal is open. Status is a plain line with a glyph, and actions have their own row. A revert arrow takes the glyph's place in every row.
- **Long text.** One pattern holds for each long field: it fits its text and rests at six lines with a More control. The focus opens it. No fade covers the inspector, because a fade makes live controls look disabled.
- **System prompt.** A node's row shows the prompt with the node's own values, as Markdown. Its template stays in the blueprint editor.

## Considered options

- **Keeping orthogonal wires with lanes.** Lanes separate parallel runs, but wires still merge at each port and look rougher than the references.
- **Keeping each wire in its source kind's hue.** It says who sent the wire, but it hides the active path and makes Merge look like a failure.
- **Stacking every wire behind cards, as Blender does.** It is simple, but a wire along a row of cards would vanish, so the route stays as a plan.
- **Letting Fit go as low as the graph needs.** The whole graph shows, but no title reads.
- **Growing a long field with no limit.** It is one rule, but a long System prompt pushes Input and Output out of view.

## Consequences

- A cropped view can leave cards under the floating controls after a pan. Fit and Find stop at the graph's edge to reduce this.
- A model name on a full card can end in an ellipsis, and its tooltip holds it whole. What a node waits for takes that whole line.
- Run Workflow leaves the bar while a node runs, because Stop All takes its place.
- The run bar's chips replace its progress bar, and their tooltips hold the words.
