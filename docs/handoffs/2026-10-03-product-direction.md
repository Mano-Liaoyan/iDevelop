# Product direction research

## Task

Investigate PlanWeave and Pi AI for a cross-platform desktop application that coordinates coding agents on an editable node canvas. Capture the user's requirements, provider access evidence, stack tradeoffs, and a proposed first workflow. This task produces research and design documents. It does not implement an application.

## Investigation checklist

- [x] Route through the **how** skill. For motivation questions, also route through the **why** skill.
- [x] Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only investigation`.
- [x] Produce the `how`-shaped output (Overview / Key Concepts / How It Works / Where Things Live / Gotchas), or a recommendation with a tradeoffs table if the request is a decision between alternatives.
- [x] Apply the **unslop** skill to the reply.

The investigation is read-only with respect to application code and external services. This handoff and research documents preserve its findings as required by AGENTS.md.

## Research coverage

- [x] Frame. Completion requires cited findings for both reference projects, the requested providers, desktop stack alternatives, and a proposed first user workflow.
- [x] Fan out. Three read-only explorers cover PlanWeave, provider access, and desktop frameworks. The coordinator owns all document edits.
- [x] Aggregate. Reconcile evidence, distinguish support from unverified claims, and obtain an independent synthesis.
- [x] Report. Present a recommendation, tradeoffs, and unresolved product choices.

## Decisions and evidence

The user confirmed real-time team collaboration from the first release and review before a generated graph executes. The team service must be self-hosted and synchronize workflow state only. All agents run on local member machines. Standalone solo use must work without installing or connecting to a server.

These product facts are recorded in `docs/context.md`. All implementation architecture remains provisional. The initial proposal distinguishes editable workflows from immutable approved runs, and task definitions from agent attempts. Markdown is the portable session record, generated from structured records. A local runner owns its provider credentials and GitHub operations.

The user suggested IDE collaboration systems as possible references. Current primary sources identify VS Live Share as a product with a feedback repository, the separate Microsoft Live Share SDK as Teams-oriented, and Code With Me as being sunset. Yrs and Automerge are reusable Rust collaboration candidates. Neither decides execution ownership or validates the semantic correctness of merged graphs.

## Artifacts and verification

- `docs/product-direction.md` records confirmed requirements, PlanWeave findings, proposed task and attempt semantics, collaboration boundaries, session handoffs, stack alternatives, and acceptance cases.
- `docs/provider-access.md` records the nine requested provider families, the complete pinned inventory of 42 Pi provider configurations, source links, and access limitations.
- `docs/context.md` records only confirmed product direction. Technology recommendations remain provisional.
- This handoff records scope, decisions, and observed checks. No application code, dependencies, generated skills, account configuration, or credentials changed.

PlanWeave was inspected at `8647d015ac562e8fda148415b84ee77e3b3ada89`. Pi was inspected at `4c6fb7cfe8c538a668726f6f8b3554098c39faee`. Three read-only explorers used the configured `gpt-6.1-sol` role at max reasoning. A separate synthesis used the configured `gpt-6-astra` role at max reasoning. This was independent analysis within one model family. Read-only workers shared the checkout. The coordinator alone edited documents.

All three research slices returned findings with explicit runtime verification gaps. The synthesizer found no substantive contradiction or omitted confirmed requirement. It independently rechecked the current OpenAI sign-in grant and Claude SDK subscription notice. Its one clarification was accepted. The proposed server authorizes synchronized records and arbitrates task claims, so it is not merely a passive relay, but it contains no agent execution engine.

Observed checks:

- `node scripts/pstack.mjs check` passed for 49 skills, the pinned clean upstream, generated adapters, shared links, and model configuration.
- A PowerShell scan counted 42 unique provider identifiers in the saved inventory and confirmed that local Markdown links resolve.
- A second scan checked trailing whitespace, final newlines, and balanced code fences across all four documents. It passed, including the new untracked files.
- `git diff --check` passed for tracked changes. Git status showed only the four intended documentation artifacts.

The setup check does not validate an application. `audit-codex` was not repeated because no setup or skill-isolation behavior changed. No provider login, paid request, native prototype, runtime benchmark, or platform build ran. The external `deslop` and control skills were not needed because this investigation creates no commit or application interface. Project-local technical-writing and unslop guided the documents.

Model the Domain shaped the separation between task, attempt, graph revision, and approval. Experience First made editable canvas behavior, IME, and navigation acceptance criteria part of framework selection. Prove It Works required pinned source evidence and explicit labels for untested runtime claims.

## Open issues and next action

All three initial product questions are answered and the research is complete. No application or provider support is claimed as implemented. The next implementation task is to compare Rust with Qt Quick and Rust with Slint using the saved acceptance cases. Qt's QML JavaScript expressions must remain explicit in that comparison. Then prove one permitted subscription-backed local agent and one API-backed agent through solo use and two-client shared workflow review.

Custom-client subscription eligibility, coding-plan interactive-use restrictions, native rendering performance, terminal integration, product licensing, and three-platform packaging remain open integration questions. They are documented rather than assumed resolved. No commit, pull request, or deployment was created.
