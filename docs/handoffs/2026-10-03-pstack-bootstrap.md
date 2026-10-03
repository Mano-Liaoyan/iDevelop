# PStack development environment bootstrap

## Task and scope

Initialize iDevelop as a public personal GitHub repository. Install PStack within the project. Prepare a common workflow for Codex, Pi, Claude Code, and Gemini CLI. Isolate skills without changing other projects. Record the future GUI direction without choosing its architecture.

## Decisions

- Use Lauren Tan's official `cursor/plugins/pstack` source at `23e4138daa01c42d4969f7a5465f82704e64f798`, version 0.15.6. Keep its 160 files byte-identical to their Git blobs and preserve its MIT license.
- Generate 49 active skills from the pinned source. Normalize names, localize the model-rule path, and point each entry to the explicit compatibility adapter. Claude/Cursor directory links share that tree; Gemini/Pi can read it directly.
- Favor model quality with editable per-client role configuration. Codex's initial implementation model is `gpt-6.1-sol`, judgment model `gpt-6-astra`, both at max reasoning. Other client catalogs are unverified and remain null until discovered.
- Keep stable context separate from one-file-per-task handoffs. Concurrent writers use separate worktrees. These files provide continuity, not a live cross-client orchestrator.
- Codex 0.160.0 ignores skill filters in the project configuration layer, even after trust is granted. Use session-only CLI overrides generated from actual discovered SKILL.md paths. Do not disable global skills to compensate.
- The user explicitly approved trusting only this project in Codex. The user configuration was checked against its prior contents; that trust entry is its only change.

## Artifacts

`AGENTS.md`, `CLAUDE.md`, and `GEMINI.md` route to the same workflow. `.pstack/models.json` owns model roles. `.pstack/compatibility.md` records host differences. `scripts/pstack.mjs` generates and checks skills; `scripts/codex-skills.mjs` reads Codex discovery; `scripts/agent.ps1` launches isolated client sessions. The README records supported behavior and limits. GitHub Actions checks fresh setup on Windows and Linux without provider credentials.

## Observed verification

- Compared all 160 vendor files to the pinned upstream Git blob IDs: zero mismatches.
- `node scripts/pstack.mjs setup` and `check`: 49 generated skills, source SHA-256 hashes, adapter output, shared links, and model configuration passed.
- `node scripts/pstack.mjs isolate-codex`: actual app-server discovery enabled exactly the 49 project PStack skills. A separate ordinary session in the parent directory retained its external skills.
- `codex --no-daemon debug prompt-input -c <generated override>`: actual model-visible catalog contained exactly those same 49 names.
- `scripts/agent.ps1 codex --version`: launcher completed its preflight and ran Codex 0.160.0.
- PowerShell parser accepted the launcher. Node syntax checks passed. Relative Markdown link inspection found only the upstream example `[...](url)` placeholder, not a missing real reference.
- Independent review found Windows npm-shim launching and an ineffective Claude setting. Both were corrected before publication. Claude's user-source exclusion is supplied by the launcher, not a global mutation.

## Limits and next action

Codex's desktop skill picker is not isolated. Use the project CLI launcher for the verified PStack-only session. Gemini management lists may still show disabled built-ins. Claude, Pi, and Gemini are not installed on this machine's PATH, so their runtime behavior and accounts have not been verified; their adapters follow official documentation. Pi's strict launcher disables extensions, including extension-provided subagents. Native workflow substitutions are documented, not silently treated as executed Cursor tools.

The next product task is to define the first user workflow and measurable acceptance criteria, then select a stack. Build a PStack verification skill when there is an actual application to drive. Do not treat setup checks as application tests.
