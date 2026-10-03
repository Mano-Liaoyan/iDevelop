# iDevelop

English is the project's working language. Use English for project discussions and agent responses, documentation and handoffs, code comments and interface text, and commit and pull request text, even when the user's message is in another language. An explicit request for another language applies to that task. Preserve identifiers, source quotations, and required localized content when accuracy requires it.

For engineering tasks, read `.pstack/compatibility.md`, `.pstack/models.json`, and `.agents/skills/poteto-mode/SKILL.md` in full, then select and follow its playbook. Read `docs/context.md` and relevant `docs/handoffs/` records before changing the project. If generated skills are missing, run `node scripts/pstack.mjs setup` first.

Use this project's PStack skills. Skill names resolve under `.agents/skills/<name>/SKILL.md`. The shared workflow is active by default for engineering work; the user can opt out for a task. Apply only the relevant leaf principles and references.

Delegate independent research, implementation, and review when the selected PStack playbook calls for them. Every delegate must read this file and the same PStack entrypoint. Give concurrent writers separate Git worktrees and branches, with an explicit scope and checkable result. Fresh delegates are the default. The coordinator reviews their evidence and integrates changes. Never assume another client's live conversation is shared memory.

At handoff, write `docs/handoffs/YYYY-MM-DD-<task>.md` with the task, decisions and reasons, changed artifacts, commands and observed results, open issues, and next action. Each task owns its own record. Only the coordinator updates `docs/context.md` for settled project facts. Keep credentials, private transcripts, and personal account configuration out of committed records.

This repository contains the development setup and the first application slice, a C# and Avalonia desktop editor for one project's workflow. Agent execution, team synchronization, and cross-provider orchestration remain undesigned. Do not invent product architecture beyond `docs/product-direction.md` and the recorded handoffs. Run `node scripts/pstack.mjs check` for setup changes; run `node scripts/pstack.mjs audit-codex` to verify Codex skill isolation against its actual discovery API. For application changes, run `dotnet build -c Release`, `dotnet test -c Release`, and `node scripts/check-licenses.mjs`.
