# PStack submodule migration

## Request and decisions

The user asked why Codex desktop's slash menu did not show PStack and requested a submodule or subtree to reduce upstream source clutter and simplify updates.

- Choose a submodule at `.pstack/upstream`. A subtree would keep upstream source as ordinary files in the main repository. The official upstream is the `cursor/plugins` monorepo, so sparse checkout exposes `pstack` and upstream root files while excluding other plugin directories.
- Preserve upstream commit `23e4138daa01c42d4969f7a5465f82704e64f798`. This migration does not upgrade PStack.
- Replace 160 copied vendor files and the separate SHA-256 manifest with Git's recorded submodule commit. Keep existing history intact.
- Generate from Git-tracked upstream files only. Normalize text line endings, retain binary bytes, and preserve the existing project adaptation.
- Validate the submodule against the main repository's index. Stage an intentional upgrade before generation; commit it after review and verification. Ordinary initialization never follows remote main automatically.
- Document Codex desktop's `@` skill menu and `$poteto-mode` invocation. The upstream `/poteto-mode` examples target Cursor. Invocation and per-project filtering are separate concerns.

## Completed checks

- Captured SHA-256 values for all 124 generated files before moving source. Every generated file remained byte-identical after migration, including when the temporary upstream checkout used Windows CRLF.
- Ran `setup` twice and `check` in a fresh plain clone with an initially absent submodule. Initialization selected the recorded commit and introduced no unstaged changes.
- Repeated initialization and checking in a fresh linked Git worktree. Its Git status remained clean.
- Seeded an obsolete generated skill and a stale nested reference. `check` rejected them; `setup` removed both.
- Removed a current generated entrypoint and left an empty obsolete directory. `setup` repaired the entrypoint and completed cleanup.
- Confirmed that setup preserved a foreign skill and refused to overwrite it. Dirty upstream source and unrecorded upstream revisions were rejected.
- Seeded an ignored upstream log. It stayed outside the generated skills and passed the tracked-source inventory check.
- Ran the real Codex discovery audit. The project CLI override enabled exactly 49 PStack skills, and a control session outside this project retained its other skills.
- Node syntax and Git whitespace checks passed. The temporary verification harness and baseline stay in ignored `.pstack/local/`.

An independent review identified the ignored-source-file and missing-entrypoint issues. Both were corrected and reproduced in disposable fixtures. The existing CI workflow exercises initialization twice and verification on Windows and Linux.

## Artifacts and continuation

`.gitmodules` and `.pstack/upstream` own the upstream URL and version. `scripts/pstack.mjs` initializes, generates, and verifies the local skill tree. README contains the invocation, upgrade, and normal synchronization commands. Compatibility and stable context now reference the submodule. The bootstrap handoff remains a historical record of the previous layout.

The user requested updating the existing published project; this change continues on its main branch. No global settings changed. No application or model-routing design changed.

Codex desktop per-project hiding remains unsupported by the verified configuration path. The CLI launcher supplies the verified isolation. The other client runtime limits remain those documented in the bootstrap handoff. Next product work is still to define the first GUI workflow and its observable acceptance criteria.

## Sources

- [Codex changelog, March 19, 2026](https://learn.chatgpt.com/docs/changelog), skills added to the composer `@` menu.
- [Git submodule](https://git-scm.com/docs/git-submodule), recorded commits and explicit remote updates.
- [Git sparse checkout](https://git-scm.com/docs/git-sparse-checkout), directory selection local to each checkout.
- [Git subtree](https://raw.githubusercontent.com/git/git/master/contrib/subtree/git-subtree.adoc), upstream files integrated into the main repository.
