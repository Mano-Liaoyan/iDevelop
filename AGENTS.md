# iDevelop

For engineering tasks, read `.pstack/compatibility.md`, `.pstack/models.json`, and `.agents/skills/poteto-mode/SKILL.md` in full, then select and follow its playbook. Read `docs/context.md` and relevant `docs/handoffs/` records before changing the project. If generated skills are missing, run `node scripts/pstack.mjs setup` first.

Use this project's PStack skills. Skill names resolve under `.agents/skills/<name>/SKILL.md`. The shared workflow is active by default for engineering work; the user can opt out for a task. Apply only the relevant leaf principles and references.

Delegate independent research, implementation, and review when the selected PStack playbook calls for them. Every delegate must read this file and the same PStack entrypoint. Give concurrent writers separate Git worktrees and branches, with an explicit scope and checkable result. Fresh delegates are the default. The coordinator reviews their evidence and integrates changes. Never assume another client's live conversation is shared memory.

At handoff, write `docs/handoffs/YYYY-MM-DD-<task>.md` with the task, decisions and reasons, changed artifacts, commands and observed results, open issues, and next action. Each task owns its own record. Only the coordinator updates `docs/context.md` for settled project facts. Keep credentials, private transcripts, and personal account configuration out of committed records.

This repository currently contains a development setup, not an application. The GUI and cross-provider orchestration remain undesigned. Avoid inventing product architecture while maintaining this setup. Run `node scripts/pstack.mjs check` for setup changes; run `node scripts/pstack.mjs audit-codex` to verify Codex skill isolation against its actual discovery API.
