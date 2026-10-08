# Workspace and execution plan

## Task and authority

The user's feedback on 2026-10-06 changes the next product milestone. A person needs to run a workflow, manage several projects, and talk to agents without treating the inspector as a log viewer. The current compact canvas remains the visual baseline.

On 2026-10-06, the user approved this execution plan and explicitly deferred implementation. The scope, delivery sequence, and verification gates are approved. Choices listed as open still require the design validation described below. Later on 2026-10-06, the user authorized D0 and lifted the deferral. The user approved the client probe's real-client runs on 2026-10-06. The [D0 design validation record](2026-10-06-d0-design-validation.md) holds all three workstreams' results, including the [client capability probe](2026-10-06-d0-design-validation.md#the-client-probe-found-structured-requests-only-in-claude-code-and-codex-app-server).

## Planning checklist

The task follows a bounded Figure it out playbook. The deliverable is a checked plan, not a running feature.

- [x] Read the Principles section of the **poteto-mode** skill.
- [x] Phase A: Frame.
- [x] Phase B: Design the workflow.
- [x] Phase C: Run the loop. The units were evidence gathering, documentation revision, and a bounded design critique. No application experiment ran.
- [x] Phase D: Keep the audit trail.
- [x] Phase E: Verify and hand back. Documentation checks passed. Runtime behavior and independent full-file review are not verified.

The planning predicate is that all seven feedback points have a requirement, an owner in the proposed design, a delivery slice, and observable acceptance evidence. Current behavior and future behavior must not be confused.

- [x] Frame.
- [x] Fan out.
- [x] Aggregate. Discarded blocked exploration outputs. Used coordinator source evidence and a bounded independent design critique.
- [x] Report.
- [x] Read the current product direction and inspect the supplied screenshot.
- [ ] Trace execution, desktop ownership, and verification through read-only delegates. skip: the runner exposed no file tools. Coordinator source inspection replaced the blocked exploration.
- [x] Map every feedback point to an observable acceptance case.
- [x] Update the roadmap and distinguish requirements from recommendations.
- [x] Review the resulting documentation and run the handoff check. Coordinator inspection and documentation checks passed. Independent full-file review remains blocked.
- [ ] Application implementation. skip: the user explicitly prohibited implementation.
- [ ] Interactive prototypes and real-window changes. skip: this round is planning only. Empirical choices remain unverified.
- [ ] Runnable verification skills. skip: specify their coverage now and author them against real controls in a later implementation round.
- [ ] Commit, publish, or merge. skip: the user requested planning, not shipping.
- [x] Record the user's approval of the execution plan and explicit deferral of implementation.

## Throughput checkpoint

The coordinator owns documentation and decisions. Three read-only lanes inspect execution, desktop navigation and presentation, and verification. They share this checkout without writes. A fourth fresh session was intended to synthesize their findings. That step did not run after exploration failed. Reports use file pointers rather than source dumps. A fresh Opus session instead reviews an inline design packet without file access. That review cannot establish source correctness.

The intended critical path is evidence, revised roadmap, independent review, then documentation checks. The delegate runner exposed no file-reading tools, so the exploration reports were blocked and the coordinator stopped the fan-out. The coordinator inspected the source and demo directly. Independent file review remains a recorded gap, not a passed gate. Future implementation can separate inspector work from execution design. Multi-project ownership must precede background workflow execution. Conversation state must precede inline node interaction. Concurrent writers need separate worktrees and branches. Windows verification runs serially per account because the existing harness owns one theme-preference backup.

## Confirmed requirements

| Feedback | Required outcome | Planned slice |
| --- | --- | --- |
| 1. No workflow start button | A visible Run Workflow action starts eligible roots without requiring node selection. Node-only execution remains a distinct action. | E3 |
| 2. Successors do not start | Successful predecessors release connected successors automatically once all required dependencies and inputs are satisfied. Successors receive predecessor content. | E1, E2, E3 |
| 3. Unselected inspector alignment | The empty-selection panel has consistent right insets and aligned counts, fields, and actions. | U1 |
| 4. Inspector appearance | A Godot-inspired property editor has clear groups, aligned rows, and icons for most fields, within iDevelop's existing visual language. | U1 |
| 5. One project and one workflow | Several projects stay open. Each holds multiple workflows. Workflow rows initially hide their node references. | W1 |
| 6. Richer cards | New and generated cards default to collapsed. Each card and the canvas can control expansion. Expanded cards offer common run controls, supported model and effort settings, result links, and agent interaction. Attention remains visible when collapsed. GitHub creation requires an inspector action and user confirmation. | C1, N1, G1 |
| 7. Agent communication | A chat-quality view renders Markdown and preserves accessible conversation history. Status, tool activity, questions, and approvals have distinct presentations. | C1 |

The user did not request a new canvas visual style, a terminal emulator, a provider switch, or removal of first-release team synchronization.

## Evidence and limits

The baseline is `3b8bc99d4dd626e8b5925ba17efd323a906549bf`. Source inspection found these boundaries:

- `src/IDevelop.Core/Execution/WorkflowSchedule.cs` calculates readiness and starts nothing. Searching `src` and `tests` found calls in tests only. `ProjectRuns.Follow` advances review loops, not the dependency graph.
- `src/IDevelop.Core/Execution/StartCheck.cs` passes an empty input string to `NodeContext`. `src/IDevelop.Core/Nodes/AgentWork.cs` can render `context.Inputs`, but standalone execution does not populate predecessor content.
- `src/IDevelop.Core/Projects/WorkflowDocument.cs` rejects multiple workflow files on both Open and Save. This is a storage-entry restriction as well as a navigation limitation.
- `src/IDevelop.Desktop/MainWindowViewModel.cs` opens one canvas and disposes the previous project's runs when opening another folder. W1 must separate selecting a view from closing a project. `ProjectRuns` also holds one followed workflow, so simply opening several documents on one runner would not preserve each workflow's review ownership.
- `src/IDevelop.Desktop/Execution/AttemptViewModel.cs` already builds turn history across a continuation chain. `Inspector/InspectorView.axaml` renders replies in read-only text boxes capped at 240 layout units. The gap is presentation and history access, not proof that no history exists.
- `src/IDevelop.Core/Execution/AgentEvent.cs` normalizes complete messages, tool starts, notices, and outcomes. It has no structured provider-permission request or text-delta event. C1 therefore includes protocol work, not just a Markdown control.

Adding a button alone would not supply workflow execution.

The supplied screenshot shows the unselected inspector with counts close to the right edge and library actions on a different right alignment from the filter. It establishes the visual complaint. It does not establish a clipping, binding, DPI, or layout root cause. U1 must reproduce the issue in the running Windows app before changing layout.

The user also authorized read-only inspection of the demo workflow and logs in `C:\dev\fsharp\FastFSharp`. The coordinator inspected their structure without changing the project. Its one format-3 workflow has seven nodes and seven dependency connections. A Plan leads to an Implement, which splits into two Implement branches. Those join at another Implement, followed by Review and Approval.

The snapshot contains four attempt logs belonging to the planner and the first implementation node. Two planner logs contain cancellation requests. A later planner log and the implementation log contain client-success events followed by exit code zero. No attempt log exists for the remaining five nodes in this snapshot. This agrees with the source's missing scheduler wiring, but logs alone do not prove how a person initiated a run or whether the work met its acceptance criteria.

All seven saved nodes use Autonomous conversation mode. The four logs have no queued-message or continuation-turn events. They therefore do not exercise the existing May ask or continuation-history behavior. C1 needs synthetic multi-turn and independent-attempt cases rather than assuming these logs prove history was lost.

Private prompts, conversation text, account details, repository URLs, and session identifiers are excluded from this record. Local sanitized summaries remain under `.git/` and are not product fixtures.

The [node model record](2026-10-04-node-model.md) establishes continuing client sessions, typed pending questions, review loops without a round limit, and accepted planner proposals. The [redesign record](2026-10-05-node-system-redesign.md) establishes compact cards, generated Fluent icons, and the existing inspector structure. These are foundations to preserve, not evidence that the user's new acceptance cases already pass.

### Design references

- [Godot's Inspector Dock](https://docs.godotengine.org/en/stable/tutorials/editor/inspector_dock.html) and its [inspector screenshot](https://docs.godotengine.org/en/stable/_images/inspector_overview.webp) were inspected. Reuse the property hierarchy, filter, consistent value column, foldable sections, and non-default revert affordance. Do not copy its game-engine terminology or dark palette.
- [Apple's disclosure controls guidance](https://developer.apple.com/design/human-interface-guidelines/disclosure-controls) appeared in search results. The direct page fetch required JavaScript, so it was not fully inspected. Progressive disclosure below is a design recommendation, not a claim of complete HIG compliance.
- [Codex app-server documentation](https://developers.openai.com/codex/app-server) describes threads, turns, streamed items, history, and approval requests. It is a candidate for a later client-specific capability probe, not a decision to replace all current client adapters. [D0's client probe](2026-10-06-d0-design-validation.md#the-client-probe-found-structured-requests-only-in-claude-code-and-codex-app-server) ran it as a candidate. The fetched app-features URL redirected to general ChatGPT documentation and supplies no evidence about the current Codex desktop layout.
- The user's t3.chat, t3.code, WorkBuddy, and Claude desktop examples establish a desired conversation experience. No feature or protocol claim about those products was verified in this round.

## Planned experience

### Run the selected workflow without finding a root

Run Workflow belongs in the workflow toolbar near Generate Workflow. Its label and scope remain visible without a selected node. It opens a preflight summary of the selected workflow, resolved agents, dependency blockers, inputs, and code isolation. The user confirms the exact execution revision before launch.

The runner finds every eligible root. There is no artificial start node to place and no requirement to select a starting task. An empty or invalid graph shows a specific reason instead of a disabled button without explanation. Missing configuration links back to the affected nodes.

A workflow run has an aggregate summary and a Stop Workflow action. Stop prevents new launches and requests cancellation of active attempts. It does not undo completed work. Node Start and Stop affect only their stated node. Within a workflow run they remain subject to its dependencies and ownership checks. An explicitly labeled standalone run does not silently count as completion in an active workflow.

Completed planner work needs special treatment. Proposed preflight lists reusable results and requires explicit inclusion of a matching result in the new run. An old successful attempt must not automatically satisfy a new run. Reuse validates the node configuration, inputs, and relevant code revision. This avoids both rerunning a completed planner unexpectedly and trusting a stale result.

### Make completion observable without wiring UI nodes together

The user-facing behavior is reactive. The proposed implementation uses typed, durable attempt events and one run coordinator. Node controls display state and send commands. They do not subscribe directly to other controls or launch their successors themselves.

A completion event causes the coordinator to recompute readiness from recorded run state. A successor starts only after every required dependency produced an accepted result and the required content is available. A notification is a reason to recheck readiness, not authority to launch.

A diamond graph is the basic acceptance case. If A feeds B and C, and both feed D, D waits for B and C and starts once. A repeated completion event cannot start D twice. Unrelated ready work continues when another branch fails, waits for a person, or is cancelled.

A dependency carries a stable reference to the result of a specific attempt in the current run. The input includes the predecessor's report and declared artifacts. Code inputs also include their base and resulting revisions. The inspector shows which inputs the successor actually consumed. Input composition needs deterministic ordering and a visible policy for large content. It must not silently truncate required instructions.

Context connections remain non-blocking. Proposed behavior captures available context when the successor is prepared and records its provenance. A later context update does not mutate a running prompt. Missing context is visible and is not mistaken for a satisfied dependency.

Review and Approval nodes preserve their different meanings. A review releases its dependents only after agreement. Findings and intermediate completed turns do not release them. An Approval node has no agent and waits as a structured human request. Work that must wait for a review depends on the review node, not just its subject.

E3 must establish one launch authority for every run-owned task, including review fixes and reviewer turns. The existing `ProjectRuns.Follow` path must participate in that ownership rule rather than race a new dispatcher. The exact extraction remains a design choice. Standalone reviews still work, and an active workflow cannot acquire a second launcher through the standalone path. A multi-round review test must reach one Approval request only after agreement. The round count remains visible, and Stop Workflow can cancel the loop.

### Keep several projects open without changing execution ownership

The left panel becomes a project tree. Each project contains workflow rows, and expanding a workflow reveals node references. Project rows can show their workflows immediately, but workflow rows initially remain collapsed. Selecting a workflow opens its canvas without forcing the tree row open.

Proposed document state preserves each workflow's unsaved edits, undo history, selection, and viewport. Opening or selecting another project is not closing the current one. Switching views does not cancel a run. Closing a project with active work requires an explicit stop-or-keep-open decision.

Every command, event, conversation draft, and result is routed by project, workflow, task, and attempt identity as applicable. Titles are not identifiers. Two projects may contain workflows and tasks with the same names. Cloning a workflow gives it a new identity and does not inherit active execution ownership.

Project workflow files remain under each project's `.idp/workflows/`. The list of locally open projects and view preferences belongs to local app settings, not a future team workspace. Existing single-workflow projects must open without losing their graph or attempts. Format changes and migration details require a dedicated review before implementation.

### Give the inspector one clear job

The inspector remains the complete editor. Proposed sections include Task, Agent, Inputs, Outputs, Run, and Blueprint, plus sections relevant to the selected work. The selected-node header carries its kind, title, and one concise state summary. Long activity and conversation no longer compete with properties for space.

The property layout uses shared label and value alignment, consistent right padding, and a reserved trailing area for reset or action buttons. Long prompts use full-width multiline editors. Narrow inspector widths can stack a row rather than clip its value or action. The filter finds labels and reveals matching folded sections. Reset controls appear only for changed values.

Most fields get a meaningful Fluent icon with the same frame and baseline. Unknown user-defined fields use a neutral fallback, not a guessed semantic icon. Labels remain visible. Icons do not replace names or require a new icon library.

With nothing selected, the panel describes the current workflow and offers its overview and library. It uses the same header, row, and inset rules as the selected inspector. Counts and library actions align to defined columns rather than the panel edge. No invisible placeholder field is used to force spacing.

### Preserve compact cards and reveal practical controls

The existing compact card remains the default. It keeps the kind tile, title, and concise status. A persistent attention glyph and accessible label identify a question, approval, failure, or blocked input without expansion. A result-link indicator makes published artifacts discoverable without adding several lines to every card.

An expanded card adds Start or Stop, agent and supported model and effort controls, a short result preview, artifact links, and a compact conversation area. It can answer a pending question or open the full conversation. It does not embed an unbounded scrolling transcript in the graph. The inspector still exposes every setting.

Model and effort changes describe the next attempt or an explicit revised run. They never pretend to change an already-running turn. The current attempt's effective settings remain visible. Unsupported settings are absent or explain why they cannot be selected.

Each card has an explicit disclosure button. Canvas-level Expand All and Collapse All affect cards in the current workflow, not all open projects. New cards remain collapsed even after a previous Expand All. Proposed expansion state is a local view preference rather than part of execution approval. Persistence across restart remains a visual-prototype choice.

Expansion must preserve node positions and connections. Buttons, text selection, pickers, and composers must not start a node drag or steal canvas shortcuts. Expanding a card never marks the execution graph as changed. Compare height strategies and overlap behavior in a later prototype before choosing dimensions.

### Make conversation a first-class view

Proposed Conversation is a dockable pane that can expand into the main work area while retaining the selected task's project and workflow breadcrumb. An attention action on a card opens this same conversation at the outstanding request. It does not create a second thread. The inspector includes an Open Conversation action and full settings, not another independent transcript implementation.

The conversation shows user and agent messages in order across turns and continuation attempts. An attempt selector also exposes independent earlier runs, including cancelled ones, rather than showing only the newest continuation chain. Run boundaries, model changes, failures, and terminal handoffs have explicit markers. Tool activity folds separately from agent messages. Internal result-contract JSON is parsed into proposal, question, and review cards where valid, rather than printed as ordinary chat. Malformed output remains available in diagnostics.

Markdown supports headings, lists, code blocks with copy, tables, and links. Streaming must preserve selection and scroll position while the user reads earlier content. New messages offer a jump-to-latest action rather than forcing a scroll. Long history loads incrementally. Remote images, raw HTML, executable links, and local file navigation need explicit safety rules before renderer selection.

The composer distinguishes Send, queued delivery, Stop and send, and cancellation. Draft text survives selection changes and belongs to the correct conversation. An agent question has a direct response control. A permission request names the exact action and scope and offers allow or deny only when the client integration supports it. A workflow Approval node and a provider tool-permission request are not the same approval.

The current clients do not provide identical interaction protocols. Start with truthful capability reporting. Plain output that resembles a question is not proof of a structured permission request. A client without a supported approval channel must say so and provide a safe alternative. The existing sandbox policy must not be relaxed merely to make the UI seem interactive.

### Publish GitHub artifacts only after confirmation

An Architect's completed report can become a proposed issue. A pull request additionally needs an actual branch and code diff. An Architect result does not fabricate a PR-ready branch.

The inspector offers a publication preview with destination repository, artifact type, title, body, and branch information where relevant. The person triggers and confirms it. Cancel makes no request. Publication is not an automatic consequence of node success.

A confirmed result is attached to the originating node and attempt, then shown as a link on its card and in Outputs and Conversation. Refreshing or retrying an uncertain response must reconcile the original operation before creating another issue or pull request. Completion, publication, human approval, and merge remain separate states.

## Proposed architecture and decisions still open

The data shape comes before new UI handlers. Existing immutable workflow edits and attempt reducers remain the base.

| Owner | Data and responsibility | Excludes |
| --- | --- | --- |
| Local open-project session | Open projects, workflow documents, selection, view preferences, and conversation drafts | Team membership and execution decisions |
| Workflow document | Editable task graph, embedded blueprints, layout, and undo | Processes and provider sessions |
| Workflow run coordinator | Approved revision, run-owned attempts, readiness, launch claims, cancellation, and recovery | Avalonia controls and direct peer-node callbacks |
| Result and input records | Reports, artifacts, code revisions, provenance, and resolved successor inputs | Implicit reads of arbitrary latest attempts |
| Conversation model | Ordered turns, messages, activity, pending requests, and per-client capabilities | Independent card and inspector histories |
| Client, Git, and GitHub adapters | Protocol parsing, process control, code materialization, and confirmed external operations | Graph or presentation rules |

These are proposed responsibilities, not instructions to create one service per row. A module must remove duplicated rules or own a real boundary to earn its place. No message broker, distributed actor system, generic plug-in framework, or new application language is proposed.

D0 settled two of the bullets below, Git inputs and worktree lifecycle. It also settled the conversation comparison in the Interaction bullet. Conversation uses the main area by default, with a dock as an option. D0 also recorded the Interaction bullet's [client capability table](2026-10-06-d0-design-validation.md#the-client-probe-found-structured-requests-only-in-claude-code-and-codex-app-server). C1 still selects each client's protocol and adds only the event cases those protocols need. The [D0 design validation record](2026-10-06-d0-design-validation.md) holds the decisions.

These choices require a design gate before E2 or E3:

- Git inputs. Compare isolated task branches with an explicit integration step against a run integration branch with serialized writers. The preferred direction is isolated writers with explicit input materialization. Conflicts block with evidence instead of an agent silently overwriting another branch.
- Worktree lifecycle. Define branch retention, dirty-worktree recovery, review fix ownership, and user-approved cleanup. Never delete unfinished work automatically. Git remains required for workflow execution under the earlier user decision.
- Run approval. Bind attempts and inputs to the approved revision. Preserve the previously accepted rule that a planner proposal during a run may add nodes or fill only unstarted nodes. A proposed explicit run amendment records and approves that new revision without changing running attempts. Do not silently ban these proposals under an oversimplified immutable-run rule.
- Retries. Proposed initial default is no automatic retry of a failed or cancelled attempt. Manual retry records a new attempt. Transport retry, task retry, and a review fix round are distinct. The user-confirmed review loop still has no round limit.
- Concurrency. Expose a bounded run setting before parallel launches. The initial value requires a measured client and isolation probe. No unmeasured throughput promise or arbitrary universal limit belongs in this plan.
- Recovery. Duplicate completion delivery must converge. A launch with an uncertain outcome requires reconciliation before replacement. A restart must not infer success from a missing process or replay an external action blindly.
- Interaction. Compare a bottom conversation dock with a main-area conversation view in a later prototype. D0 produces a capability table for each installed client and its exact version and launch mode. Scratch probes record a reply stream, a question, a permission request where supported, allow and deny responses, a stale response, queued or live input, Stop and send, resume, and cancellation. Unsupported cases are explicit results, not failed promises of universal parity. C1 adds only the normalized event cases the selected protocols need and keeps old attempt logs readable. Existing repository fixtures and synthetic cancellation and continuation logs provide compatibility evidence. Do not commit the user's demo transcripts as fixtures.

## Approved delivery slices

The user approved these delivery units on 2026-10-06 and later authorized D0, lifting the deferral. They are not PRs created by this task. Each slice ends with headless tests, Windows real-window evidence where applicable, independent review, and the repository's required checks. D0's checks are per workstream, so inspector prototypes need not wait for Git integration research. Interaction changes require the user's visual review before merge.

| Slice | Depends on | Scope and visible completion condition |
| --- | --- | --- |
| D0. Validate the designs | None | Reproduce inspector alignment. Compare inspector, expanded-card, and conversation layouts in throwaway prototypes. Probe client interaction and Git input delivery in scratch repositories. Capture baseline evidence. No production behavior yet. All three workstreams are done, and the [D0 design validation record](2026-10-06-d0-design-validation.md) holds their results, including the client [capability table](2026-10-06-d0-design-validation.md#the-client-probe-found-structured-requests-only-in-claude-code-and-codex-app-server). |
| U1. Repair the inspector | D0 | Selected and unselected views use consistent grids, insets, icons, folds, and filters in both themes and at narrow widths. Keep the current conversation accessible until C1 replaces its presentation. |
| W1. Open multiple projects and workflows | D0 | Two projects with multiple workflows remain open. Workflow rows hide nodes by default. Switching preserves unsaved state, history, and run ownership. Existing projects still load. |
| C1. Build conversation and attention | D0's client probe, W1 | The selected task has durable, correctly routed history, Markdown, a composer, structured requests, and honest client capability limits. Inspector and compact-card attention open the same conversation. |
| E1. Define run and input records | D0, W1 | An approved workflow revision owns its attempts and explicit input provenance. Replay and duplicate-start tests pass. Prior standalone results require explicit validated reuse. |
| E2. Materialize dependency results | E1 and an accepted Git ownership and integration contract | Reports and artifacts reach successors. Isolated code inputs produce a named revision. Fan-in conflicts and uncertain ownership block without corrupting work. Review fixes use the correct code owner. |
| E3. Execute the workflow | C1, E2, and its verified writer-isolation and join-revision gate | Run Workflow finds roots, launches eligible work, waits at questions and approvals, starts successors once, and stops safely. Review advancement and scheduling share one launch authority. Failure affects only dependent branches. |
| N1. Expand nodes | U1, C1, E3 | Per-card and workflow-wide disclosure expose common controls, compact conversation, attention, and output links. Compact visuals and graph gestures remain intact. |
| G1. Attach confirmed GitHub results | U1, C1, E1 | Inspector preview and confirmation create or attach an issue or PR. Cancellation writes nothing. Retry cannot duplicate publication. N1 adds the full card presentation when available. |
| T1. Add team synchronization | Local run contracts complete | The existing first-release requirement remains. Its design must reuse project, workflow, run, and attempt identity without moving agent execution to the server. |

U1 can proceed while W1 and execution design develop in separate worktrees. C1 and E1 can proceed independently after W1. No slice may substitute a cosmetic Run button for E2 and E3. E3 cannot launch concurrent writers until E2 proves separate ownership and a named integration revision on the demo-shaped diamond. GitHub publication is not a prerequisite for local graph execution.

## Verification skill plan

Extend the project-owned `.claude/skills/verify-idevelop` feature map rather than create seven overlapping launch-and-cleanup skills. Keep one setup, isolation, evidence, and cleanup protocol. Generate installed copies through `node scripts/pstack.mjs setup`, never by editing `.agents/skills/verify-idevelop` directly.

The future guides below are proposed artifacts. They do not exist because this planning round does not implement controls or fixtures.

| Guide coverage | Required checks | Evidence |
| --- | --- | --- |
| Workflow execution | A chain and diamond fan-in. Multiple roots. Duplicate completion and double start. Failure, cancel, question, approval, review, crash, and restart. Context never blocks. | Screen states, run and attempt records, launch counts, exact successor input content, and no duplicate processes. |
| Workspace navigation | Two projects with at least two workflows each. Same titles in different projects. Unsaved switches. Default collapsed tree rows. Reopen. Close with active work. Two active review loops in separate workflows retain their own subjects. | Screenshots and persisted files proving the intended document changed and the other project did not. |
| Inspector | Empty selection, node selection, connection selection, library, filter, folds, reset, long labels, custom fields, narrow and wide panes, light and dark, and supported DPI settings. | Screenshots plus control bounds proving right-edge actions remain visible and aligned. |
| Expanded cards | New and generated cards collapsed. Per-card and all-card toggles. Start and Stop. Model and effort edits. Attention and result links. Zoom, select, drag, and text-input conflicts. | Before and after screenshots, unchanged semantic revision for disclosure, effective attempt settings, and input-event checks. |
| Conversation | Several turns, continuation attempts and independent prior runs, restart, Markdown streaming, large history, queued messages, Stop and send, stale approval, denied approval, and switching tasks with a draft. | Rendered screenshots, ordered persisted messages, exact response routing, and request identifiers. No screenshots of real private conversations. |
| GitHub publication | Preview, cancel, confirm, branch prerequisites, double click, lost response, retry, and linking an existing artifact. | Fake service request records and one resulting artifact reference. A real write requires separate user consent. |

A deterministic fake client should provide controlled completion, waiting, streaming, failure, and duplicate-event scenarios. A fake GitHub boundary should record requests without contacting GitHub. Synthetic fixtures reproduce the demo's structural cases without committing its content.

The current UI Automation harness cannot drive all graph pointer gestures or keyboard input. Headless tests cover those today. They are not proof of real-window drag, IME, focus, or shortcut behavior. Future verification needs a suitable input-driving mechanism or an explicit witnessed manual step for those cases. Until then, record the gap instead of declaring visual completion. The existing Windows harness also cannot establish macOS or Linux interaction parity.

Each feature guide must name actual controls only after they exist. Capture a pre-change baseline before implementation. Store screenshots beside assertions and persistence evidence. Verify both themes. The existing 500-task and 2,000-task acceptance targets remain targets, not measured achievements. Later performance probes must isolate rendering, history loading, and scheduling from provider latency and record the reference machine before choosing budgets.

Application slices run `dotnet build -c Release`, `dotnet test -c Release`, `node scripts/check-licenses.mjs`, `node scripts/planweave-tokens.mjs --check`, and `node scripts/fluent-icons.mjs --check`. Skill changes also run `node scripts/pstack.mjs check` and `node scripts/pstack.mjs audit-codex`. Documentation changes run `node scripts/check-handoffs.mjs`.

## Alternatives not selected

- Direct events between node view models. They couple launch behavior to selection, lifetime, and open views, and make replay and duplicate handling harder to inspect. Typed events into one run coordinator preserve the requested reactive behavior.
- One overloaded inspector for properties, logs, and chat. It keeps the exact competition for space that the feedback rejects. The proposed conversation view shares data with cards and inspector without sharing their layout.
- Expanded cards by default or full transcripts inside every card. They discard the compact canvas the user likes and crowd the graph. Expansion reveals common controls on demand.
- A cosmetic run button on top of standalone task execution. It cannot guarantee correct inputs, isolation, or exactly one launch when dependencies complete.
- Team synchronization before local ownership is explicit. It spreads the existing single-project assumptions across machines. Team mode remains required after the local run model is proven.
- Separate verification skills with duplicated setup. They can drift on client isolation and cleanup. Feature guides under one skill retain a single launch contract.

## Independent critique and disposition

A fresh Claude Opus 5.5 session at xhigh reviewed an inline design packet. Its result is not a source review or a review of the complete document.

- The reviewer asked for an explicit Git prerequisite before automatic fan-out. E2 and E3 already had that sequencing, but the table now names the accepted integration contract and verified diamond join as hard prerequisites. No extra architecture slice is necessary.
- The reviewer identified two potential dispatch authorities. The plan now requires the existing review driver and new coordinator to share one launch authority for run-owned work. Its suggestion to treat changes requested as a terminal review result was rejected. Findings remain intermediate under the user's no-round-limit loop. The Approval node was already specified as human-only.
- The reviewer asked for concrete client probe outputs and event compatibility checks. D0 now names the capability table and probe cases. Its suggestion to commit the four demo logs as fixtures was rejected because those contain private content. Existing fixtures and synthetic equivalents provide the compatibility checks.

## Changed artifacts

- This record holds requirements, recommendations, delivery dependencies, rejected alternatives, and verification contracts.
- `docs/product-direction.md` records the revised product milestone and delivery order.
- `docs/context.md` records the user's new requirements and points to this plan without claiming it is implemented.

## Commands and observed results

- `git status --short` showed a clean checkout before planning.
- `node scripts/model-policy.mjs resolve exploration` and `node scripts/model-policy.mjs resolve judgment` returned Claude Opus 5.5 with explicit xhigh effort.
- `node scripts/check-handoffs.mjs` passed with six linked records after the roadmap update.
- `git diff --check` passed after the roadmap update.
- A temporary Python check passed for seven feedback rows, local Markdown link targets, punctuation in this record, and a change set limited to these three Markdown files. Its first invocation failed on shell escaping and its corrected invocation passed.
- Read-only delegate exploration was blocked by the Claude runner's `tools=none` configuration. The coordinator stopped workflow `9671cabf-bc12-40f4-8ab6-fad22b286c6b` and discarded its unsupported recommendations. No delegate edited project files. Independent file review is not complete.
- The local decision trail is `.git/workspace-planning-decisions.tsv`. The sanitized demo summaries are `.git/demo-structure.json` and `.git/demo-events-summary.json`. They contain no private message content.
- A bounded independent design-packet critique completed on Claude Opus 5.5 at xhigh. The coordinator addressed its accepted findings above. The report is a local subagent artifact, not a committed transcript.
- No build, app run, prototype, or live provider test was performed in this planning round.

## Open issues and next action

The execution plan is approved. Detailed run approval, concurrency, renderer choice, and client approval capabilities remain design work within its gates. Approval of the plan does not select among those open alternatives or establish runtime verification.

D0 is done across all three workstreams, as the [design validation record](2026-10-06-d0-design-validation.md) records. C1 can start after W1 from the capability table and the five event cases. It selects each client's protocol, with Codex app-server as a candidate for the Codex adapter. U1 can start from layout A and the shared column definition. W1 can start and includes the blueprint icon and color field in its format and migration review. E2 builds on the revised Git contract after E1. E3 still needs its writer-isolation and join gate verified on the demo-shaped diamond in the implementation. Delete the local `d0/ui-prototypes` branch and its worktree once U1 no longer needs them.
