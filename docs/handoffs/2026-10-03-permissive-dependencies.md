# Permissive dependency preference

## Task

Revise the desktop framework shortlist to favor permissive dependencies suitable for a commercial product and company use without mandatory framework fees. Preserve the existing solo, team synchronization, and local-agent requirements.

## Investigation checklist

- [x] Route through the **how** skill. For motivation questions, also route through the **why** skill.
- [x] Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only investigation`.
- [x] Produce the `how`-shaped output (Overview / Key Concepts / How It Works / Where Things Live / Gotchas), or a recommendation with a tradeoffs table if the request is a decision between alternatives.
- [x] Apply the **unslop** skill to the reply.

This changes research documents only. No application dependencies are installed.

## Decisions, artifacts, and verification

The user wants dependencies suitable for company use and a possible commercial product without mandatory framework fees. Prefer standard permissive licenses such as MIT, Apache-2.0, and BSD. Preserve proprietary distribution as an option. This does not select iDevelop's own license.

Removed Qt Quick and Slint from the preferred prototype shortlist. Both have no-fee licensing routes, so their exclusion reflects the user's preference for standard permissive terms, not a claim that all commercial use requires payment.

The revised comparison is Rust with egui and egui-snarl against Flutter with a Rust engine. The first supports a Rust application stack and an existing node editor. Flutter is the comparison candidate for the surrounding desktop interface. Iced remains a reserve candidate with an unresolved accessibility concern. No performance ranking or framework selection is final.

Official upstream licenses and package declarations were checked on 2026-10-03. Egui and egui-snarl declare MIT or Apache-2.0. Iced uses MIT. Flutter uses BSD-3-Clause. Yrs and Automerge publish MIT licenses. Sources are linked in `docs/product-direction.md`. These findings do not certify every transitive dependency, font, icon, plugin, or bundled binary.

Changed artifacts:

- `docs/context.md` records the new licensing preference and revised next step.
- `docs/product-direction.md` replaces the native shortlist and adds the dependency licensing preference.
- This handoff records the follow-up. The previous product-direction handoff remains a historical account of the earlier recommendation.

A fresh read-only `gpt-6-astra` delegate at the configured max reasoning level independently synthesized the narrower recommendation. It confirmed the distinction between no mandatory fees and no license obligations. Experience First keeps the eventual UI choice tied to observed interaction quality. Prove It Works limits the current claims to inspected license texts and documentation.

Verification:

- A PowerShell scan passed for trailing whitespace and local Markdown links in the changed documents.
- `git diff --check` passed for tracked changes.
- A targeted `rg` scan confirmed the current design and project context identify the permissive shortlist and explicitly supersede Qt and Slint.
- Git status retained the earlier research artifacts and showed only documentation changes. No dependency, runtime, setup, credential, or Git configuration changed.

No runtime test, license audit of a complete build, commit, or deployment ran. Setup and skill-discovery checks were not repeated because their inputs did not change.

## Next action

Prototype Rust with egui and compare it with Flutter using the existing canvas, IME, accessibility, solo-mode, and shared-workflow acceptance cases. Check the exact shipped dependency versions before adopting the production stack. Preserve license notices and applicable attribution. Provider usage and optional hosting costs remain separate from software-library licensing.
