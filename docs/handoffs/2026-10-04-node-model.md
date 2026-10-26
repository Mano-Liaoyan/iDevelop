# Node model

## Task

On 2026-10-04 the user made the node model the top priority, ahead of workflow execution: "once the node is designed well, executing the rest is comparatively easy." The user asked for typed nodes, node types that users define and save as blueprints, a review loop between two agents, and a way to talk to a running agent. This record holds the design. The user accepted it on 2026-10-04 with their own review loop, as [What the user decided](#what-the-user-decided) records.

## What the user decided

The user wrote in Chinese. This summary keeps the meaning.

- Nodes have types. An implement node reads a plan and executes it with the agent, model, and reasoning setting the user chose. A plan node makes a plan and hands it to implement nodes. An architect node designs. A review is a node. Connections express relationships.
- A user may bring their own commands or skill set, such as Matt Pocock's skills, into a node.
- Nodes are built on interfaces. A node that needs a capability, such as interaction, implements that capability's interface.
- Users define their own nodes and node types. Any node can be saved as a blueprint. Built-in blueprints are read-only, and a user changes one by deriving a new blueprint and saving it.
- A review is two agents that iterate until both are satisfied, then hand the result on. Each agent's context window stays clean, because not every model has a large window and a bloated one makes the model worse.
- Interaction matters most. When an agent asks how to proceed, the user answers, guides, or corrects it. Some nodes only implement and never ask. The solution must cost little, work with all four clients, and leave room to extend.
- How a plan reaches implement nodes "depends". A planner usually creates the nodes, but sometimes the nodes exist already, or the user draws an architect and three to five empty nodes and fills them later. The long-term goal is a button where the user talks to one agent and the whole workflow appears.
- Placing a node copies its blueprint and records which blueprint and version it came from. A later edit of the blueprint changes only nodes placed afterwards.
- Blueprints live in a personal library in the user's folder and in a project library under `.idp/blueprints/`.

The user also decided these on 2026-10-04:

- An accepted proposal only adds nodes and fills nodes that have not started.
- Changing one node's fields, template, or work means deriving a blueprint first.
- The Approval node stays in the first release.
- The implement node does its work and tells the reviewer what it changed. The reviewer judges and says what is wrong. The implement node fixes it and hands it back. They go back and forth until both agree, and each agent keeps its own session. On 2026-10-25 the owner decided that the reviewer waits for the person after a set number of rounds ([ADR 0040](../adr/0040-after-three-review-rounds-the-reviewer-waits-for-the-person.md)).

## Design

### A node type is a blueprint over one work

A blueprint is data. It has an id and a version, a name, a description, the blueprint it was derived from, a work, a list of fields, and default settings. The work says what the node does, and it is a closed set, `WorkKind`:

| Work | Holds | Run by |
| --- | --- | --- |
| Agent | Read-only or edit access, whether it proposes graph edits, a prompt template | `CliAgent` |
| Review | A reviewer template and a fix template | `CliAgent` |
| Merge | A prompt template for the merge agent | `CliAgent` |
| Person | Nothing. A person approves or sends back | `PersonAgent` |

A field has a key, a label, a shape (one line or several), a required flag, and a default. A prompt template uses `{{title}}`, the field keys, `{{inputs}}`, and the work's own variables. iDevelop appends its result contract after the rendered template, so no blueprint can remove it.

A node has an id and a title, the key of the blueprint it came from (`id@version`), its field values, and its settings: the agent, model, and reasoning, plus a conversation mode. The workflow embeds one copy of every blueprint version its nodes use. Placing a node copies the blueprint in, so a later edit of the library blueprint changes nothing already placed, and the file opens on a machine without the author's library. `Workflow.Apply` drops a copy that no node uses, so the two collections never need to be kept in sync.

| Part | Fixed by the blueprint version | A node may change it |
| --- | --- | --- |
| The work, its access, and whether it proposes | Yes | No |
| The fields and the prompt templates | Yes | No |
| The title and the field values | Seeds the defaults | Yes |
| The agent, model, reasoning, and conversation mode | Seeds the defaults | Yes |

The rule is "the blueprint fixes the structure, the node sets the values". A node's connections and results keep their meaning because its work never changes under them. To change the structure, the user derives a blueprint, which is one click from any node. "Save as blueprint" turns a node's values and settings into the defaults of a new blueprint.

A user-defined node type is a blueprint, made in the window without code. It sets the name, the description, the work, the access, whether the agent proposes, its own fields, its prompt templates, and its default agent and conversation mode. It cannot add a work, change the result contract, change graph rules, or run a script. A new work needs code: one `WorkSpec` case and one `WorkKind` value, and the build fails on an enum switch that misses it.

### Six built-in types

Built-ins live in the app under the reserved `idevelop.` id prefix, so they are read-only by construction. Their templates are plain English and name no skill library. A user who wants a command or a skill derives a blueprint and writes its name into the template or a field.

| Type | Work | Consumes | Produces | Default conversation |
| --- | --- | --- | --- | --- |
| Implement | Agent, edit | Its fields and the reports of earlier nodes | Its final report and a commit | Autonomous |
| Plan | Agent, read-only, proposes | A goal, constraints, earlier reports, the empty nodes after it | A proposal of nodes and connections, and a report | May ask |
| Architect | Agent, read-only, proposes | A brief, earlier reports, the empty nodes after it | A design as its report, and a proposal that fills the empty nodes | May ask |
| Review | Review | Its subject's ticket, report, and change, other inputs as context | A verdict | Autonomous |
| Approval | Person | Its inputs | The person's decision, passing the inputs on | Autonomous |
| Merge | Merge | The commits of the nodes before it | A merge commit and a report | Autonomous |

### Review is a node, and connections express flow

Connections are `dependency`, which waits for its source and receives its result, and `context`, which reads the source's latest result without waiting. In `Implement → Review → Next`, the review's subject is its one dependency predecessor that changes the project, an editing agent or a Merge node ([ADR 0037](../adr/0037-a-review-after-a-merge-node-reviews-the-merge.md)). `Workflow.Apply` rejects a second one. The loop runs along a feedback edge, never as a connection, so the dependency graph stays acyclic.

1. The reviewer's turn starts its session with the reviewer template, the subject's ticket, its report of what it changed and why, and its change. It answers with a verdict: approve, or reject with findings, each with a severity and a location ([ADR 0029](../adr/0029-a-finding-has-a-severity-and-a-location.md)).
2. A reject sends the findings, in the fix template, along the feedback edge to the subject, which continues its own session with them ([ADR 0041](../adr/0041-feedback-goes-to-the-direct-predecessor.md)).
3. Once the subject completes again, the reviewer reviews its new result. Then step 2 repeats.

Each agent reads its own history plus the other side's short message, so a round adds one message to each session. Small tickets keep both sessions small. Every finding names the change that would settle it. The loop ends when the reviewer approves. After the review's round limit, 3 by default, the reviewer waits for the person's guidance ([ADR 0040](../adr/0040-after-three-review-rounds-the-reviewer-waits-for-the-person.md)). A verdict that iDevelop cannot read makes the reviewer wait for the person with the reason.

### Planning produces graph edits the person approves

Every agent that plans produces one `Proposal`. A Plan node, an Architect node, and Generate Workflow all produce it. A proposal can add nodes, fill nodes, and add connections. It cannot delete, move, or retype anything. Filling an empty node the person drew and creating a new node are the same mechanism.

- The prompt lists the empty nodes after the planner as handles such as `slot-1`, and the blueprints the planner may place as handles such as `type-2`. The agent writes only handles and never writes ids. A new node whose blueprint has an agent of its own takes that agent, so the person's choices come with it. For any other new node, the planner proposes an agent ([ADR 0019](../adr/0019-a-planner-chooses-each-new-task-s-agent.md)).
- iDevelop parses the proposal once, where it arrives, and mints the new ids from the plan's id, so the same log always gives the same ids.
- A dry run through `Workflow.Apply` draws the proposed nodes as dashed ghost cards and fills the drawn ones in place. The person accepts all of them or unticks some first. Accepting applies one `Batch` edit: all or nothing, and one undo step. A proposal that would close a cycle names the cycle and changes nothing.
- Planning is design time. A person who wants the shape early draws empty nodes first, or sets the planner to Chat, where the first proposal appears after one turn and changes after each reply. The graph stays editable at any time ([ADR 0042](../adr/0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).
- Generate Workflow places a Plan node in Chat mode and runs it.

### Interaction resumes the client's own session

A turn is one client process. An attempt holds one or more turns that share the client's own session. No process runs between turns, so a waiting node costs nothing and survives quitting iDevelop. All four clients passed this in the probe. The [agent client reference](../agent-clients.md#sessions) lists each client's session id and resume arguments.

Each node has a conversation mode:

- **Autonomous** never waits for the person. The person can still write to it.
- **May ask** lets the agent end a turn with a question. The node then waits with the question.
- **Chat** waits after every turn. The person replies, or approves the last reply as the node's result.

The agent signals a question, a proposal, or a verdict in one fenced `idevelop` JSON block at the end of its final message. One parser, `ResultBlock`, reads it for all four clients, without each client's schema flag, because Pi has none.

The person acts from the inspector, the expanded card, or the conversation view:

- **Send** queues a message as the next turn of the session.
- **Stop and send** stops the running turn and resumes the session with the message.
- **Open in terminal** copies the client's own command for its terminal UI on the same session, such as `claude --resume <id>`, after a change into the project folder. iDevelop records the hand-off in the node's log.

An embedded terminal was rejected. It needs a PTY on three operating systems and hides the structured event stream that decides success and drives the card. A live process per conversation was rejected too, because it holds a process while the person thinks ([ADR 0026](../adr/0026-a-continued-session-reads-as-one-conversation.md)).

### Storage

- A blueprint file is `idevelop.blueprint/1`, strict like the workflow file, with one file per blueprint. The project library is `.idp/blueprints/`, which Git tracks. The personal library is `iDevelop/blueprints/` in the per-user application data folder that holds `settings.json`.
- **Derive** creates a new id at version 1 and records the parent. A save writes the next version only if the file still holds the version the editor opened. Otherwise it reports that the file changed, which covers two windows editing one blueprint.
- An app update that changes a built-in ships it as its next version. A saved workflow keeps the version it embedded, so an update never changes its prompts.
- The workflow file is `idevelop.workflow/3`. It embeds the blueprints, and each task holds its blueprint key, field values, and settings. Connection kinds are `dependency` and `context`. Opening a format 2 file converts it: each task becomes a node of the built-in Implement with the same text and agent, and each of its review connections becomes a dependency. The window says what it converted. The app writes only format 3. The format 2 reader goes once no format 2 file remains.

### Delivery

The five slices are built: talking to a node, typed nodes with workflow format 3 and conversation modes, blueprint libraries with **Save as blueprint**, **Derive**, and the blueprint editor, planning with slot and type handles, ghost cards, and accepting a subset as one batch, and review and approval. Real-client tests, opt-in through `IDEVELOP_REAL_CLIENTS`, ran a May ask node and an Autonomous Architect on Linux on 2026-10-05. The engine of [ADR 0042](../adr/0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md) runs these nodes.

## Commands and observed results

| Command | Result |
| --- | --- |
| A scratch `probe-conversation.py` on Linux, two turns per client in a fresh Git repository | Claude Code, Codex, Pi, and Antigravity CLI each asked one question in turn 1, resumed the same session in a new process in turn 2, and wrote `banana` to `answer.txt`. Turns took 3.2 to 6.5 seconds. Claude Code and Antigravity CLI also took both turns in one live process over stdin, in 7.1 and 8.7 seconds. |
| The same probe with Pi on `deepseek/deepseek-flash` | The provider answered 401 while `pi auth check --provider deepseek --json` printed `ready`, and Pi exited 0. Pi passed on `openai-codex/gpt-5.6-luna` |
| `--help` of each client | Claude Code has `--json-schema` and `--permission-prompts host`. Codex has `exec --output-schema`, `codex queue`, and `app-server`. Pi has `--mode rpc`. Antigravity CLI has `--json-schema` and `--conversation`. Each client can open a session in its own terminal UI |

## Open issues

The probe answered four questions on Linux on 2026-10-04 and 2026-10-05:

- Each client ended its final message with a readable result block in 2 of 2 samples.
- Each client resumed its session after a turn was stopped mid-tool-call.
- A fresh read-only turn wrote nothing on Claude Code in plan mode, Codex with a read-only sandbox and approvals off, and Antigravity CLI with `--mode plan`. Without a mode, Antigravity CLI starts in request-review and wrote the file in 2 of 3 runs. Pi has no read-only mode. On a resumed session, Claude Code and Codex held read-only, but Antigravity CLI resumed with `--mode plan` still wrote the file in 4 of 4 runs. A reviewer or planner session must therefore start read-only.
- A project-local command ran on each client in one sample: `.claude/commands/` through `/name` on Claude Code, `.agents/skills/` through `$name` on Codex and `/name` on Antigravity CLI, and `.pi/prompts/` through `/name` on Pi. An agent could also have read the command file on its own, so one sample does not settle that.

These remain unverified:

- Whether each client keeps a long resumed session usable, for example by compacting it. A review loop resumes the same two sessions for each round.
- `pi auth check` reported a provider ready that then answered 401. iDevelop uses that check for Pi's readiness.
- The Antigravity CLI row reads the conversation id from the `init` event, and the probe read it from the `result` event. In the probe both events reported the same id, so the row keeps `init`.
