# Run a whole workflow

A user runs the shown workflow from `Run Workflow` beside `Generate`, without selecting a node. A preflight sheet lists every task with its agent and inputs, what keeps the workflow from starting, the base it starts from, and how tasks are isolated. `Start run` approves one run and starts it. A bar at the bottom of the canvas shows the run's status, how many tasks are done, and what goes on now, with `Resume` and `Stop Workflow`. While the run owns a task, its card, its inspector, and its conversation show the run's view of it. An Approval node waits for the person in the inspector. Switching projects or workflows stops nothing, and the sidebar's dots show a hidden run's activity. The run's journal is `.idp/runs/<workflow-id>/<run-id>/events.jsonl`.

## Sub-features

- `run-preflight` opens the preflight without a selection, lists the tasks, agents, and inputs, and starts nothing on `Cancel`.
- `run-gap` lists what keeps the workflow from starting, keeps `Start run` off, and `Show` selects the named task.
- `run-base` offers HEAD or a snapshot of uncommitted work, and says that ignored files never reach a task.
- `run-start` approves one run, shows `WorkflowRunBar` with `Running`, and hides `Run Workflow` while the run is active.
- `run-cards` shows each task's state in the run on its card and in the inspector's `RunTaskStatus`, and hides the task's own `Run` and composer.
- `run-conversation` opens a run task's conversation from its card's attention glyph or `Open conversation`, and a reply goes through the run.
- `run-gate` shows an Approval node's request in the inspector's `Approval` section, with `Approve` and `Send back`.
- `run-stop` records `Stop Workflow`, stops the running task, and starts no other.
- `run-conversation-strip` repeats the run's status, `Resume`, and `Stop Workflow` in the conversation view while it covers the canvas.
- `run-retained` keeps a run going while another project is shown, with `ProjectRunning`, `WorkflowRunning`, `ProjectWaiting`, and `WorkflowWaiting`.
- `run-leave` asks before closing the window during an active run, and `Stop and leave` records the stop.

## How to get to it (user POV)

- Choose `Run Workflow` at the canvas's top right.
- In the preflight, choose `Start run`, `Cancel`, a base, `Show` beside a gap, or `Show the active run`.
- In the run bar, choose `Resume`, `Stop Workflow`, or, once the run has settled, the dismiss button.
- Choose the waiting pill at the top of the canvas, or a card's attention glyph, to reach a task that waits.
- Choose an Approval node in the sidebar, then `Approve` or `Send back` in the inspector.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.
- The sample's first task asks for Claude Code, which a fake session blocks. Choose it with `Select-Element (Get-SidebarTasks $s.Window)[0]` and `Select-PickerEntry $s 'TaskClient' 'Codex'`. The third task has no agent; choose it and pick `Codex` too.
- The project folder must be a Git repository with a commit. Run `git -C $s.Project init -q -b main`, `git -C $s.Project add -A ':!.idp'`, and `git -C $s.Project -c user.name=Verify -c user.email=verify@example.test commit -q -m base`.
- `$s.Gate` does not exist, so each turn waits until it does.

- **Preflight.** Run `Invoke-Element (Find-ById $s.Window 'RunWorkflow')`. `Wait-Until { Find-ById $s.Window 'PreflightTasks' 1 } 30` returns the list. `Find-AllById $s.Window 'PreflightTask'` reads the three titles in task order, and `PreflightBase` reads `HEAD, <commit> on main`. Run `Save-Evidence $s 'preflight'`.
- **Cancel.** Run `Invoke-Element (Find-ById $s.Window 'PreflightCancel')`. `Find-ById $s.Window 'PreflightSheet' 1` returns `$null`, and `$s.Project\.idp\runs` holds no run folder.
- **Start.** Open the preflight again and run `Invoke-Element (Find-ById $s.Window 'PreflightStart')`. `Wait-Until { (Find-ById $s.Window 'RunStatus').Current.Name -eq 'Running' } 30` returns `$true`. `RunProgress` reads `0 of 3 done`, the first card's `CardStatus` reads `Running`, and `Find-ById $s.Window 'RunWorkflow' 1` returns `$null`. Run `Save-Evidence $s 'run-started'`.
- **Cards.** Choose the second task. `RunTaskStatus` reads `Waits for "Design the workflow file format"`, and `Find-ById $s.Window 'RunTask' 1` returns `$null`.
- **Finish.** Run `New-Item -ItemType File $s.Gate`. `Wait-Until { (Find-ById $s.Window 'RunStatus').Current.Name -eq 'Completed' } 120` returns `$true`, and `RunProgress` reads `3 of 3 done`. Run `Save-Evidence $s 'run-completed'`. The journal's last line is `settled` with `completed`.
- **Stop.** Run `Remove-Item $s.Gate`, start another run, wait for `Running`, and run `Invoke-Element (Find-ById $s.Window 'StopWorkflow')`. `Wait-Until { (Find-ById $s.Window 'RunStatus').Current.Name -eq 'Stopped' } 30` returns `$true`. The first card reads `Cancelled` and the others `Not started`. Run `Save-Evidence $s 'run-stopped'`.

## Gotchas

- Preflight reads Git off the window's thread, so wait for `PreflightTasks` before reading it.
- A workflow run needs a Git repository whose HEAD names a commit. Without one, the preflight lists the gap and `Start run` stays off.
- The fake Codex answers every turn alike, so an Approval node and a Chat task's replies are covered by the headless tests `WorkflowRunNavigationTests`, not by this recipe.
- Windows has not run this recipe yet; the headless tests and a Linux window under Xvfb cover it so far.
