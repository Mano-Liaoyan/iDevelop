# Run and cancel a task

A user runs a task with its agent client from the inspector. While it runs, the card, the inspector, and a run bar at the bottom of the canvas say so. Cancel stops the client and every process it started. A run that ends on its own shows its result. A task whose client cannot start shows why and starts nothing. Every attempt is recorded in `.idp/attempts/<task-id>/<attempt-id>/events.jsonl`.

## Sub-features

- `run-start` starts the selected task's client and shows `Running` on the card, in the inspector, and in the run bar.
- `run-cancel` stops the client and the processes it started, and records the run as `Cancelled`.
- `run-succeed` shows `Succeeded`, the result, and the recent activity of a run that ends on its own.
- `run-refused` shows why a task whose client is not ready cannot start, and starts nothing.
- `run-leave` asks before closing the window during a run, and `Stop and leave` records the run as interrupted.

## How to get to it (user POV)

- Choose a task in the sidebar or on the canvas, then choose `Run` under `RUN` in the inspector.
- Choose `Cancel` in the run bar, or `Cancel` beside `Run` in the inspector.
- Close the window or open another folder while a task runs.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.
- `$s.Gate` does not exist. The fake Codex starts a sleeper process, writes its id to `$s.Run\fake-bin\sleeper.pid`, and waits until `$s.Gate` exists. Then it replays a successful turn that ends with `DONE`.

- **Refused start.** Choose the first task, whose client is Claude Code, and run it. Run `Select-Element (Get-SidebarTasks $s.Window)[0]` and `Invoke-Element (Find-ById $s.Window 'RunTask')`. `(Find-ById $s.Window 'StartProblem').Current.Name` and `(Find-ById $s.Window 'Status').Current.Name` both start with `Claude Code is not ready.`, and `Find-ById $s.Window 'RunBar' 1` returns `$null`.
- **Start.** Choose the second task and run it. Run `Select-Element (Get-SidebarTasks $s.Window)[1]` and `Invoke-Element (Find-ById $s.Window 'RunTask')`. `RunBar` appears. `RunBarTask` reads `Implement atomic save`, `RunBarAgent` reads `Codex · GPT-6-Sol · medium`, `LastRunStatus` reads `Running`, and the second card's `CardStatus` reads `Running`.
- **Sleeper.** Run `$sleeper = Wait-Until { $f = "$($s.Run)\fake-bin\sleeper.pid"; if (Test-Path $f) { [int](Get-Content $f) } } 30`. `Get-Process -Id $sleeper` finds the process. Run `Save-Evidence $s 'running'`.
- **Cancel.** In a new shell, read the id again with `$sleeper = [int](Get-Content "$($s.Run)\fake-bin\sleeper.pid")`. Run `Invoke-Element (Find-ById $s.Window 'RunBarCancel')`. `Wait-Until { (Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Cancelled' } 30` returns `$true`. `Wait-Until { -not (Get-Process -Id $sleeper -ErrorAction SilentlyContinue) } 30` returns `$true`. `Find-ById $s.Window 'RunBar' 1` returns `$null`, the card reads `Cancelled`, and the window title stays `project - iDevelop`. Run `Save-Evidence $s 'cancelled'`.
- **Cancelled record.** Read the attempt. Run `Get-ChildItem "$($s.Run)\cancelled\attempts" -Recurse -Filter events.jsonl | Get-Content | ForEach-Object { $_ | ConvertFrom-Json } | Select-Object type, command`. The types are `requested`, `launched`, `cancelRequested`, and `exited`. The `requested` line's `command` is `$s.Run\fake-bin\codex.CMD`, which proves the fake ran.
- **Succeed.** Open the gate and run again. Run `New-Item -ItemType File $s.Gate` and `Invoke-Element (Find-ById $s.Window 'RunTask')`. `Wait-Until { (Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Succeeded' } 30` returns `$true`. `Get-Value (Find-ById $s.Window 'LastRunResult')` is `DONE`, and `LastRunActivity` lists a `command:` line and `DONE`. Run `Save-Evidence $s 'succeeded'`. The new attempt's types are `requested`, `launched`, four `agent` lines, and `exited`.
- **Close the gate.** Run `Remove-Item $s.Gate`, so the next run waits again.
- **Leave during a run.** Run the task again with `Invoke-Element (Find-ById $s.Window 'RunTask')`, then `Close-Window $s.Window`. Run `Invoke-Element (Find-InProcessWindows $s.Process 'KeepRunning')`. The window stays open and `LastRunStatus` still reads `Running`. Close again and run `Invoke-Element (Find-InProcessWindows $s.Process 'StopAndLeave')`. `$s.Process.WaitForExit(15000)` returns `$true`. Run `Save-Evidence $s 'interrupted'`, which copies the files without a screenshot once the window is closed. The newest attempt's types end with `interruptRequested` and `exited`.

## Gotchas

- Run only Codex tasks in a fake session. The other clients are blocked on purpose, so their tasks reach only `run-refused`.
- The gate stays open until it is removed. While it exists, every run of the session succeeds at once, and a cancel cannot be caught.
- Each run rewrites `sleeper.pid`. Read it after the run starts and before the next one.
- A run that ends on its own leaves its sleeper running, as a real client leaves a dev server. `Stop-IDevelop` stops it. Cancel and `Stop and leave` stop it themselves.
- `RunBar` appears only while a task runs, so `Find-ById` returns `$null` for it afterward. Pass a short timeout, such as `Find-ById $s.Window 'RunBar' 1`.
- Two tasks running at once, a second window, and opening another folder during a run need two windows or the folder picker. `RunTests` covers them headlessly.
