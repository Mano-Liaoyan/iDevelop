# iDevelop

iDevelop coordinates coding agents over a workflow of tasks on a node canvas. These are the words its workflow execution uses, in code, issues, and records, where a newcomer would read them the wrong way.

## Language

### Runs

**Run**:
One approved execution of a workflow, from its approval until it settles Completed or Stopped, with its own journal.
_Avoid_: job, execution

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
A run's single place for a starting or running client. Waiting, gates, settlement, publication, and cleanup take none.

### Results

**Result**:
A task's accepted, immutable outcome in a run, its code, report, and artifacts, which hands on to its dependents. A successful attempt is not a result until it is published or accepted.
_Avoid_: output, success

**Current result**:
A task's newest result. It supersedes the one before, which stays recorded.

**Result origin**:
What produced a result: an executed attempt, a reused standalone report, an approved rebase, or a person's gate approval. Only an executed result comes from a client in the run.

**Stale result**:
A task's current result that was built, directly or through other results, from a result that has since been superseded. It does not hand on. An agent task with one needs attention, while an Approval node takes a new gate request instead.
_Avoid_: outdated. A Stale answer is a different thing: an answer to a gate request that is no longer open.

**Rebase**:
A person-approved replay of a stale result's recorded change onto its current inputs, as one new commit, with no client run. Its result has the rebased origin.
_Avoid_: git rebase, which iDevelop does not run

### Blocks

**Block**:
A durable record, with evidence, that holds a task back until a recorded recheck or repair resolves it, such as a dirty checkout or a conflicting join. In a run, a task under a block is Blocked, and a task that only waits for its dependencies is Pending, although the standalone schedule and the product direction also call that task blocked.

**Drift**:
A change to a task's checkout after its result was accepted or its baseline was recorded. It becomes a block and never changes the accepted result.

**Publishing attempt**:
The attempt whose checkout a task's current result keeps, where that task's drift is recorded and looked for.

### Approval and gates

**Preflight**:
Run Workflow's preview of what a run would approve. It records nothing.

**Run approval**:
A person's confirmation of a preflight, which approves exactly one run on one base.
_Avoid_: approval alone, which also names a gate's answer

**Approval intent**:
The durable note a run approval writes before any other side effect, so a crash finishes as the same run.

**Snapshot base**:
A commit of the person's uncommitted work over HEAD, which a run can start from instead of HEAD while their branch, index, and files stay as they were.

**Gate request**:
An Approval node's durable request for a person's answer on one fixed set of inputs. It creates no attempt, and only superseded inputs give the node a new one.
_Avoid_: approval attempt

**Send back**:
A person's answer that refuses a gate request with a reason. It holds the node's dependents and is final for that request.
_Avoid_: reject, deny
