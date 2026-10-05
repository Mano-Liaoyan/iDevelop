# Node model

## Task

On 2026-10-04 the user made the node model the top priority, ahead of workflow execution: "once the node is designed well, executing the rest is comparatively easy." A node today holds a title, instructions, acceptance criteria, and an agent setting. The user asked for typed nodes, node types that users define and save as blueprints, a review loop between two agents, and a way to talk to a running agent. This record holds the design. The user accepted it on 2026-10-04 and changed the review loop, as [What the user decided](#what-the-user-decided) records.

## Checklist

The Feature playbook drove the design. Each delivery slice runs the remaining steps in its own pull request.

- [x] 1. `how` over the affected subsystem. A fresh read-only Opus 5.5 session at xhigh mapped the workflow model and the single-task engine against workflow execution.
- [x] The user answered three product questions. See [What the user decided](#what-the-user-decided).
- [x] The coordinator ran a two-turn conversation on all four real clients. See [Commands and observed results](#commands-and-observed-results).
- [x] 2. `architect` for parallel design exploration. Two fresh Opus 5.5 runners at xhigh designed from opposing starting directions. A fresh Opus 5.5 cross-judge at xhigh scored them. See [The design arena](#the-design-arena).
- [x] The user accepted the design, kept the defaults for three open questions, and changed the review loop.
- [ ] 3. Throughput checkpoint. Slice 1 writes it in its pull request.
- [ ] 4. Delegate code writing, one slice at a time.
- [ ] 5. to 8. Verify, rebase, interrogate, and open the pull requests, one per slice.

## What the user decided

The user wrote in Chinese. This summary keeps the meaning.

- Nodes have types. An implement node reads a plan and executes it with the agent, model, and reasoning setting the user chose. A plan node makes a plan and hands it to implement nodes. An architect node designs. A review node is probably better than the review connection. Connections still express relationships.
- iDevelop does not depend on PStack. A user may prefer their own commands or another skill set, such as Matt Pocock's skills.
- Nodes are built on interfaces, with a base node interface. A node that needs a capability, such as interaction, implements that capability's interface.
- Users define their own nodes and node types. Any node can be saved as a blueprint. Built-in blueprints are read-only, and a user changes one by deriving a new blueprint and saving it.
- A review is two agents that iterate until both are satisfied, then hand the result on. The user rejected a fixed limit on rounds. Each agent's context window stays clean, because not every model has a large window and a bloated one makes the model worse.
- Interaction matters most. When an agent asks how to proceed, the user answers, guides, or corrects it. Some nodes only implement and never ask. A live terminal is acceptable but not required if it costs too much. The solution must cost little, work with all four clients, and leave room to extend.
- Git stays required for workflow runs. The current canvas look is fine.
- How a plan reaches implement nodes "depends". A planner usually creates the nodes, but sometimes the nodes exist already, or the user draws an architect and three to five empty nodes and fills them later. The long-term goal is a button where the user talks to one agent and the whole workflow appears. Waiting for a plan node to finish running before any implement node exists is not ideal. The user left the design to the coordinator.
- Placing a node copies its blueprint and records which blueprint and version it came from. A later edit of the blueprint changes only nodes placed afterwards.
- Blueprints live in a personal library in the user's folder and in a project library under `.idp/blueprints/`.

After reading the proposal on 2026-10-04, the user kept the defaults for three questions:

- During a workflow run, an accepted proposal only adds nodes and fills nodes that have not started.
- Changing one node's fields, template, or work means deriving a blueprint first.
- The Approval node stays in the first release.

The user changed the review loop. The proposal started both agents in fresh sessions each round and paused for the user when a round repeated an earlier one. The user wants the implement node to do its work and tell the reviewer what it changed. The reviewer judges and says what is wrong. The implement node fixes it and hands it back. They go back and forth until both agree. Each agent keeps its own session, the loop has no round limit, and the user does not have to stop it.

## Design

### A node type is a blueprint over one of three works

A blueprint is data. It has an id and a version, a name, a description, the blueprint it was derived from, a work, a list of fields, and default settings. The work says what the node does, and it is a closed set:

| Work | Holds | Implemented by |
| --- | --- | --- |
| Agent | Read-only or edit access, whether it proposes graph edits, a prompt template | `AgentWork` |
| Review | A reviewer template and a fix template | `ReviewWork` |
| Person | Nothing. A person approves or sends back | `PersonWork` |

A field has a key, a label, a shape (one line or several), a required flag, and a default. A prompt template uses `{{title}}`, the field keys, `{{inputs}}`, and the work's own variables. The work appends iDevelop's result contract after the rendered template, so no blueprint can remove it.

A node keeps today's id and title. It gains the key of the blueprint it came from (`id@version`), its field values, and its settings: the agent, model, and reasoning, plus a conversation mode. The workflow embeds one copy of every blueprint version its nodes use. Placing a node copies the blueprint in, so a later edit of the library blueprint changes nothing already placed, and the file opens on a machine without the author's library. `Workflow.Apply` drops a copy that no node uses, so the two collections never need to be kept in sync.

| Part | Fixed by the blueprint version | A node may change it |
| --- | --- | --- |
| The work, its access, and whether it proposes | Yes | No |
| The fields and the prompt templates | Yes | No |
| The title and the field values | Seeds the defaults | Yes |
| The agent, model, reasoning, and conversation mode | Seeds the defaults | Yes |

The rule is "the blueprint fixes the structure, the node sets the values". A node's connections and attempt history keep their meaning because its work never changes under them. To change the structure, the user derives a blueprint, which is one click from any node. "Save as blueprint" turns a node's values and settings into the defaults of a new blueprint.

### Interfaces where behavior varies, data where users customize

The test for an interface is that implementations behave differently and more are coming. Otherwise data or a closed union is better, because an exhaustive switch shows the next developer every place a new case must go.

- `INodeWork` is the base node interface. Its one method, `Next(context, history)`, is a pure decider. It reads the node, what earlier nodes handed on, and the node's history, and it returns one step: run a turn, wait for the person with a reason, finish with a handoff, or fail. It starts no process and touches no file. A standalone run and the later workflow scheduler call it the same way, and the same history gives the same step, so a crash between a turn's end and the recorded step converges on reopen.
- `IConverses` is the interaction capability. `AgentWork` continues the client's session with the person's message. `ReviewWork` adds the message to its ledger as guidance. `PersonWork` has no agent and does not implement it.
- Each work carries a `WorkKind` enum value. The build already fails on a non-exhaustive enum switch, so a new work breaks every mapping that must handle it.
- Each client stays a row of pure functions, as phase 3 decided. A row gains the arguments that resume a session. The exit policy stays in one reducer and settles a turn instead of an attempt.
- Proposing graph edits is a flag on the agent work, because it has one behavior. Waiting for a person is a closed union, `Pending`, with one inspector section per case.

A user-defined node type is a blueprint, made in the window without code. It sets the name, the description, the work, the access, whether the agent proposes, its own fields, its prompt templates, and its default agent and conversation mode. It cannot add a work, change the result contract, change graph rules, or run a script. A new work needs code: one `INodeWork`, one `WorkSpec` case, and one `WorkKind` value.

### Five built-in types

Built-ins live in the app under the reserved `idevelop.` id prefix, so they are read-only by construction. Their templates are plain English and name no skill library. A PStack or Matt Pocock user derives a blueprint and writes a command or skill name into its template or a field.

| Type | Work | Consumes | Produces | Default conversation |
| --- | --- | --- | --- | --- |
| Implement | Agent, edit | Its fields, the reports of earlier nodes, and later their merged changes | Its change and its final report | Autonomous. The prompt is today's, byte for byte |
| Plan | Agent, read-only, proposes | A goal, constraints, earlier reports, the empty nodes after it | A proposal of nodes and connections, and a report | May ask |
| Architect | Agent, read-only, proposes | A brief, earlier reports, the empty nodes after it | A design as its report, and a proposal that fills the empty nodes | May ask |
| Review | Review | One subject's change and ticket, other inputs as context | A verdict, and the subject's change at the approved revision | Not applicable |
| Approval | Person | Its inputs | The person's decision, passing the inputs on | Not applicable |

### Review is a node, and connections express flow

`ConnectionKind.Review` goes away, which settles what a review connection blocks. Connections are `dependency`, which waits for its source and receives its result, and `context`, which reads the source's latest result without waiting. In `Implement → Review → Next`, the review's subject is its one dependency predecessor that produces a change. `Workflow.Apply` rejects a second one. The loop runs as attempts, never as edges, so the dependency graph stays acyclic under today's rule.

The implement node runs first, as any node does. Then the review node starts a back-and-forth between two sessions, and each session keeps its own history.

1. The reviewer's first turn starts its session with the reviewer template, the subject's ticket, the implementer's final report of what it changed and why, and the diff. It answers with a verdict: approve, or findings.
2. If the reviewer found problems, iDevelop resumes the implementer's own session with the findings. The implementer fixes each one, or disputes it with a reason, and reports what it changed.
3. iDevelop resumes the reviewer's session with that report and the new diff. The reviewer approves, raises new findings, or answers each dispute by withdrawing the finding or explaining why it stands. Then step 2 repeats.

Each fix round is a **Continue** of the implement node, a new attempt whose first turn resumes the implementer's session. Each review round is a new turn in the review node's own session. Neither agent reads the other's transcript. Each reads its own history plus the other side's short message, so a round adds one message and one diff to each session. Small tickets keep both sessions small.

The loop ends when the two agents agree. The reviewer approves with no open finding, and the implementer's last reply disputes nothing. There is no round limit, and the user does not have to stop it. iDevelop keeps agreement reachable:

- Every finding names the change that would settle it.
- A disputed finding goes back to the reviewer, who withdraws it or answers the reason.
- If a round repeats the positions of an earlier round, iDevelop says so to both agents in their next message and asks the reviewer for the exact change that would settle each open finding.

The user can add guidance at any time, which reaches both agents in their next message, and can cancel the review as any run can be cancelled. The loop does not need either to end.

iDevelop folds a findings ledger from the verdicts and replies. The card shows the round and the open findings. The inspector lists each finding with both sides' latest words. The ledger is a record and a view, and the agents talk through their sessions. If a session cannot resume, for example after the user changed the node's client, the next turn starts a fresh session with the ticket, the latest change, and the ledger.

iDevelop records the project tree with `git write-tree` through a temporary index when each turn starts and ends. A node's change is the difference between the tree at its first turn's start and the tree at its last turn's end, so the review reads the subject's change and not every edit in the folder. A read-only turn whose tree changed fails with that reason, which checks read-only access on every client, including Pi, which has no permission system.

### Planning produces graph edits the person approves

Every agent that plans produces one `Proposal`. A Plan node, an Architect node, and the future "talk to one agent" button all produce it. A proposal can add nodes, fill nodes, and add connections. It cannot delete, move, or retype anything. Filling an empty node the person drew and creating a new node are the same mechanism.

- The prompt lists the empty nodes after the planner as handles such as `slot-1`, and the blueprints the planner may place as handles such as `type-2`. The agent writes only handles. It never writes ids and never chooses agents. A new node takes its blueprint's default agent, model, and reasoning, so the person's choices come with it.
- iDevelop parses the proposal once, where it arrives, and mints the new ids then. A second Accept is rejected by the existing duplicate-id rule.
- A dry run through `Workflow.Apply` draws the proposed nodes as dashed ghost cards and fills the drawn ones in place. The person accepts all of them or unticks some first. Accepting applies one `Batch` edit: all or nothing, one undo step, and later one synchronized operation. A proposal that would close a cycle names the cycle and changes nothing.
- Before a run, the person runs the planner on its own, as any task runs today. The workflow is complete before it runs. A person who wants the shape early draws empty nodes first, or sets the planner to Chat, where the first proposal appears after one turn and changes after each reply.
- During a run, which workflow execution builds, an accepted proposal may only add nodes and fill nodes that have not started.
- The "talk to one agent" button places a Plan node in Chat mode and opens its conversation. Only the entry point is new.

### Interaction resumes the client's own session

A turn is one client process. An attempt holds one or more turns that share the client's own session. No process runs between turns, so a waiting node costs nothing and survives quitting iDevelop. All four clients passed this in the probe.

| Client | First turn | Next turn |
| --- | --- | --- |
| Claude Code | `--session-id <id>` that iDevelop chooses | `--resume <id>` |
| Codex | The id in `thread.started` | `codex exec resume --json <id> -` |
| Pi | `--session-id <id>` that iDevelop chooses | The same `--session-id <id>` |
| Antigravity CLI | The `conversation_id` it reports | `--conversation <id>` |

Each node has a conversation mode:

- **Autonomous** never waits for the person. The person can still write to it.
- **May ask** lets the agent end a turn with a question. The node then waits with "Waiting for you" and the question.
- **Chat** waits after every turn. The person replies or marks the node done.

The agent signals a question, a proposal, a verdict, or its answers to findings in one fenced JSON block at the end of its final message. One parser, `ResultBlock`, reads it for all four clients, without each client's schema flag, because Pi has none. A required block that is missing or unreadable gets one repair turn that asks only for the block.

The person acts from the inspector:

- **Send** queues a message as the next turn.
- **Stop and send** stops the running turn's process tree and resumes the session with the message.
- **Continue** on a finished or interrupted node starts a new attempt whose first turn resumes the old session. The old attempt stays as it was, so a quit during a run still reads Interrupted, as phase 3 promised.
- **Open in terminal** is offered only while the node waits. It copies the client's own command for its terminal UI on the same session, such as `claude --resume <id>`. iDevelop records the hand-off, and its record says that turns taken in the terminal are not in it.

The card's status pill gains "Waiting for you" in a PlanWeave status color. The run bar counts the waiting nodes and jumps to the next one. The inspector shows each turn as the person's message and the agent's final text, with tool activity folded, then a composer, then the section of the current `Pending` case.

| State | Client processes | Basis |
| --- | --- | --- |
| A turn runs | One client tree, as in phase 3 | Phase 3 |
| The node waits for the person | None | The design. Every probed turn exited |
| The next turn starts | One process start | Probed turns took 3.2 to 6.5 seconds including model time |

An embedded terminal was rejected. It needs a PTY on three operating systems and hides the structured event stream that decides success and drives the card. A live process per conversation was deferred. Claude Code and Antigravity CLI took two turns in one process, but Pi's RPC mode and Codex's app server were not probed, and a live process holds memory while the person thinks. A client row can add live input later without changing the node model.

### Storage

- A blueprint file is `idevelop.blueprint/1`, strict like the workflow file, with one file per blueprint. The project library is `.idp/blueprints/`, which Git tracks. The personal library is `iDevelop/blueprints/` in the per-user application data folder that already holds `settings.json`.
- **Derive** creates a new id at version 1 and records the parent. A save writes the next version only if the file still holds the version the editor opened. Otherwise it reports that the file changed, which covers two windows editing one blueprint.
- An app update that changes a built-in ships it as its next version. A saved workflow keeps the version it embedded, so an update never changes its prompts.
- The workflow file moves to `idevelop.workflow/3`. It adds the embedded blueprints, and each task gains its blueprint key, field values, and settings. Connection kinds are `dependency` and `context`. Opening a format 2 file converts it: each task becomes a node of the built-in Implement with the same text and agent, and each review connection becomes a dependency, which blocked the same way and did nothing at run time. The window says what it converted. The app writes only format 3. The format 2 reader goes once no format 2 file remains, as format 1 did.
- Attempt logs gain new event types, and `Requested` gains optional properties, never positional ones, so every phase 3 log folds as before. `AttemptStatus` gains `WaitingForInput`, the status the product direction already lists.

### Delivery

Each slice ends in a check that a reviewer can rerun. The real-window checks use the `verify-idevelop` skill on Windows.

1. **Talk to a node.** First, commit the conversation probe as a real-client check and extend it: the result block from each client, a turn stopped mid-tool-call and resumed, each client's read-only mode, and whether a slash command at the start of a non-interactive prompt runs. Then each client row resumes a session, attempts hold turns, and the inspector gains the composer with Send, Stop and send, Continue, and Open in terminal. No file format changes. *Check.* The probe passes on all four clients. The two-turn exchange runs through the Release window on each client. Every recorded phase 3 fixture folds to the same record.
2. **Typed nodes and format 3.** The node record, embedded blueprints, the built-in Implement, the new `Workflow.Apply` rules, the conversation modes, and the format 2 conversion. *Check.* A test shows that the Implement template renders today's prompt byte for byte. The format 2 sample opens, says what it converted, saves as format 3, and reopens. A May ask node asks and waits on each client.
3. **Blueprint libraries.** Both libraries, **Save as blueprint**, **Derive**, the blueprint editor with fields and templates, and the palette in the right panel. *Check.* Derive a type into the project library, place it, edit the blueprint to version 2, and place it again. After a save and a reopen, the first node keeps version 1. A built-in offers only **Derive**.
4. **Planning.** Proposals, the Plan and Architect built-ins, slot and type handles, ghost cards, and accepting a subset as one batch. *Check.* An Architect with two drawn empty nodes fills both and adds one. Accept applies as one undo step. A proposal that would close a cycle names it and applies nothing. A Chat planner shows nodes after its first turn.
5. **Review and approval.** `ReviewWork`, the two continuing sessions, the ledger, the repeat check over every earlier round, fix rounds as attempts of the subject, tree snapshots, the Review and Approval built-ins, and the removal of `ConnectionKind.Review`. *Check.* Tests drive the loop through scripted replies that agree in one round and in three, settle a dispute that the reviewer withdraws, repeat an earlier round, and receive guidance. A seeded defect is found, fixed, and approved with a Codex implementer and a Claude Code reviewer, and every round resumes the same two sessions.

Workflow execution follows. Its scheduler marks a node ready when every dependency predecessor finished with a handoff, calls `Next` for each ready node, and runs the step it returns. The scheduler, worktrees, and merge rules stay with that phase.

## The design arena

Both runners received one brief with the user's requirements, the code map, and the probe results. Each also received a different starting direction.

- **Runner A, closed kinds.** Three sealed node records (Implement, Plan, Review) implement capability interfaces such as `IConversational` and `IReviewing`. A blueprint is a template node over one kind, and a user type can change its role text, agent, access, and interaction, but not its fields. The review keeps its own loop log.
- **Runner B, capability composition.** One node record points to an embedded, versioned blueprint. A blueprint picks one of three works and declares its own fields and prompt templates. `INodeWork` is a pure decider, and `IConverses` is the one capability interface.

The two converged on review as a node, planning as approved graph edits, conversation through each client's session resume, one file per blueprint, and format 3 with a format 2 conversion.

| Criterion | A | B |
| --- | --- | --- |
| Requirement coverage | 4 | 5 |
| Data shape | 5 | 4 |
| Interfaces and extensibility | 3 | 4 |
| Interaction | 3 | 4 |
| Review and planning | 5 | 4 |
| Reader load and delivery | 3 | 4 |
| Total | 23 | 25 |

The cross-judge and the coordinator chose B as the base. Its extension point is one pure decider per work, which a standalone run and the scheduler call the same way. Its user types declare their own fields, which meets the user's request for richer, user-defined nodes. Its first slice delivers interaction without a file format change.

These parts came from A or from the judge:

- The repeat check compares against every earlier round, not only the last one, so it catches a loop that goes back and forth. From A. After the user's change, a repeat prompts both agents instead of pausing for the user.
- A `WorkKind` enum tag makes a new work fail the build. B's switch over records would only throw at run time. From A.
- Planners write slot and type handles and never choose agents, and the person can accept a subset. B let a planner set a new node's agent and accepted only all or nothing. From A.
- Reconcile still reports Interrupted, and Continue resumes in a new attempt. B had replaced the phase 3 Interrupted case with a waiting state. From the judge.
- Tree snapshots at each turn's start and end attribute a node's change and check read-only turns. Both candidates diffed the whole folder and trusted read-only modes. From the judge.
- The terminal hand-off is offered only while a node waits and is recorded. Both candidates let the client's terminal add turns silently. From the judge.
- The probe extension comes first in slice 1, before Stop and send depends on a resumable stopped turn. From A's delivery order.

These parts were rejected:

- A's per-kind node records, because a user type could not declare fields, and a node without an agent, such as an approval gate, had no way to run.
- A's six-call plan acceptance, which the judge flagged as a shallow module, and its id minting on every translation, which could place nodes twice after a crash.
- A's Architect with edit access. A user who wants design files in the repository derives an Architect with edit access.
- B's two-verdict stall rule and B's all-or-nothing acceptance, replaced as listed above.
- Fresh sessions for every review round, which both runners chose to keep each prompt small. The user chose continuing sessions on 2026-10-04, with small tickets keeping them small.
- Typed ports, which both runners rejected. They would add a port to every connection to express one rule, a review's single subject.
- One class per node type implementing `INode`, which users cannot write, and which would split the exit policy across classes as phase 3 rejected.

## Commands and observed results

| Command | Result |
| --- | --- |
| A scratch `probe-conversation.py` on Linux, two turns per client in a fresh Git repository | Claude Code, Codex, Pi, and Antigravity CLI each asked one question in turn 1, resumed the same session in a new process in turn 2, and wrote `banana` to `answer.txt`. Turns took 3.2 to 6.5 seconds. Claude Code and Antigravity CLI also took both turns in one live process over stdin, in 7.1 and 8.7 seconds. These are the first real client runs on Linux, through a script and not through iDevelop |
| The same probe with Pi on `deepseek/deepseek-flash` | The provider answered 401 while `pi auth check --provider deepseek --json` printed `ready`, and Pi exited 0. Pi passed on `openai-codex/gpt-5.6-luna` |
| `--help` of each client | Claude Code has `--json-schema` and `--permission-prompts host`. Codex has `exec --output-schema`, `codex queue`, and an experimental `app-server`. Pi has `--mode rpc`. Antigravity CLI has `--json-schema` and `--conversation`. Each client can open a session in its own terminal UI |
| Two architect runners and a cross-judge, fresh Opus 5.5 sessions at xhigh | Both packages were complete. The judge scored A 23 and B 25 and recommended B with three grafts from A |
| `node scripts/check-handoffs.mjs` | Exit 0 |

## Open issues

These are unverified, and slice 1 probes the first four:

- Whether every model reliably ends with a readable result block, especially through Pi.
- Whether a turn stopped mid-tool-call leaves a session that resumes cleanly on each client.
- Whether Claude Code's plan mode, Codex's read-only sandbox, and Antigravity CLI without accept-edits keep a non-interactive turn read-only.
- Whether each client runs a slash command, such as `/tdd`, at the start of a non-interactive prompt. If one does not, a PStack or Matt Pocock command reaches that agent as plain text.
- Whether each client keeps a long resumed session usable, for example by compacting it. A review loop resumes the same two sessions for as many rounds as agreement takes.
- Two agents that never agree keep the loop running, because the user chose agreement as the only end. The card shows the round count.
- `pi auth check` reported a provider ready that then answered 401. iDevelop uses that check for Pi's readiness.
- The shipped Antigravity CLI row reads the conversation id from the `init` event, and the probe read it from the `result` event. Slice 1 checks which one the row should trust.
- Before workflow execution gives each node its own worktree, tasks that run at once in one project folder blur the tree snapshots. A node's change can then include another task's edits, and a read-only turn can fail because of them.

## Next action

Slice 1, talk to a node, starts on its own branch.
