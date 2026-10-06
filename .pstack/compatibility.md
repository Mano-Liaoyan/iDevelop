# Project-local PStack adapter

This is a local adaptation of Lauren Tan's official Cursor PStack, not an official cross-client port. `.pstack/upstream` is a Git submodule of `cursor/plugins`, and the parent repository's gitlink pins its revision. Sparse checkout exposes `pstack` and upstream root files. `scripts/pstack.mjs setup` initializes a missing submodule and generates the active skill tree from `.pstack/upstream/pstack` and from the project-owned skills in `skills/`. An existing checkout must match the gitlink in the index; stage an intentional upgrade before regenerating. Change the adapter, the submodule revision, or a project skill in `skills/` deliberately, then regenerate; do not hand-edit generated skills.

## Models

`.pstack/models.json` is the canonical version 3 model policy. The registry holds each model's identity, client verification, and `verifiedEfforts`. Each role holds an ordered array of `{ model, reasoningEffort }` assignments, where `model` is a registry key. Effort belongs to the assignment because one model can work at different efforts in different roles. Separate registry entries for each effort would duplicate identity and verification. Version 2 is no longer accepted.

`scripts/model-policy.mjs` validates the policy and resolves a role into a ready or blocked result. It does not launch clients or bridge provider subscriptions. Run `node scripts/model-policy.mjs resolve <role>` before delegation. Every assigned effort must be explicit, verified for its model, and within the policy ceiling. A pending model blocks every role that selects it, without blocking unrelated roles or substituting another model. A partly available panel returns all missing members in assignment order and no runnable participants.

The user selected these development roles on 2026-10-06. They replace the earlier all-Opus assignment.

| Policy role | Model and client | Effort |
| --- | --- | --- |
| `backend-implementation` | GPT-6.1 Sol through Codex | `high` |
| `frontend-implementation` | Claude Opus 5.5 through Claude Code | `high` |
| `backend-review` | Claude Opus 5.5 through Claude Code | `xhigh` |
| `frontend-review` | Claude Opus 5.5 through Claude Code | `xhigh` |
| `judgment` | GPT-6 Astra through Codex | `xhigh` |
| `difficult-task-review` | GPT-6 Astra through Codex | `xhigh` |
| `exploration` | GPT-6.1 Sol through Codex | `low` |

Each implementation role has one owner. Other roles can select an ordered panel of distinct models from their required provider. Gemini 3.8 Flash keeps its verified registry entry, but no role selects it. Changing a role's provider also changes `requiredRoles` in `scripts/model-policy.mjs` and its tests.

`reviewPolicy.crossProvider` is scoped. Backend review requires a different provider from its implementation owner. Frontend review intentionally shares Opus with frontend implementation. Every review still runs in a fresh session that did not write the change. The coordinator enforces session separation because the resolver has no session state. Astra's difficult-task review supplements the scope-specific review rather than replacing it. For backend work, Sol and Astra share OpenAI, while the Opus backend reviewer supplies the cross-provider review.

| PStack role | Canonical policy role |
| --- | --- |
| feature, refactoring; bug-fix; perf-issue; hillclimb | `frontend-implementation` or `backend-implementation`, based on scope |
| hardest tasks | Scope-specific implementation plus `difficult-task-review` |
| judgment and prose; how explainer; why synthesizer; reflect judgment, divergent, synthesizer | `judgment` |
| how explorer; why investigators; reflect tooling | `exploration` for read-only work; scope-specific implementation for edits |
| swarm workers | Resolve each worker's purpose as exploration, implementation, or scope-specific review |
| arena runners; architect runners | `judgment` for design panels; retain scope-specific implementation ownership for coding |
| arena cross-judge pool; interrogate reviewers | `frontend-review` or `backend-review`; exclude the authoring session and review in a fresh Opus session |

Every invocation must set the assignment's explicit reasoning effort at or below `xhigh`. The `large` budget sets a ceiling, not a uniform effort. Keep the user's `high` and `low` assignments. `max`, `ultra`, higher levels, and implicit effort are prohibited. Gemini's scale stops at `high`. These names are provider-specific settings, not equal token budgets. This policy overrides upstream skill defaults and fallback instructions. Never use `inherit-parent` or `auto` to bypass the effort ceiling or a missing role participant.

A registry entry's `requestedModel` records the user's intended model. It is not dispatchable until the entry has a verified executable `model`. `verifiedEfforts` records the supported efforts confirmed through the native catalog or successful client requests. A pending entry may have an empty list until verification completes.

On 2026-10-06, Codex's live `model/list` confirmed GPT-6.1 Sol and GPT-6 Astra with `low`, `medium`, `high`, and `xhigh` support. Claude Code requests with explicit `low`, `high`, and `xhigh` completed through the configured Bedrock route labelled Claude Opus 5.5. The user selected that configured route. These requests verify client access to the configured route, not an independent lookup of its backing model. Bedrock denied that lookup. Recheck the configured route when starting work on another machine. No personal configuration or account identifiers belong in this policy.

Gemini 3.8 Flash runs through Antigravity CLI (`agy`), which replaced Gemini CLI for individual Google accounts. `agy models` lists only combined IDs such as `gemini-3.8-flash-high`. A signed-in request with `--model gemini-3.8-flash --effort high` reached the backend as Gemini 3.8 Flash (High), and `--effort low` changed it to the Low variant. Use the bare ID with a separate `--effort`. Confirm the served variant from the backend label in `--log-file`, not from a success status. The `init` event of `--output-format stream-json` shows only the requested model ID. Version 1.1.8 silently fell back to a default model when a headless `--model` override failed to resolve ([issue 710](https://github.com/google-antigravity/antigravity-cli/issues/710)). Version 1.2.16 rejects an unknown ID with exit code 1. Check exact model availability and explicit effort controls through a supported client before activation. Do not infer account access from public documentation, a binary on PATH, or the presence of credentials. Set model IDs and effort as separate native fields. Do not invent combined model-effort slugs.

`.cursor/rules/pstack-models.mdc` points every workflow role to this policy and provides no inherited fallback. `.codex/config.toml` keeps project launch defaults at Astra with `xhigh`, matching judgment work. Backend implementation and exploration must pass their resolved Sol model and effort explicitly. Editing project policy does not change an already-running conversation or personal account settings. Verify the active setting when starting work. The role resolver validates prepared dispatches. It does not intercept arbitrary manual client use.

`/setup-pstack` updates project files only. It never changes personal account configuration or files in the user's home directory. Preserve explicit user choices and do not rewrite generated or upstream skills to change model policy.

## Tool mapping

- Cursor `Task` means the host's native subagent tool. If `poteto-agent` is not a registered type, give a fresh native delegate `.pstack/upstream/pstack/agents/poteto-agent.md`, `AGENTS.md`, and the active `poteto-mode` skill to read. Preserve specialized reviewer roles. Honor the host's concurrency limit.
- Worktrees isolate concurrent writers. Use the host's managed worktrees when supported, otherwise ordinary Git worktrees. Read-only workers can share a checkout. Read the full result and verify the diff before integrating.
- Cursor `TodoWrite`, `AskQuestion`, and browser controls map to the host's corresponding tools. If no todo tool exists, keep a concise task checklist in the task's handoff record, or in its pull request description when the task writes no record, including evidenced skips.
- Skill invocation uses the native mechanism where available. Otherwise read the named project's `SKILL.md` and execute it. Cross-skill references still mean the same local PStack skill, including user-invoked skills reached explicitly by the active workflow.
- `deslop`, `control-cli`, `control-ui`, and Cursor's `create-skill` are external dependencies, not bundled PStack skills. The catalog holds only the PStack skills and the project-owned skills in `skills/`. To keep external skills out of it, perform a direct diff cleanup, use the available terminal/browser harness, and validate skill frontmatter/links with a runnable check. On Windows, the project's `verify-idevelop` skill is the harness for the desktop app's real window. Record the substitution. Do not claim the external skill ran. If the real surface cannot be driven, report a blocked verification step.
- Cursor cloud scheduling, transcript lookup, and some automation playbooks are host-specific. Use only real capabilities of the active client. `recall`/`reflect` can use this project's handoff records and transcripts explicitly available to the session. Do not search unrelated projects' histories.
- Optional upstream orchestration/watch helpers require Bun and their own dependencies. Inspect their package files and install locally only when that workflow is needed. Bash helpers need Bash or WSL on Windows. No helper runtime is silently installed by setup.

Generated skills normalize frontmatter names to directory names and replace the upstream global model-rule path with the project path. Each generated entrypoint points here. Project skills go through the same rendering, so the ownership guard, the drift check, and the Codex audit treat them like PStack skills. A project skill that shares a PStack skill's name fails setup. Source behavior otherwise remains upstream, including progressive loading, playbooks, verification, independent review, and outcome-based completion. Host permission boundaries and the user's requested scope continue to apply.
