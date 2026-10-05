# Open a project and save edits

A user opens a project folder, edits a task in the inspector, and saves the workflow into the folder's `.idp/workflows/<workflow-id>.json`. The window title marks unsaved edits with `*`, and closing the window or opening another folder with unsaved edits asks whether to save them first.

## Sub-features

- `open-named` opens the folder named at start, and the title names the folder.
- `edit-task` edits a task's title, instructions, and acceptance criteria, and marks the project unsaved.
- `save-button` saves through the save button at the end of the breadcrumb.
- `first-save` creates `.idp/workflows/<workflow-id>.json` in an empty folder.
- `prompt-cancel` keeps the window and the edits when the user cancels the prompt.
- `prompt-discard` closes without writing when the user chooses `Don't save`.
- `prompt-save` writes the edits and closes when the user chooses `Save`.
- `reopen` shows the saved edits after a restart.
- `folder-asks` asks about unsaved edits before the folder button opens the system folder picker.

## How to get to it (user POV)

- Name a folder after the program at start. `Start-IDevelop` does this.
- Choose the folder button beside `PROJECT` in the sidebar.
- Choose a task in the sidebar or on the canvas, then type in the inspector's `Title`, `Instructions`, or `Acceptance criteria` box.
- Choose the save button at the end of the breadcrumb, or press Ctrl+S, or Cmd+S on macOS.
- Close the window with unsaved edits.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`.
- Run each bullet in one call, so its variables stay set. A call that reads the sample's workflow file first runs `$wf = "$($s.Project)\.idp\workflows\019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json"`.
- The file stores a task's `instructions` and `acceptanceCriteria` as arrays of lines.

- **Open.** Check the title and the task count. Run `Assert-Step $s ($s.Window.Current.Name -eq 'project - iDevelop') 'the title names the project'` and `Assert-Step $s ((Find-ById $s.Window 'TaskCount').Current.Name -eq '3') 'the sidebar counts three tasks'`.
- **Edit.** Choose the second task and retitle it. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Set-Text (Find-ById $s.Window 'TaskTitle') 'Implement atomic save 原子保存'`, and ``Set-Text (Find-ById $s.Window 'TaskAcceptanceCriteria') "The previous file survives a crash.`nNo reader sees a partial file."``. Then run `Assert-Step $s ((Wait-Until { $s.Window.Current.Name -eq 'project* - iDevelop' }) -eq $true) 'the title marks unsaved edits'`, `Assert-Step $s ((Find-ById $s.Window 'UnsavedChanges').Current.Name -eq 'Unsaved changes') 'the breadcrumb says Unsaved changes'`, and `Assert-Step $s ($null -ne (Find-NameOutside $s.Window 'Implement atomic save 原子保存' 'SidebarTasks')) 'the card shows the new title'`.
- **Save.** Choose the save button. Run `Save-Evidence $s 'before-save'` and `Invoke-Element (Find-ById $s.Window 'Save')`. Then run `Assert-Step $s ((Wait-Until { $s.Window.Current.Name -eq 'project - iDevelop' }) -eq $true) 'the title loses its unsaved mark'` and `Assert-Step $s ($null -eq (Find-ById $s.Window 'UnsavedChanges' 1)) 'the Unsaved changes note is gone'`.
- **Saved file.** Read the second view. Run `$task = (Get-Content -Raw -Encoding UTF8 $wf | ConvertFrom-Json).tasks[1]`, `Assert-Step $s ($task.title -eq 'Implement atomic save 原子保存') 'the file holds the new title'`, ``Assert-Step $s (($task.acceptanceCriteria -join "`n") -eq "The previous file survives a crash.`nNo reader sees a partial file.") 'the file holds both typed criteria'``, and `Save-Evidence $s 'saved'`.
- **Prompt, Cancel.** Edit again and close. Run `Set-Text (Find-ById $s.Window 'TaskTitle') 'Unsaved edit'` and `Close-Window $s.Window`. Then run `Assert-Step $s ((Find-InProcessWindows $s.Process 'Question').Current.Name -eq 'Save changes to project?') 'closing asks whether to save changes to project'`, `Invoke-Element (Find-InProcessWindows $s.Process 'CancelChanges')`, `Assert-Step $s (-not $s.Process.WaitForExit(1000)) 'Cancel keeps the window open'`, and `Assert-Step $s ($s.Window.Current.Name -eq 'project* - iDevelop') 'the edit stays unsaved after Cancel'`.
- **Prompt, Don't save.** Close again and discard. Run `Close-Window $s.Window`, `Invoke-Element (Find-InProcessWindows $s.Process 'DiscardChanges')`, `Assert-Step $s ($s.Process.WaitForExit(15000)) "Don't save closes the window"`, and `Assert-Step $s ((Get-Content -Raw -Encoding UTF8 $wf | ConvertFrom-Json).tasks[1].title -eq 'Implement atomic save 原子保存') 'the file keeps the saved title'`.
- **Reopen.** Restart in the same session. Begin the call with `Import-Module ./scripts/real-window.psm1` and `$s = Start-IDevelop -Reopen`, because `Connect-IDevelop` fails while the window is closed. Then run `Assert-Step $s ($null -ne (Find-NameOutside $s.Window 'Implement atomic save 原子保存' 'SidebarTasks')) 'the reopened card shows the saved title'` and `Assert-Step $s ((Get-SidebarTasks $s.Window).Current.Name -notcontains 'Unsaved edit') 'the discarded edit is gone'`.
- **Prompt, Save.** Choose the second task again, edit, close, and save. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Set-Text (Find-ById $s.Window 'TaskTitle') 'Saved from the prompt'`, `Close-Window $s.Window`, and `Invoke-Element (Find-InProcessWindows $s.Process 'SaveChanges')`. Then run `Assert-Step $s ($s.Process.WaitForExit(15000)) 'Save closes the window'` and `Assert-Step $s ((Get-Content -Raw -Encoding UTF8 $wf | ConvertFrom-Json).tasks[1].title -eq 'Saved from the prompt') 'the prompt saved the edit'`.
- **Folder button asks first.** Begin the call with `Import-Module ./scripts/real-window.psm1` and `$s = Start-IDevelop -Reopen`. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Set-Text (Find-ById $s.Window 'TaskTitle') 'Unsaved edit'`, and `Invoke-Element (Find-ById $s.Window 'OpenFolder')`. Then run `Assert-Step $s ((Find-InProcessWindows $s.Process 'Question').Current.Name -eq 'Save changes to project?') 'the folder button asks whether to save changes first'`, `Invoke-Element (Find-InProcessWindows $s.Process 'CancelChanges')`, and `Assert-Step $s ($s.Window.Current.Name -eq 'project* - iDevelop') 'the edit stays unsaved after Cancel'`.
- **First save.** Close the window with `Close-Window $s.Window`, `Invoke-Element (Find-InProcessWindows $s.Process 'DiscardChanges')`, and `$s.Process.WaitForExit(15000)`. Then run `$s = Start-IDevelop -Reopen -Empty`, `Assert-Step $s ($s.Window.Current.Name -eq 'empty-project - iDevelop') 'the title names the empty folder'`, `Assert-Step $s ((Find-ById $s.Window 'TaskCount').Current.Name -eq '0') 'the sidebar counts no task'`, and `Assert-Step $s (-not (Test-Path "$($s.Project)\.idp")) 'the empty folder has no .idp folder'`. Run `Invoke-Element (Find-ById $s.Window 'AddTask')`, `Invoke-Element (Find-ById $s.Window 'AddNodeItem')` to choose the popover's first row, Implement, and `Invoke-Element (Find-ById $s.Window 'Save')`. Then run `$files = @(Wait-Until { Get-ChildItem "$($s.Project)\.idp\workflows" -Filter *.json -ErrorAction SilentlyContinue })`, `Assert-Step $s ($files.Count -eq 1) 'the first save writes one workflow file'`, `$saved = Get-Content -Raw -Encoding UTF8 $files[0].FullName | ConvertFrom-Json`, and `Assert-Step $s (@($saved.tasks).Count -eq 1 -and $saved.tasks[0].title -eq 'New task') 'its only task is titled New task'`.

## Gotchas

- The sidebar repeats every task title. Find a card with `Find-NameOutside`, or the sidebar's copy passes for the card.
- After `Don't save` or `Save` in the prompt the process exits. `Connect-IDevelop` then fails until `Start-IDevelop -Reopen` reopens the window.
- A second `Close-Window` while the prompt is open opens no second prompt. Answer the open one.
- The folder button opens the operating system's folder picker when nothing is unsaved. This harness does not drive that dialog. Open a folder by naming it at start, and see `UnsavedChangesTests` for the picker paths.
- Ctrl+S and Cmd+S are keys, which UI Automation patterns cannot press. `CanvasTests` and `AgentPickerTests` save with them headlessly.
- Saving rewrites the whole workflow file. Compare parsed values, not bytes, after an edit.
