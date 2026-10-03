# Cross-provider model policy

## Task

Apply the user's explicit development model assignments. GPT-6 Astra owns backend implementation. Claude Opus 5.5 owns the Avalonia UI. Astra and Opus jointly review judgment and difficult tasks. Gemini 3.8 Flash and Astra review frontend work. Opus reviews Astra's backend work. No task may use max or a higher reasoning level.

## Feature checklist

- [x] `how` over the affected subsystem. Read the model policy, adapter, launcher, setup validator, and bootstrap record. The current setup selects roles through instructions rather than a cross-provider dispatcher.
- [ ] `architect` for parallel design exploration. Deferred: the required Opus participant is unavailable. Scope is a model registry and explicit role lists with one validation boundary, prepared as a local draft without a cross-provider design verdict. Do not replace Opus with another GPT reviewer.
- [x] Write the throughput checkpoint as four todo items.
  - [x] **Blocking first steps.** Verify native model choices and vendor-documented external model names. Preserve unverified external choices as pending.
  - [x] **Independent workstreams.** The coordinator owns configuration and documentation. An Astra delegate owns the validator and behavioral tests in a separate worktree.
  - [x] **Shared mutable state.** Use disjoint file scopes and integrate the delegate's patch sequentially.
  - [x] **Smallest safe decomposition.** One backend implementation owner is sufficient for the policy checker. No application UI work is involved.
- [x] Delegate code-writing to a subagent using the configured feature model with a specific scope and success criteria. An Astra delegate at explicit xhigh owns only the resolver, tests, setup-check integration, and CI checks in a managed worktree.
- [x] Verify on the matching surface. All 59 policy tests, script syntax checks, the full PStack setup check, and actual Codex skill discovery passed. A setup check is not provider authentication or a cross-provider review.
- [x] Rebase into small, ordered commits: publication skipped. Preserve the local changes until the required external review can run.
- [x] If the design is contested, `interrogate` before shipping: not run. No shipping is part of this configuration task, and required Opus review remains outstanding.
- [x] **Opening a PR**: skipped. No PR or external publication was requested; required Opus review is unavailable.

## Evidence and decisions

The native Codex subagent catalog exposes `gpt-6-astra` at `xhigh`. `Get-Command` found Codex and Node, but no Claude Code, Gemini CLI, or Pi on PATH. No provider credentials were read.

Official documentation names `claude-opus-5-5` and supports `xhigh` effort. Google documents `gemini-3.8-flash` and low, medium, and high thinking levels. Use xhigh for Astra and the requested Opus route, and high for Gemini. These labels are provider-specific, not equal token budgets. [Claude model configuration](https://code.claude.com/docs/en/model-config), [Gemini reasoning levels](https://ai.google.dev/gemini-api/docs/latest-model?hl=en)

External model names are recorded as requested choices. Their executable `model` fields remain null until the client, account access, and explicit effort setting are verified. Missing participants block the complete role. A backend implementation route can resolve independently while its required review remains unavailable. It cannot be reported as cross-reviewed or ready to ship.

The model IDs live in configuration, rather than being duplicated as constants in the validator. The validator enforces the user's provider ownership and review requirements. Applying PStack's Encode Lessons in Structure and Boundary Discipline principles, it rejects prohibited reasoning and incomplete or invalid assignments at the configuration boundary. It does not enforce arbitrary manual client invocations.

## Changed artifacts and verification

- `.pstack/models.json`: version 2 model registry, scoped roles, explicit effort ceiling, and required cross-provider review policy.
- `.codex/config.toml`: Astra/xhigh defaults for new project launches; explicit app or CLI overrides remain possible.
- `.cursor/rules/pstack-models.mdc` and `.pstack/compatibility.md`: workflow role mapping, mandatory resolution before dispatch, no inherited fallback, and blocked incomplete panels.
- `scripts/model-policy.mjs`: pure configuration validation and read-only role resolution. This does not launch clients or authenticate providers.
- `scripts/model-policy.test.mjs`: behavioral tests for ready and blocked routes, forbidden effort values, incomplete panels, same-provider reviews, and invalid configuration.
- `scripts/pstack.mjs` and `.github/workflows/pstack.yml`: setup validation uses the new policy; Windows and Linux CI run the tests and syntax checks.
- `README.md`, `docs/context.md`, and this record: assignments, activation limits, evidence, and next action.

The Astra delegate used branch `codex/pstack-model-policy` in the managed `pstack-model-policy` worktree and committed only the four scoped code/CI files as `3bd81605c8e8822889854e6d258d02a8b30c363b`. The coordinator inspected those changes and applied their patch to the primary checkout. The managed worktree was then archived with a recoverable snapshot. Existing product research, context edits, and other task handoffs were preserved. The integrated primary changes remain uncommitted pending the required Opus review.

Observed verification on the primary checkout:

| Command or check | Result |
| --- | --- |
| `node --test --test-reporter=dot scripts/model-policy.test.mjs` | All 59 tests passed. |
| `node --check` for `model-policy.mjs`, `model-policy.test.mjs`, and `pstack.mjs` | Passed. |
| `node scripts/model-policy.mjs validate` | `valid`, exit 0. |
| `node scripts/model-policy.mjs resolve backend-implementation` | `ready`: exact Astra model, explicit xhigh, exit 0. |
| `node scripts/model-policy.mjs resolve frontend-review` | `blocked`: missing Gemini, no runnable partial panel, expected exit 2. |
| `node scripts/pstack.mjs check` | Passed: 49 skills, clean pinned upstream `23e4138`, generated adapters, shared links, and model configuration. |
| `node scripts/pstack.mjs audit-codex` | Passed through the actual discovery API: exactly 49 project skills enabled, unrelated skills retained outside the project. |
| `git diff --check` | Passed; Git reported a normal README line-ending normalization warning. |

The first setup check found generated `.claude/skills` and `.cursor/skills` junctions still targeting the old `IDevelop` casing after the project-folder rename. Both junctions were verified to target this project's skill directory, then recreated with the current `iDevelop` casing. No skill contents changed. The full check passed afterward. Codex discovery initially failed because sandboxed Windows executable discovery was denied; the authorized read-only audit passed outside the sandbox and passed again after refreshing the junctions. CI on Linux has been configured but not run in this Windows session.

## Remaining work

Enable the Claude and Gemini clients through supported local integrations and verify access to the exact requested models. Do not replace them silently. The current Codex desktop conversation's model and effort controls are separate from project policy and cannot be changed through the available task tools.

After verification, activate the external model entries and run the required Opus review over the setup changes. Application implementation has not started. No provider login, model request, PR, or push was performed by this task.
