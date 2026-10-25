# Run a whole workflow

A user runs the shown workflow from `Run Workflow` beside `Generate`, without selecting a node. The engine starts every node that has no current result and whose predecessors are completed, and the rest follow as their inputs complete. The bar at the bottom center counts the nodes that run and wait for the person, names the running ones, and offers `Stop All`, and `Merge into my branch` while every node is completed and nothing is stale (ADR 0017). An Approval node, and a reviewer at its round limit, wait for the person with `Approve` and `Send back`. Switching projects or workflows stops nothing, and the sidebar's dots show a hidden workflow's activity. Each node's log is `.idp/nodes/<node-id>.jsonl`.

## Sub-features

- `run-start` starts every ready node, and `WorkflowRunBar` shows `RunStatus` such as `1 running` and `RunActivity` with their titles.
- `run-cards` shows each node's state on its card: `Idle`, `Running`, `Input required · <why>`, `Completed`, `Failed`, or `Stale`.
- `run-refused` keeps a node's `RunTask` off while a predecessor is not completed, and `StartProblem` reads `It waits for <title>.`.
- `run-stop` stops every running node with `StopAll`. Finished results stay.
- `run-stale` flags a completed node `Stale` once its instruction, its model, or an input changes, and `Merge into my branch` hides until it runs again.
- `run-merge` offers `MergeIntoBranch` once every node is completed and current, and merges the end nodes' commits into the checked-out branch.
- `run-gate` offers `Approve` and, with a note in `SendBackNote`, `SendBack` on a waiting Approval node.
- `run-conversation-strip` repeats the bar's status, `Stop All`, and `Merge into my branch` in the conversation view while it covers the canvas.
- `run-retained` keeps nodes running while another project is shown, with `ProjectRunning`, `WorkflowRunning`, `ProjectWaiting`, and `WorkflowWaiting`.

## How to get to it (user POV)

- Choose `Run Workflow` at the canvas's top right.
- In the bar, choose `Stop All`, or `Merge into my branch` once it shows.
- Choose the bar's pill, the waiting pill at the top of the canvas, or a card's attention glyph, to reach a node that waits.
- Choose an Approval node, then `Approve`, or write a note and choose `Send back`, in the inspector or on its expanded card.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex. `Start-IDevelop` makes the sample's copy a Git repository with one commit.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.
- The sample's first node asks for Claude Code, which a fake session blocks. Choose it with `Select-Element (Get-SidebarTasks $s.Window)[0]` and `Select-PickerEntry $s 'TaskClient' 'Codex'`. The third node has no agent; choose it and pick `Codex` too.
- `$s.Gate` does not exist, so each turn waits until it does.

- **Start.** Run `Invoke-Element (Find-ById $s.Window 'RunWorkflow')`. `Wait-Until { (Find-ById $s.Window 'RunStatus').Current.Name -like '* running' } 30` returns `$true`. The first card's `CardStatus` reads `Running`. Choose the second node; `StartProblem` reads `It waits for Design the workflow file format.`. Run `Save-Evidence $s 'run-started'`.
- **Stop All.** Run `Invoke-Element (Find-ById $s.Window 'StopAll')`. `Wait-Until { -not (Find-ById $s.Window 'WorkflowRunBar' 1) } 30` returns `$true`, and the first card reads `Idle`. Run `Save-Evidence $s 'run-stopped'`.
- **Finish.** Run `New-Item -ItemType File $s.Gate` and `Invoke-Element (Find-ById $s.Window 'RunWorkflow')`. `Wait-Until { (Find-ById $s.Window 'RunStatus').Current.Name -eq 'Every node is complete' } 120` returns `$true`, every card reads `Completed`, and `MergeIntoBranch` shows. Run `Save-Evidence $s 'run-completed'`.
- **Stale.** Choose the first node and run `Set-Text (Find-ById $s.Window 'TaskInstructions') 'Draft it again.'`. Its card reads `Stale`, and `Find-ById $s.Window 'MergeIntoBranch' 1` returns `$null`. Run `Invoke-Element (Find-ById $s.Window 'RetryTask')`; once every card reads `Completed` again, `MergeIntoBranch` shows.
- **Merge.** Run `Invoke-Element (Find-ById $s.Window 'MergeIntoBranch')`. `Status` reads `Merged the workflow's work into your branch.`, and `git -C $s.Project log --oneline` shows the merge.

## Gotchas

- An editing node needs a Git repository whose HEAD names a commit. Without one, `StartProblem` reads `The project folder is in no Git work tree.`.
- The fake Codex answers every turn alike, so an Approval node, a review's round limit, and a Chat node's reply are covered by the headless tests `SendBackTests`, `ReviewNodeTests`, and `ConversationTests`.
- Windows has not run this recipe yet; the headless tests and a Linux window under Xvfb cover it so far.
