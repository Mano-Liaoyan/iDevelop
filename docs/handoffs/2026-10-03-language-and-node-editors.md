# Language and node-editor alternatives

## Task

Broaden the framework comparison beyond Rust. The user likes egui but finds egui-snarl unsuitable or difficult, asks for other node editors, and explicitly accepts C++ and C#. Preserve permissive licensing, all three desktop platforms, solo use without a server, optional self-hosted synchronization, and local agents.

## Investigation checklist

- [x] Route through the **how** skill. For motivation questions, also route through the **why** skill. Used the simple explainer route for this options comparison. No separate history investigation was needed.
- [x] Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only investigation`.
- [x] Produce the `how`-shaped output (Overview / Key Concepts / How It Works / Where Things Live / Gotchas), or a recommendation with a tradeoffs table if the request is a decision between alternatives.
- [x] Apply the **unslop** skill to the reply.

## Decisions and evidence

Rust was a recommendation, not a user requirement. Egui-snarl is a separate library, not a required part of egui. Compare actual node-editor implementations before choosing an application language.

The recommendation now includes a complete C# application using Avalonia and BAndysc's NodifyAvalonia port, and a C++ application using Dear ImGui and thedmd's imgui-node-editor. An egui implementation remains possible with a different editor. No Rust backend is required for either non-Rust option. This is a shortlist, not an accepted implementation decision.

Avalonia, NodifyAvalonia, Dear ImGui, and imgui-node-editor publish MIT licenses. The exact license links, platform evidence, and limitations are recorded in the [product direction](../product-direction.md). Commercial use does not require purchasing Avalonia's optional products. Shipped dependencies and assets still need version-specific review.

NodifyAvalonia supplies graph interactions inside an Avalonia application. Its package name differs from `Nodify.Avalonia`, and the original Nodify targets WPF. Its inspected compatibility table names Avalonia 11.1.0, and it lists cutting lines as unsupported. Dear ImGui's editor offers a Blueprint example, but that sample uses an ImGui layout fork. Its accessibility and internationalization limitations matter for iDevelop's surrounding interface.

Other egui editors include `egui_node_editor`, `egui_node_graph2`, and `egui_nodes`. The inspected package manifests require different egui versions. A branch and a published crate can differ. Cached Docs.rs responses also differed, so no latest-version assertion is made for `egui_node_editor`.

The **Experience First** principle changed the comparison to include prompt editing, accessibility, and the surrounding product controls. The **Prove It Works** principle kept capabilities from source inspection separate from unperformed builds and benchmarks.

## Delegation

A fresh read-only explainer, `language_options`, checked the C# and C++ options under the project's PStack entrypoint. The coordinator checked the supplied sources, researched the egui alternatives, and made all document edits. No delegate wrote files or needed a separate worktree.

## Changed artifacts

- `docs/context.md` records that C# and C++ are acceptable and Rust is optional.
- `docs/product-direction.md` broadens the native comparison and removes assumed Rust backends from other language choices.
- This record preserves the evidence, limitations, and next action.

## Commands and observed results

- `Get-Content` read the project entrypoints, current design, and relevant handoffs.
- `rg -n 'Rust|egui|Flutter|language|framework|node.editor' docs/product-direction.md docs/context.md docs/provider-access.md` located language assumptions before editing.
- Browser source reads verified repository descriptions, package metadata, official platform documentation, and licenses. Direct `Invoke-WebRequest` attempts for three Rust manifests failed with transport authentication errors; browser reads supplied available evidence instead.
- A PowerShell scan of all three task documents passed for trailing whitespace and local Markdown link targets, including untracked files.
- `git diff --check` exited 0 with no findings.
- `git status --short` showed only the current research documents and the earlier conversation's uncommitted research documents. No application or setup files appeared.

No application code, dependencies, provider credentials, setup configuration, or historical handoff records were changed. No prototype, platform build, authentication test, or performance benchmark ran.

## Open issues and next action

Build bounded comparisons using the existing interaction acceptance cases before selecting a stack. Start with Avalonia and NodifyAvalonia for the whole product and Dear ImGui with imgui-node-editor for the Blueprint interaction. Retain an egui alternative if the user prefers its style. Verify the exact package combinations, English and Chinese input, keyboard behavior, accessibility, and all three platform builds. Solo operation, optional self-hosted synchronization, and local agent execution remain required in every candidate.
