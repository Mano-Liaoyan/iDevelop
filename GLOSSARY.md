# iDevelop

iDevelop runs a workflow of nodes on a canvas, each node an abstraction of an agent. These are the words its workflow execution uses, in code, issues, and records, where a newcomer would read them the wrong way. [ADR 0042](docs/adr/0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md) holds the execution model.

## Language

### Agents

**Node**:
An abstraction of an agent: the model and effort it chooses, its system prompt, its instructions and skills, its conversation with a person, the input it reads from earlier agents or a person, and the output it leaves for later agents and the person. The owner set this on 2026-10-18 ([ADR 0031](docs/adr/0031-a-node-is-an-abstraction-of-an-agent.md)).

**CliAgent**:
The node kind in code whose agent is a coding client, Claude Code, Codex, Pi, or Antigravity CLI. Implement, Plan, Architect, Review, and Merge nodes are all `CliAgent` nodes with different blueprints.
_Avoid_: task runner

**PersonAgent**:
The node kind in code whose agent is the person. An Approval node is one: it approves, which completes it with a result that forwards its inputs, or sends back ([ADR 0007](docs/adr/0007-a-person-agent-approves-or-sends-back.md)).
_Avoid_: gate

**Merge node**:
A node of the built-in Merge blueprint, run by `CliAgent`, whose agent resolves a conflict between the predecessors that connect into it, in a worktree at Git's merge with its markers. iDevelop commits the agent's files on both parents ([ADR 0033](docs/adr/0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)). A review after a Merge node reviews the merge ([ADR 0037](docs/adr/0037-a-review-after-a-merge-node-reviews-the-merge.md)).
_Avoid_: merge agent, which the issues use for its agent alone

**Skill choice**:
The skills a node's agent may use, by its client's own names, kept in the node's agent settings. No choice means every skill the client loads. A choice holds exactly its skills: an allow-list for Claude Code and Pi, and for Codex every other listed skill turned off. Antigravity CLI takes none. A chosen skill that is no longer installed is skipped, and a skill list that can't be read leaves the choice unchecked rather than stopping the node.
_Avoid_: allow-list, which only two clients take

### Execution

**Engine**:
The in-process part of Core that starts every ready node, routes each result along the node's edges, and keeps each node's log. It holds no state beyond the nodes' logs.
_Avoid_: run, coordinator, scheduler

**Basis**:
What a node's output depends on: its inputs (the latest results of its predecessors), its instruction (its rendered template and fields), and its model (client, model, and effort). Each result records the basis it was built from.

**Ready**:
A node whose predecessors are all completed and none of whose predecessors' results is stale. Every ready node starts at once ([ADR 0018](docs/adr/0018-every-ready-node-starts-at-once.md)).
_Avoid_: unblocked

**Input required**:
A node's state while it waits for the person: a question, an approval, a conflict to resolve, a turn the app's closing interrupted, or a review that reached its round limit. No client process runs while a node waits.
_Avoid_: blocked, paused

**Attempt**:
One fresh session of a node's agent, made of one or more turns. A Retry and an upstream change each start a new attempt, while a person's message and a feedback edge continue the current one.
_Avoid_: run, execution

**Turn**:
One client process within an attempt. The turns of an attempt share the client's own session.

**Continue**:
The next turn of a node's session, started by a person's message or by a feedback edge, and the person's choice for a turn that the app's closing interrupted.
_Avoid_: Retry, which starts a fresh session

**Retry**:
A new attempt of a node in a fresh session, in a clean worktree from the merge of its predecessors' latest commits ([ADR 0032](docs/adr/0032-each-editing-node-works-in-its-own-worktree.md)). A node that runs again after an upstream change starts the same way.
_Avoid_: rerun

**Node worktree**:
The Git worktree a node's agent works in, at `.worktrees/<workflow>/<node>` in the project folder. For a project below its repository's top, the agent works in the project's folder of that worktree ([ADR 0036](docs/adr/0036-a-project-folder-may-sit-below-its-repository-s-top.md)). An editing node's worktree is on its own branch, `idp/<workflow>/<node>`, and each turn ends with one commit there. A read-only node's worktree is detached at the merge of its predecessors and commits nothing. A continued session keeps the worktree's files, and a fresh session starts it clean ([ADR 0032](docs/adr/0032-each-editing-node-works-in-its-own-worktree.md)).
_Avoid_: checkout, workspace

**Merge of predecessors**:
The commit a node's worktree starts from when its predecessors hand on different commits, merged with `git merge-tree`. A conflict goes to the Merge node they connect into, or without one makes the node input-required until the person resolves it in the node's worktree and marks it resolved ([ADR 0033](docs/adr/0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)).
_Avoid_: fan-in

**Mark resolved**:
The person's action on a merge conflict that no Merge node takes, after they fixed its files in the node's worktree, which Open worktree opens. iDevelop commits their files on the conflict's parents, and the node goes on as after a Merge node's merge. It is refused while a conflicting file holds Git's conflict markers ([ADR 0033](docs/adr/0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)).

**End node**:
A node with no successors. Merge into my branch merges the end nodes' commits.

**Merge into my branch**:
The person's action that merges every end node's commit into the checked-out branch. It is offered only while every node is completed and nothing is stale. It refuses when the end nodes conflict with each other, and tells the person to add a Merge node after them, and when the branch moved and the merge conflicts, with the conflicting paths. The engine never merges into the person's branch by itself ([ADR 0033](docs/adr/0033-a-merge-node-resolves-conflicts-and-merge-into-my-branch-waits-for-a-complete-workflow.md)).
_Avoid_: merge to main

### Results

**Result**:
A node's completed, immutable output, a report plus an optional commit, with the basis it was built from. Its successors read it as input.
_Avoid_: success

**Latest result**:
A node's newest result. It supersedes the one before, which stays in the node's history.

**Stale result**:
A latest result whose recorded basis differs from the node's current basis, because a predecessor has a newer result, or the person changed the node's instruction, model, or edges. Staleness is computed, never stored or cascaded. A node with a stale predecessor is not ready ([ADR 0034](docs/adr/0034-a-result-goes-stale-when-its-basis-changes.md)).
_Avoid_: outdated

### Reviews and feedback

**Subject**:
The one node a review reviews: its predecessor that changes the project, an editing agent or a Merge node.
_Avoid_: reviewee, target

**Feedback edge**:
The route a review's reject or an approval's send back takes, with its findings or note, to the node's direct predecessors, which continue in their own session and worktree. The reviewer or approver then runs again on the new result ([ADR 0041](docs/adr/0041-feedback-goes-to-the-direct-predecessor.md)).

**Round**:
One reject along a review's feedback edge and its predecessor's new result. After a node's round limit, 3 by default, the reviewer waits for the person ([ADR 0040](docs/adr/0040-after-three-review-rounds-the-reviewer-waits-for-the-person.md)).

**Send back**:
A person's answer on an Approval node that refuses its inputs with a note, which goes along the feedback edge to its predecessors ([ADR 0035](docs/adr/0035-send-back-is-a-feedback-edge.md)).
_Avoid_: reject, which names a review's verdict

### Canvas

**Expanded card**:
A node's card unfolded in place to show what goes on now, its agent's pickers, and its actions. Expansion is this machine's view of the workflow: the cards around it slide aside on screen only, and the workflow's positions and file never change ([ADR 0024](docs/adr/0024-an-expanded-card-unfolds-in-place-and-stays-expanded-on-this-machine.md)).
_Avoid_: expanded node, and an expanded workflow, which is a sidebar row that shows its nodes

**Peek**:
A collapsed card's tooltip, a read-only look at what its expanded card shows first.
