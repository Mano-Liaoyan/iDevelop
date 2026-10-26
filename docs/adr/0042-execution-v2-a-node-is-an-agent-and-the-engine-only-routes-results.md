# Execution v2: a node is an agent, and the engine only routes results

A node is an agent whose output depends only on its basis, and an in-process engine starts every ready node and routes each result along the node's edges. Nothing else holds execution state: each node keeps its result history and its session id. The owner settled this model on 2026-10-25 in map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179), as an engine built from first principles (KISS, SOLID, DRY, YAGNI). It builds on [ADR 0031](0031-a-node-is-an-abstraction-of-an-agent.md).

## The model

1. **A node is an agent.** Its output, a report plus an optional commit, is determined only by its **basis**: its inputs (the latest results of its predecessors), its instruction (the rendered template and fields), and its model (client, model, and effort).
2. **Two node kinds in code.** `CliAgent` runs Claude Code, Codex, Pi, or Antigravity CLI through the existing client layer. `PersonAgent` approves or sends back. Review and Merge are **blueprints**, a prompt template plus an output format, run by `CliAgent`.
3. **The engine** is in-process and written here. Every ready node starts at once, and a node is ready when all its predecessors are completed and none of their results is stale. On completion, the node's result goes along every outgoing edge. Its message types borrow A2A's task states: working, input-required, completed, and failed.
4. **Communication** is one-way along edges, at completion. A node never talks to another agent during a turn. It may only ask the person, which makes it input-required.
5. **Feedback edges.** A review's reject or an approval's send back sends its findings or note to its predecessor or predecessors, which continue in their own session and worktree. The reviewer or approver then runs again on the new result. After 3 rounds, a per-node setting that defaults to 3, the reviewer waits for the person.
6. **Sessions.** A person's message and feedback continue the node's session. An upstream change and a Retry start a fresh session, which is given only the latest upstream results.
7. **Staleness** is a pure function. A result is stale when its recorded basis, its input result ids, its instruction text, and its model, differs from the node's current basis. Nothing is cascaded or stored.
8. **An upstream rerun while a node runs** stops the downstream turn and marks it stale. Once the upstream node completes, the downstream node runs again by itself in a fresh session. **A person who edits a running node**, its instruction, its model, or its edges, stops it and marks it stale, and the node waits for the person's Run.
9. **Retry** starts a fresh session in a clean worktree from the merge of its predecessors' commits.
10. **Git.** Each editing node has its own worktree and its own branch, `idp/<workflow>/<node>`. Several predecessors' commits merge with `git merge-tree`. A conflict goes to a connected Merge node. Without one, the node waits for the person to resolve the conflict in its worktree and mark it resolved. A root node starts from the HEAD of the project's branch.
11. **Merge into my branch** is offered only when every node is completed and nothing is stale. It merges every end node's commit into the checked-out branch. When the end nodes conflict with each other, it refuses and tells the person to add a Merge node after them. When the branch moved and the merge conflicts, it refuses and names the conflicting paths. The engine never merges into the person's branch by itself.
12. **State** is each node's result history plus its session id, kept in one append-only log per node and replayed when the project opens. A turn that was running when the app closed reopens as input-required, interrupted, and the person chooses Continue, which goes on with the session, or Retry.
13. **Generate Workflow** is design time. A planner produces a workflow document, and the person can edit it at any time. The graph is always editable.
14. **One window per project.** A lock file keeps a project to one window, and a second window shows that the project is already open in another window.

## The engine's surface for the desktop

```
event NodeChanged(NodeId, NodeState)       // Idle | Running | InputRequired(why) | Completed | Failed | Stale
NodeState State(NodeId); IReadOnlyList<NodeResult> History(NodeId)
string? CanRun(NodeId)                      // null or the reason it cannot run
Task Run(NodeId); Task RunWorkflow()        // RunWorkflow = every node not completed or stale, in dependency order
Task Retry(NodeId); Task Stop(NodeId); Task StopAll()
Task Send(NodeId, string text, bool interrupt)
Task Answer(NodeId, string key, QuestionsReply) // key: the engine's own, from InputRequired.Question
Task Approve(NodeId); Task SendBack(NodeId, string note)
Task MarkResolved(NodeId)
bool CanMergeIntoBranch; Task MergeIntoBranch()
IConversation Conversation(NodeId)          // snapshot, paging, new conversation, open in terminal
```

## Considered options

- **An agent framework or protocol.** herdr, Orca, A2A, ACP, and Microsoft Agent Framework were evaluated and rejected. The engine needs only to start ready nodes and route results, and each would bring a model of its own to fit around. Only A2A's names for task states are borrowed. ACP could later replace the per-client protocol parsers.

## Consequences

- Routing a review's findings to the implementer whose change touched each file, and detecting a review round that repeats an earlier one, are left out. Either can return later as a routing rule without changing the engine.
- The engine is meant to stay under about 2,000 lines. A change that would push it past that is raised on the map first.

Source: map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179), settled with the owner on 2026-10-25.
