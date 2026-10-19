# iDevelop

iDevelop runs a workflow of nodes on a canvas, each node an abstraction of an agent. These are the words its workflow execution uses, in code, issues, and records, where a newcomer would read them the wrong way.

## Language

### Runs

**Run**:
One approved execution of a workflow, from its approval until it settles Completed or Stopped, with its own journal.
_Avoid_: job, execution

**Node run**:
A run that a node's Run started. It runs that node and any node the person runs while it is active, and each task after them once all of that task's dependency predecessors have results in the run, which may be results it carried from earlier runs. It completes once nothing more can start.
_Avoid_: partial run, run from here

**Standalone run**:
A task run on its own, outside any run.
_Avoid_: run, when no workflow run is meant

**Attempt**:
One try at a task, made of one or more turns. A retry, a review fix, and a Continue each start a new attempt, while a reply to a waiting attempt is its next turn.
_Avoid_: run, execution

**Turn**:
One client process within an attempt. The turns of an attempt share the client's own session.

**Controlling window**:
The one iDevelop window that may command a run. Every other window only reads it.

**Resume**:
The controlling window's permission to schedule a run's work, needed for a new run and after every reopen.
_Avoid_: Continue, Retry

**Continue**:
A new attempt that resumes an interrupted attempt's stored client session, as Continue fix does after the app closed.
_Avoid_: Resume, continuation

**Continuation**:
The next turn of the same attempt, started by a reply or by text queued during a turn.
_Avoid_: Continue, which starts a new attempt

**Retry**:
A new attempt of a task with a fresh client session. A person starts it for a task that is done or whose newest attempt ended without a result, and the run starts it by itself for a task whose result went out of date, once every task before it is done, and for an unstarted attempt whose inputs changed. An editing task's Retry continues in its worktree with its earlier change, and the new work of the tasks before it merges in before its agent starts ([ADR 0034](docs/adr/0034-a-retry-merges-new-inputs-into-its-earlier-change-and-an-out-of-date-task-runs-again.md)). A person's Retry takes an optional note that ends the agent's first prompt, which opens with the reason of a sent-back Approval node the task feeds ([ADR 0035](docs/adr/0035-ask-again-after-a-send-back-and-a-retry-note-to-the-agent.md)). A step the coordinator tries again after a busy refusal is not a Retry.
_Avoid_: Resume, rerun

**Close as stopped**:
A person's confirmed closure of an uncertain turn with no recorded root exit, as after a crash, so that nothing launches it again. It ends one attempt, not the run.
_Avoid_: Stop, Stop Workflow

**Client slot**:
A run's place for one task's starting or running client root. A run starts every ready task at once, up to a safety bound of 32 slots. Waiting, gates, settlement, and cleanup take none.

**Node worktree**:
The Git worktree a node's agent works in during a run, at `.worktrees/<run>/<node>`. An editing node's worktree is on a branch of its own, and each turn ends with one commit there. A read-only node's worktree is detached at the merge of its predecessors and commits nothing. Every attempt of the node, a Retry's included, works in the same worktree ([ADR 0032](docs/adr/0032-each-editing-node-works-in-its-own-worktree-kept-simple.md)).
_Avoid_: checkout, workspace

**Merge of predecessors**:
The commit a node's worktree starts from when its dependency predecessors hand on different commits, merged with `git merge-tree`. A conflict goes to a Merge node's agent, and the node starts once the merge is committed. Without a Merge node to take it, the conflict blocks the node with the conflicting paths until the person resolves it in the conflict's worktree and marks it resolved ([ADR 0033](docs/adr/0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)). The code calls it a join.
_Avoid_: fan-in

**Final merge**:
The merge of a finished run's work into the branch HEAD named at its approval, before the run completes. It waits while the folder that has the branch checked out has uncommitted changes, or while a rebase or a bisect holds the branch, and a conflict goes to the Merge node with no successors, or without one waits for the person to resolve it in the conflict's worktree and mark it resolved ([ADR 0033](docs/adr/0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).
_Avoid_: end-of-run merge, which names the same thing in the issues

### Results

**Result**:
A node's accepted, immutable output in a run, its report and artifacts, which its dependents read as input. A successful attempt is not a result until it is accepted.
_Avoid_: success

**Current result**:
A task's newest result. It supersedes the one before, which stays recorded.

**Result origin**:
What produced a result: an executed attempt, a standalone report that the run approval included, a person's gate approval, or an earlier run whose result a node run carried. Only an executed result comes from a client in the run.

**Earlier result**:
A task's result from a run of its workflow that has settled. It still counts as complete while it was its run's current, non-stale result, no later run ran the task, the task and the connections into it are unchanged, and each result it took counts the same way. Otherwise it is out of date.
_Avoid_: old result, cached result

**Carried result**:
An earlier result that a node run takes for a task it does not run, recorded in its own journal with its report and its artifacts. The earlier run's work is already in the branch the run starts from, so a carried result brings no code. Run Workflow carries nothing.
_Avoid_: imported result, reused result, which names a standalone report a run reuses

**Stale result**:
A task's current result that was built, directly or through other results, from a result that has since been superseded. It does not hand on. An agent task with one runs again by itself as a Retry once every task before it is done, and an Approval node takes a new gate request instead.
_Avoid_: outdated. A Stale answer is a different thing: an answer to a gate request that is no longer open.

### Reviews

**Interrupted fix**:
A review's fix round that closing iDevelop interrupted, or that a crash or a kill lost and the person then closed as stopped. It never starts again by itself and waits for the person's Continue fix or Retry fix.
_Avoid_: failed fix

**Continue fix**:
The person's choice to go on with an interrupted fix: a Continue in the fix's own client session.
_Avoid_: Resume, which never chooses a fix

**Retry fix**:
The person's choice to redo an interrupted fix round: a Retry in a fresh client session, in the subject's worktree with its files as they are.
_Avoid_: Retry alone, which names a new attempt of any task

### Approval and gates

**Preflight**:
The preview of what a run would approve, which Run Workflow opens for the whole workflow and a node's Run for that node and the tasks after it. It records nothing.

**Run approval**:
A person's confirmation of a preflight, which approves exactly one run.
_Avoid_: approval alone, which also names a gate's answer

**Approval intent**:
The durable note a run approval writes before any other side effect, so a crash finishes as the same run.

**Inclusion**:
A run approval's adoption of a report that a task produced on its own before the run, such as a planner's, as that task's result, so the run never runs the task. It brings no client session into the run.
_Avoid_: import

**Gate request**:
An Approval node's durable request for a person's answer on one fixed set of inputs. It creates no attempt, and superseded inputs or the person's Ask again give the node a new one.
_Avoid_: approval attempt

**Send back**:
A person's answer that refuses a gate request with a reason. It holds the node's dependents and is final for that request.
_Avoid_: reject, deny

**Ask again**:
The person's confirmed choice, after a send back, to open a new gate request on the same inputs. The sent-back request stays in the run's history with its reason ([ADR 0035](docs/adr/0035-ask-again-after-a-send-back-and-a-retry-note-to-the-agent.md)).

### Planning

**Slot**:
A task after a planner whose fields are all blank, which the planner's proposal may fill.
_Avoid_: client slot, which is a run's place for a running client

**Amendment**:
A recorded change to an approved run's workflow, from a run planner's proposal that the person accepted. It only adds tasks and connections and fills tasks that have not started, and a task counts as started once the plan of its start is recorded. An edit of the workflow document changes no run.
_Avoid_: edit, which changes only the document

### Agents

**Node**:
An abstraction of an agent: the model and effort it chooses, its system prompt, its instructions and skills, its conversation with a person, the input it reads from earlier agents or a person, and the output it leaves for later agents and the person. The owner set this on 2026-10-18 ([ADR 0031](docs/adr/0031-a-node-is-an-abstraction-of-an-agent.md)).

**Merge node**:
A built-in node whose agent resolves a run's merge conflicts: between its own predecessors, between the predecessors of a node whose conflicting branches connect into it, or in the final merge. Between nodes, its own step merges its predecessors' code and hands it on with their reports and artifacts, as an approval that needs no person does. Its agent runs only on a conflict, each one in a worktree at Git's merge with its markers, and iDevelop commits the agent's files on both parents. Its Retry continues from the files the failed attempt left ([ADR 0033](docs/adr/0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).
_Avoid_: merge agent, which the issues use for its agent alone

**Mark resolved**:
The person's action on a merge conflict that no Merge node takes, after they fixed its files in the conflict's worktree, which Open worktree adds and opens. iDevelop commits their files on the conflict's two parents, and the run goes on as after a Merge node's merge. It is refused while a conflicting file holds Git's conflict markers ([ADR 0033](docs/adr/0033-a-merge-node-resolves-a-runs-conflicts-and-a-finished-run-merges-into-its-branch.md)).

**Skill choice**:
The skills a node's agent may use, by its client's own names, kept in the task's agent settings. No choice means every skill the client loads. A choice holds exactly its skills: an allow-list for Claude Code and Pi, and for Codex every other listed skill turned off. Antigravity CLI takes none. A chosen skill that is no longer installed is skipped, and a skill list that can't be read leaves the choice unchecked rather than stopping the task.
_Avoid_: allow-list, which only two clients take

### Canvas

**Expanded card**:
A task's card unfolded in place to show what goes on now, its agent's pickers, and its actions. Expansion is this machine's view of the workflow: the cards around it slide aside on screen only, and the workflow's positions and file never change ([ADR 0024](docs/adr/0024-an-expanded-card-unfolds-in-place-and-stays-expanded-on-this-machine.md)).
_Avoid_: expanded node, and an expanded workflow, which is a sidebar row that shows its tasks

**Peek**:
A collapsed card's tooltip, a read-only look at what its expanded card shows first.
