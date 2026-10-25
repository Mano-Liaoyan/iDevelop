# Run and stop a node

A user runs a node from the inspector, its expanded card, or its menu. The engine starts it once its predecessors are completed, and each node after it runs by itself once all of its own predecessors are completed. While a node runs, its card, its inspector, and the bar at the bottom of the canvas say so. Stop ends the turn and every process the client started. A turn that ends on its own leaves a result, which the inspector lists under `Results`. A node that the engine cannot start says why and starts nothing. Each node's log is `.idp/nodes/<node-id>.jsonl`.

## Sub-features

- `run-start` starts the selected node and shows `Running` on the card, in `RunTaskStatus`, and in the bar's `RunStatus` as `1 running`.
- `run-stop` stops the client and the processes it started. A node without a result goes back to `Idle`.
- `run-complete` shows `Completed`, and the result's first paragraph under `Results`.
- `run-refused` keeps `RunTask` off for a node whose client is not ready, or whose predecessors are not completed, and says why in `StartProblem`.
- `run-retry` offers `RetryTask` once a turn has ended, which starts a fresh session.
- `run-leave` asks before closing the window while a node runs. `Stop and leave` stops it, and it reopens as `Input required · interrupted`.

## How to get to it (user POV)

- Choose a node in the sidebar or on the canvas, then choose `Run` in the `Run` section of the inspector.
- Choose `Stop` in the inspector, or `Stop All` in the bar.
- Close the window or the project while a node runs.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex. `Start-IDevelop` makes the sample's copy a Git repository, because a node that edits the project works in its own worktree.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.
- `$s.Gate` does not exist. The fake Codex starts a sleeper process, writes its id to `$s.Run\fake-bin\sleeper.pid`, and waits until `$s.Gate` exists. Then it replays a successful turn that ends with `DONE`.

- **Refused start.** Choose the first node, whose client is Claude Code. Run `Select-Element (Get-SidebarTasks $s.Window)[0]`. `(Find-ById $s.Window 'RunTask').Current.IsEnabled` is `$false`, and `(Find-ById $s.Window 'StartProblem').Current.Name` starts with `Claude Code is not ready.`.
- **Start.** Choose the second node and run it. Run `Select-Element (Get-SidebarTasks $s.Window)[1]` and `Invoke-Element (Find-ById $s.Window 'RunTask')`. `WorkflowRunBar` appears, `RunStatus` reads `1 running`, `RunActivity` reads `"Implement atomic save"`, `RunTaskStatus` reads `Running`, and the second card's `CardStatus` reads `Running`. Run `Save-Evidence $s 'running'`.
- **Stop.** Read the sleeper with `$sleeper = [int](Get-Content "$($s.Run)\fake-bin\sleeper.pid")`. Run `Invoke-Element (Find-ById $s.Window 'StopTask')`. `Wait-Until { (Find-ById $s.Window 'RunTaskStatus').Current.Name -eq 'Idle' } 30` returns `$true`, and `Wait-Until { -not (Get-Process -Id $sleeper -ErrorAction SilentlyContinue) } 30` returns `$true`. Run `Save-Evidence $s 'stopped'`. The node's log ends with a `stopped` entry.
- **Complete.** Open the gate and run again. Run `New-Item -ItemType File $s.Gate` and `Invoke-Element (Find-ById $s.Window 'RunTask')`. `Wait-Until { (Find-ById $s.Window 'RunTaskStatus').Current.Name -eq 'Completed' } 30` returns `$true`, and `Results` lists `Result 1 · Current`. Run `Save-Evidence $s 'completed'`. The log has `turnStarted`, `agent` lines, and `completed`, whose result names a commit.
- **Retry.** Run `Invoke-Element (Find-ById $s.Window 'RetryTask')`. `Results` then lists `Result 2 · Current` above `Result 1`.
- **Leave during a run.** Remove the gate with `Remove-Item $s.Gate`, retry once more, then `Close-Window $s.Window`. Run `Invoke-Element (Find-InProcessWindows $s.Process 'KeepRunning')`. The window stays open and `RunTaskStatus` still reads `Running`. Close again and run `Invoke-Element (Find-InProcessWindows $s.Process 'StopAndLeave')`. `$s.Process.WaitForExit(15000)` returns `$true`. Reopen with `$s = Start-IDevelop -Reopen`; the card reads `Input required · interrupted`, and the inspector offers `ContinueTask` and `RetryTask`.

## Gotchas

- The gate stays open until it is removed. While it exists, every turn ends at once, and a stop cannot be caught.
- A turn that ends on its own leaves its sleeper running, as a real client leaves a dev server. `Stop-IDevelop` stops it. Stop and `Stop and leave` stop it themselves.
- `WorkflowRunBar` shows only while a node runs or waits, or while Merge into my branch is offered, so pass a short timeout to `Find-ById`.
