# iDevelop

iDevelop coordinates coding agents over a workflow of tasks on a node canvas. These are the words its workflow execution uses, in code, issues, and records, where a newcomer would read them the wrong way.

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
A new attempt of a task with a fresh client session, started only by a person. A step the coordinator tries again after a busy refusal is not a Retry.
_Avoid_: Resume, rerun

**Client slot**:
A run's place for one task's starting or running client root. A run starts every ready task at once, up to a safety bound of 32 slots. Waiting, gates, settlement, publication, and cleanup take none.

### Results

**Result**:
A task's accepted, immutable outcome in a run, its code, report, and artifacts, which hands on to its dependents. A successful attempt is not a result until it is published or accepted.
_Avoid_: output, success

**Current result**:
A task's newest result. It supersedes the one before, which stays recorded.

**Result origin**:
What produced a result: an executed attempt, a standalone report that the run approval included, an approved rebase, a person's gate approval, or an earlier run whose result a node run carried. Only an executed result comes from a client in the run.

**Earlier result**:
A task's result from a run of its workflow that has settled. It still counts as complete while it was its run's current, non-stale result, no later run ran the task, the task and the connections into it are unchanged, and each result it took counts the same way. Otherwise it is out of date.
_Avoid_: old result, cached result

**Carried result**:
An earlier result that a node run takes for a task it does not run, recorded in its own journal with its code replayed onto the run's base, its report, and its artifacts. One whose code conflicts with the base is not carried, and its task must run again. Run Workflow carries nothing.
_Avoid_: imported result, reused result, which names a standalone report a run reuses

**Stale result**:
A task's current result that was built, directly or through other results, from a result that has since been superseded. It does not hand on. An agent task with one needs attention, while an Approval node takes a new gate request instead.
_Avoid_: outdated. A Stale answer is a different thing: an answer to a gate request that is no longer open.

**Rebase**:
A person-approved replay of a stale result's recorded change onto its current inputs, as one new commit, with no client run. Its result has the rebased origin.
_Avoid_: git rebase, which iDevelop does not run

### Reviews

**Interrupted fix**:
A review's fix round that closing iDevelop interrupted, or that a crash or a kill lost and the person then closed as stopped. It never starts again by itself and waits for the person's Continue fix or Retry fix.
_Avoid_: failed fix

**Continue fix**:
The person's choice to go on with an interrupted fix: a Continue in the fix's own client session, on the checkout as the fix left it.
_Avoid_: Resume, which never chooses a fix

**Retry fix**:
The person's choice to redo an interrupted fix round: a Retry in a fresh client session, after the round's unfinished work is salvaged and the checkout reset.
_Avoid_: Retry alone, which names a new attempt of any task

### Blocks

**Block**:
A durable record, with evidence, that holds a task back until a recorded recheck or repair resolves it, such as a dirty checkout or a conflicting join. In a run, a task under a block is Blocked, and a task that only waits for its dependencies is Pending, although the standalone schedule and the product direction also call that task blocked.

**Drift**:
A change to a task's checkout after its result was accepted or its baseline was recorded. It becomes a block and never changes the accepted result.

**Preserve and restore**:
A person's repair of a drift or capture block: Preserve retains the checkout as it is now, and Restore moves it back to its recorded baseline after a preview. It never moves shared refs or the stash, and it never gives a rejected capture a result.
_Avoid_: git restore, which iDevelop does not run

**Close as stopped**:
A person's confirmed closure of an uncertain turn with no recorded root exit, as after a crash, so that nothing launches it again. It ends one attempt, not the run.
_Avoid_: Stop, Stop Workflow

**Publishing attempt**:
The attempt whose checkout a task's current result keeps, where that task's drift is recorded and looked for.

### Approval and gates

**Preflight**:
The preview of what a run would approve, which Run Workflow opens for the whole workflow and a node's Run for that node and the tasks after it. It records nothing.

**Run approval**:
A person's confirmation of a preflight, which approves exactly one run on one base.
_Avoid_: approval alone, which also names a gate's answer

**Approval intent**:
The durable note a run approval writes before any other side effect, so a crash finishes as the same run.

**Snapshot base**:
A commit of the person's uncommitted work over HEAD, which a run can start from instead of HEAD while their branch, index, and files stay as they were.

**Inclusion**:
A run approval's adoption of a report that a task produced on its own before the run, such as a planner's, as that task's result, so the run never runs the task. It brings no code, ownership, or client session into the run.
_Avoid_: import

**Gate request**:
An Approval node's durable request for a person's answer on one fixed set of inputs. It creates no attempt, and only superseded inputs give the node a new one.
_Avoid_: approval attempt

**Send back**:
A person's answer that refuses a gate request with a reason. It holds the node's dependents and is final for that request.
_Avoid_: reject, deny

### Planning

**Slot**:
A task after a planner whose fields are all blank, which the planner's proposal may fill.
_Avoid_: client slot, which is a run's place for a running client

**Amendment**:
A recorded change to an approved run's workflow, from a run planner's proposal that the person accepted. It only adds tasks and connections and fills tasks that have not started, and a task counts as started once the plan of its start is recorded. An edit of the workflow document changes no run.
_Avoid_: edit, which changes only the document
