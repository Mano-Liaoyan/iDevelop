# Project-local PStack adapter

This is a local adaptation of Lauren Tan's official Cursor PStack, not an official cross-client port. The immutable source is `vendor/pstack`; `.pstack/upstream.json` pins its revision and file hashes. `scripts/pstack.mjs` generates the active skill tree from it. Change the adapter or pinned source deliberately, then regenerate; do not hand-edit generated skills.

## Models

`.pstack/models.json` is the editable source for this project's role choices. It is instruction data, not a cross-provider execution engine. Use the current client's block. The implementation role covers feature/refactoring, bug-fix, perf-issue, hillclimb, how explorer, why investigators, reflect tooling, and swarm workers. Judgment covers prose, hardest tasks, how explainer, why synthesizer, and reflect judgment/divergent/synthesizer. Reviewer entries supply arena, architect, and interrogate panels and the arena cross-judge pool.

Check selected IDs and reasoning levels against the current host before dispatch. Codex's initial IDs were available at setup. Set the model and reasoning effort as separate native fields; do not invent Cursor-style combined IDs. Preserve explicit user edits. A null client block means its catalog has not been inspected: discover its available models and establish quality-first roles before the first delegation. If a host cannot vary subagent models or expose multiple providers, report that limitation and use its strongest available model for independent passes. Do not call same-family reviewers a cross-provider review. Model changes need no skill edits.

`.cursor/rules/pstack-models.mdc` supplies safe portable aliases for upstream parsers. The current client's explicit JSON choices take precedence. `/setup-pstack` updates project files only, including the relevant JSON block. It never writes to the user's home directory.

## Tool mapping

- Cursor `Task` means the host's native subagent tool. If `poteto-agent` is not a registered type, give a fresh native delegate `vendor/pstack/agents/poteto-agent.md`, `AGENTS.md`, and the active `poteto-mode` skill to read. Preserve specialized reviewer roles. Honor the host's concurrency limit.
- Worktrees isolate concurrent writers. Use the host's managed worktrees when supported, otherwise ordinary Git worktrees. Read-only workers can share a checkout. Read the full result and verify the diff before integrating.
- Cursor `TodoWrite`, `AskQuestion`, and browser controls map to the host's corresponding tools. If no todo tool exists, keep a concise task checklist in the task's handoff document, including evidenced skips.
- Skill invocation uses the native mechanism where available. Otherwise read the named project's `SKILL.md` and execute it. Cross-skill references still mean the same local PStack skill, including user-invoked skills reached explicitly by the active workflow.
- `deslop`, `control-cli`, `control-ui`, and Cursor's `create-skill` are external dependencies, not bundled PStack skills. To preserve the PStack-only catalog, perform a direct diff cleanup, use the available terminal/browser harness, and validate skill frontmatter/links with a runnable check. Record the substitution. Do not claim the external skill ran. If the real surface cannot be driven, report a blocked verification step.
- Cursor cloud scheduling, transcript lookup, and some automation playbooks are host-specific. Use only real capabilities of the active client. `recall`/`reflect` can use this project's handoff records and transcripts explicitly available to the session. Do not search unrelated projects' histories.
- Optional upstream orchestration/watch helpers require Bun and their own dependencies. Inspect their package files and install locally only when that workflow is needed. Bash helpers need Bash or WSL on Windows. No helper runtime is silently installed by setup.

Generated skills normalize frontmatter names to directory names and replace the upstream global model-rule path with the project path. Each generated entrypoint points here. Source behavior otherwise remains upstream, including progressive loading, playbooks, verification, independent review, and outcome-based completion. Host permission boundaries and the user's requested scope continue to apply.
