# Keep several projects and workflows open

A user keeps several projects open in one window, and each project lists its workflows in the sidebar. A workflow row starts collapsed and shows its tasks only when expanded. Choosing a row shows that workflow's canvas. Each workflow keeps its unsaved edits, its undo history, and its running tasks while the user looks at another one. The window opens the same projects again at its next start.

## Sub-features

- `rows-collapsed` lists each project's workflows, with every workflow row collapsed.
- `new-workflow` adds an unsaved workflow named `Workflow 2` and shows it.
- `switch-keeps-edits` keeps an unsaved edit and its undo step across a switch to another workflow and back.
- `run-stays-owned` keeps a running task running and marked on its own workflow's row while another workflow is shown, and the other workflow shows no run bar.
- `close-asks-run` asks before Close Project stops a running task, and `Keep running` leaves the project open with the task running.
- `reopen-projects` opens the previous projects again at the next start, beside a folder named at start.
- `same-titles` shows two projects whose workflows and tasks share titles, each with its own rows.
- `close-asks-unsaved` asks before Close Project discards unsaved edits, and `Cancel` keeps the project open.

## How to get to it (user POV)

- Name a folder after the program at start. A project that was open at the last exit opens too.
- Choose the folder button beside `PROJECT` to add a project.
- Choose `New workflow` or `Close project` on a project row.
- Choose a workflow row to show it, or its chevron to list its tasks.
- Press F2 on a workflow row, or choose `Rename` in its context menu, to rename it.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`. The fake Codex is ready, as in [run and cancel](./run-and-cancel.md).
- Run each bullet in one call, so its variables stay set. A call that reads rows first runs `$rows = { Find-AllById $s.Window 'WorkflowRow' }`.
- The sample's workflow has no name, so its row reads `Workflow`.

- **Rows collapsed.** Run `Assert-Step $s (@(Find-AllById $s.Window 'WorkflowRow').Current.Name -join ',' -eq 'Workflow') 'the project lists one workflow'` and `Assert-Step $s ($null -eq (Find-ById $s.Window 'SidebarTasks' 1)) 'the workflow row starts collapsed'`. Run `Save-Evidence $s 'collapsed'`.
- **New workflow.** Run `Invoke-Element (Find-ById $s.Window 'NewWorkflow')`. Then run `Assert-Step $s ((Wait-Until { (Find-ById $s.Window 'BreadcrumbWorkflow').Current.Name -eq 'Workflow 2' }) -eq $true) 'the new workflow is shown'`, `Assert-Step $s ((Find-AllById $s.Window 'WorkflowRow').Current.Name -join ',' -eq 'Workflow,Workflow 2') 'the project lists both workflows'`, and `Assert-Step $s ($null -ne (Find-ById $s.Window 'WorkflowUnsaved' 1)) 'the new workflow is unsaved'`. Run `Invoke-Element (Find-ById $s.Window 'Save')` and `Assert-Step $s ((Wait-Until { @(Get-ChildItem "$($s.Project)\.idp\workflows" -Filter *.json).Count -eq 2 }) -eq $true) 'saving writes a second workflow file'`.
- **Switch keeps edits.** Show the first workflow and edit its second task. Run `Invoke-Element (Find-AllById $s.Window 'WorkflowRow')[0]`, `Select-Element (Get-SidebarTasks $s.Window)[1]`, and `Set-Text (Find-ById $s.Window 'TaskTitle') 'Edited before the switch'`. Show the other workflow with `Invoke-Element (Find-AllById $s.Window 'WorkflowRow')[1]`, then run `Assert-Step $s ($null -eq (Find-ById $s.Window 'UnsavedChanges' 1)) 'the saved workflow shows no unsaved note'`. Come back with `Invoke-Element (Find-AllById $s.Window 'WorkflowRow')[0]` and `Select-Element (Get-SidebarTasks $s.Window)[1]`. Then run `Assert-Step $s ((Get-Value (Find-ById $s.Window 'TaskTitle')) -eq 'Edited before the switch') 'the edit survives the switch'` and `Save-Evidence $s 'switched-back'`. Run `Invoke-Element (Find-ById $s.Window 'Undo')` and `Assert-Step $s ((Wait-Until { (Get-Value (Find-ById $s.Window 'TaskTitle')) -eq 'Implement atomic save' }) -eq $true) 'undo still takes the edit back'`.
- **Run stays owned.** Run the second task with `Invoke-Element (Find-ById $s.Window 'RunTask')` and `Assert-Step $s ($null -ne (Find-ById $s.Window 'RunBar')) 'the run bar appears'`. Show the other workflow with `Invoke-Element (Find-AllById $s.Window 'WorkflowRow')[1]`. Then run `Assert-Step $s ($null -eq (Find-ById $s.Window 'RunBar' 1)) 'the other workflow shows no run bar'`, `Assert-Step $s (@(Find-AllById $s.Window 'WorkflowRunning').Count -eq 1) 'one workflow row is marked running'`, `Assert-Step $s ($null -ne (Find-ById $s.Window 'ProjectRunning' 1)) 'the project row is marked running'`, and `Save-Evidence $s 'run-elsewhere'`. Come back with `Invoke-Element (Find-AllById $s.Window 'WorkflowRow')[0]` and run `Assert-Step $s ((Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Running') 'the task still runs in its own workflow'`.
- **Close asks about the run.** Run `Invoke-Element (Find-ById $s.Window 'CloseProject')` and `Invoke-Element (Find-InProcessWindows $s.Process 'KeepRunning')`. Then run `Assert-Step $s ($null -ne (Find-ById $s.Window 'ProjectName' 1)) 'Keep running leaves the project open'` and `Assert-Step $s ((Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Running') 'the task keeps running'`. Finish it with `New-Item -ItemType File $s.Gate`, `Assert-Step $s ((Wait-Until { (Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Succeeded' } 30) -eq $true) 'the run finishes'`, and `Remove-Item $s.Gate`.
- **Reopen with a second project.** Copy the sample with `Copy-Item -Recurse "$($s.Project)" "$($s.Run)\second"`, then close with `Close-Window $s.Window` and `$s.Process.WaitForExit(15000)`. Begin the next call with `Import-Module ./scripts/real-window.psm1` and `$s = Start-IDevelop -Reopen -KeepProjects -Project "$($s.Run)\second"`. Then run `Assert-Step $s ((Find-AllById $s.Window 'ProjectName').Current.Name -join ',' -eq 'project,second') 'both projects are open'`, `Assert-Step $s ((Find-AllById $s.Window 'WorkflowRow').Current.Name -join ',' -eq 'Workflow,Workflow 2,Workflow,Workflow 2') 'each project lists its own workflows with the same names'`, and `Assert-Step $s ((Find-ById $s.Window 'BreadcrumbProject').Current.Name -eq 'second') 'the folder named at start is shown'`. Run `Save-Evidence $s 'reopened'`.
- **Close asks about unsaved edits.** Show the second project's first workflow and edit it. Run `Invoke-Element (Find-AllById $s.Window 'WorkflowRow')[2]`, `$toggle = (Find-AllById $s.Window 'WorkflowExpand')[2].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)`, `if ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $toggle.Toggle() }`, `Select-Element (Find-AllById $s.Window 'SidebarTasks')[-1].FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)[0]`, and `Set-Text (Find-ById $s.Window 'TaskTitle') 'Unsaved in second'`. Run `Invoke-Element (Find-AllById $s.Window 'CloseProject')[1]`. Then run `Assert-Step $s ((Find-InProcessWindows $s.Process 'Question').Current.Name -eq 'Save changes to second?') 'closing asks about the unsaved edit'`, `Invoke-Element (Find-InProcessWindows $s.Process 'CancelChanges')`, and `Assert-Step $s (@(Find-AllById $s.Window 'ProjectName').Count -eq 2) 'Cancel keeps the project open'`. Close it again and discard with `Invoke-Element (Find-AllById $s.Window 'CloseProject')[1]` and `Invoke-Element (Find-InProcessWindows $s.Process 'DiscardChanges')`. Then run `Assert-Step $s ((Wait-Until { (Find-AllById $s.Window 'ProjectName').Current.Name -join ',' -eq 'project' }) -eq $true) 'only the first project stays open'`.

## Gotchas

- Every project and workflow row repeats its ids. `Find-ById` returns the first one, so pick a row from `Find-AllById` by its sidebar index.
- `Start-IDevelop` forgets the remembered projects unless it gets `-KeepProjects`. The folder picker behind `OpenFolder` is the operating system's, which this harness does not drive, so a second project comes from a reopen.
- A row that is expanded at exit opens expanded at the next start. Count `SidebarTasks` after a reopen rather than assuming every row is collapsed.
- The run question's buttons keep the window's labels, `Stop and leave` and `Keep running`, for Close Project too.
- `WorkflowExpand` is a toggle. It takes `TogglePattern`, not `InvokePattern`, so `Invoke-Element` cannot open it.
- Renaming takes F2 or a context menu, which UI Automation patterns cannot open. `WorkspaceTests` covers rename and its undo headlessly.
