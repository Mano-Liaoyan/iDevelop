# Assign all prototype work to Claude Opus 5.5

## Task

The user judged that dispatching work across Claude, Codex, and Antigravity is premature before the app exists. Until the app prototype works, Claude Opus 5.5 does all frontend and backend work. After the prototype works, the user will choose each part's owner inside the app. The user asked for this decision to be recorded in the project, not in assistant memory.

## Decisions

- Every policy role selects `opus`: frontend and backend implementation, frontend and backend review, judgment, difficult-task review, and exploration.
- Each review runs in a fresh Opus session that did not write the change, with explicit `--model claude-opus-5-5 --effort xhigh`.
- The validator's role table now requires the `anthropic` provider for every role. It encodes the current decision as before, so a config edit cannot silently move a role to another provider.
- `reviewPolicy.crossProvider` must be `false`. A `true` value could never validate while every role requires `anthropic`, so the author and provider separation loop was deleted. The change that moves a role to another provider must restore it from Git history.
- The scope-specific roles stay. Reassigning a part later changes `requiredRoles`, its tests, and `models.json`, not the role vocabulary.
- The GPT-6 Astra and Gemini 3.8 Flash registry entries keep their verified routes. No role selects them. `.codex/config.toml` still sets Codex launches to Astra at xhigh, which matters only if Codex is used manually.
- The effort ceiling, explicit effort, and blocking on an unavailable route are unchanged.

## Changed artifacts

- `.pstack/models.json` assigns every role to `opus` and sets `reviewPolicy.crossProvider` to `false`.
- `scripts/model-policy.mjs` requires `anthropic` for every role, requires `crossProvider` to be `false`, and keeps the single-owner check for each scope.
- `scripts/model-policy.test.mjs` uses the interim policy as its fixture. It covers every role resolving to Opus, a pending Opus route blocking every role without substitution, a single owner per scope, and rejection of non-Claude assignments and of `crossProvider: true`.
- `README.md`, `.pstack/compatibility.md`, `docs/context.md`, and `.cursor/rules/pstack-models.mdc` describe the interim assignment and the fresh-session review. The README also no longer calls the stack undecided.

## Commands and observed results

| Command | Result |
| --- | --- |
| `node --test scripts/model-policy.test.mjs` | 57 passed, 0 failed. |
| Same tests against a copy that requires Google and OpenAI for frontend review | 12 failed, so the tests detect the role table. |
| Same tests against a copy that checks a single owner only for backend | 1 failed, so the tests detect the frontend owner check. |
| Same tests against a copy that accepts any `crossProvider` value | 1 failed, so the tests detect the pinned flag. |
| `node scripts/model-policy.mjs resolve <role>` for all seven roles | Each `ready`, with `claude-opus-5-5` at xhigh. |
| `node scripts/pstack.mjs check` | PASS for 49 skills, clean upstream `23e4138`, adapters, links, and model configuration. |
| Review in a fresh session through `scripts/agent.ps1 claude --print ... --model claude-opus-5-5 --effort xhigh`, with edit and shell tools disallowed | Request changes with two medium and seven low findings. All nine were accepted and fixed. The checkout's file hashes were unchanged by the review. |
| Second fresh-session review of the fixes, same command | All nine fixes confirmed. It found one stale open issue in this record, an incomplete reassignment checklist, and two wording and formatting nits. All four were fixed without a third review. |

## Open issues

- Same-model review is weaker than the earlier cross-provider review. The decision trades that for a simpler workflow until the app exists.
- Multi-member review panels cannot form while every role selects one entry. The removed panel tests covered blocking a partly available panel, listing every missing member in order, and never returning a runnable member of a blocked panel. The change that reassigns roles must restore those tests with literal expected results.

## Next action

Start the first implementation slice in C#: a solution skeleton with pinned Avalonia and NodifyAvalonia versions whose licenses are checked, a minimal task, connection, and workflow model kept separate from the UI, and a canvas that shows and edits a small sample graph.
