# English working language

## Request and changes

The user requested English as the working language throughout iDevelop.

- Added the shared language policy to `AGENTS.md`. Project discussions, agent responses, documentation, handoffs, code comments, interface text, and commit and pull request text default to English. Explicit task-specific language requests and source material that requires faithful preservation remain supported.
- Translated the README, including headings, examples, client guidance, limitations, and update instructions.
- Recorded the decision in `docs/context.md`. Existing handoffs and scripts were already in English.

The policy applies to this project through its shared agent instructions. No global settings, runtime code, model configuration, or upstream PStack files changed.

## Verification

- Scanned all tracked project files for Han characters. No matches remained. The upstream submodule was excluded as third-party source.
- Compared every PowerShell block and the set of Markdown link targets in the translated README against the previous commit. They matched exactly.
- Confirmed that `CLAUDE.md` and `GEMINI.md` import `AGENTS.md`.
- `node scripts/pstack.mjs check` passed for all 49 skills and the pinned upstream revision.
- `git diff --check` passed. No runtime tests were added for this documentation change.

## Next action

Continue project work in English. The next product task remains defining the first user workflow and its acceptance criteria before choosing a technology stack.
