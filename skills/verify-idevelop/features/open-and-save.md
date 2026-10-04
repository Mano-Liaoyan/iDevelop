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
- The sample's workflow file is `$s.Project\.idp\workflows\019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json`.

- **Open.** Check the title and the task count. Run `Assert-Step $s ($s.Window.Current.Name -eq 'project - iDevelop') 'the title names the project'` and `Assert-Step $s ((Find-ById $s.Window 'TaskCount').Current.Name -eq '3') 'the sidebar counts three tasks'`. Both pass.
- **Edit.** Choose the second task and retitle it. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Set-Text (Find-ById $s.Window 'TaskTitle') 'Implement atomic save 原子保存'`, and ``Set-Text (Find-ById $s.Window 'TaskAcceptanceCriteria') "The previous file survives a crash.`nNo reader sees a partial file."``. The title becomes `project* - iDevelop`, `(Find-ById $s.Window 'UnsavedChanges').Current.Name` is `Unsaved changes`, and `Find-NameOutside $s.Window 'Implement atomic save 原子保存' 'SidebarTasks'` finds the card.
- **Save.** Choose the save button. Run `Save-Evidence $s 'before-save'`, `Invoke-Element (Find-ById $s.Window 'Save')`, and `Wait-Until { $s.Window.Current.Name -eq 'project - iDevelop' }`. The title loses its `*` and `Find-ById $s.Window 'UnsavedChanges' 1` returns `$null`.
- **Saved file.** Read the second view. Run `$task = (Get-Content -Raw -Encoding UTF8 "$($s.Project)\.idp\workflows\019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json" | ConvertFrom-Json).tasks[1]`. `$task.title` is `Implement atomic save 原子保存`, and `$task.acceptanceCriteria` holds the two typed lines. Run `Save-Evidence $s 'saved'`.
- **Prompt, Cancel.** Edit again and close. Run `Set-Text (Find-ById $s.Window 'TaskTitle') 'Unsaved edit'`, `Close-Window $s.Window`, and `Invoke-Element (Find-InProcessWindows $s.Process 'CancelChanges')`. A dialog named `Save changes to project?` appeared. Afterward `$s.Process.HasExited` is `$false` and the title is still `project* - iDevelop`.
- **Prompt, Don't save.** Close again and discard. Run `Close-Window $s.Window`, `Invoke-Element (Find-InProcessWindows $s.Process 'DiscardChanges')`, and `$s.Process.WaitForExit(15000)`. It returns `$true`, and the saved file still has the title `Implement atomic save 原子保存`.
- **Reopen.** Restart in the same session. Run `$s = Start-IDevelop -Reopen`. `Find-NameOutside $s.Window 'Implement atomic save 原子保存' 'SidebarTasks'` finds the card, and `(Get-SidebarTasks $s.Window).Current.Name` does not contain `Unsaved edit`.
- **Prompt, Save.** Choose the second task again, edit, close, and save. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Set-Text (Find-ById $s.Window 'TaskTitle') 'Saved from the prompt'`, `Close-Window $s.Window`, `Invoke-Element (Find-InProcessWindows $s.Process 'SaveChanges')`, and `$s.Process.WaitForExit(15000)`. It returns `$true`, and the file's second task is titled `Saved from the prompt`.
- **Folder button asks first.** Reopen with `$s = Start-IDevelop -Reopen`, then run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Set-Text (Find-ById $s.Window 'TaskTitle') 'Unsaved edit'`, `Invoke-Element (Find-ById $s.Window 'OpenFolder')`, and `Invoke-Element (Find-InProcessWindows $s.Process 'CancelChanges')`. The same prompt appears, and after Cancel the title is still `project* - iDevelop`.
- **First save.** Close the window with `Close-Window $s.Window`, `Invoke-Element (Find-InProcessWindows $s.Process 'DiscardChanges')`, and `$s.Process.WaitForExit(15000)`. Then run `$s = Start-IDevelop -Reopen -Empty`. The title is `empty-project - iDevelop`, `TaskCount` reads `0`, and `$s.Project\.idp` does not exist. Run `Invoke-Element (Find-ById $s.Window 'AddTask')` and `Invoke-Element (Find-ById $s.Window 'Save')`. One file appears under `$s.Project\.idp\workflows`, and its only task is titled `New task`.

## Gotchas

- The sidebar repeats every task title. Find a card with `Find-NameOutside`, or the sidebar's copy passes for the card.
- After `Don't save` or `Save` in the prompt the process exits. `Connect-IDevelop` then fails until `Start-IDevelop -Reopen` reopens the window.
- A second `Close-Window` while the prompt is open opens no second prompt. Answer the open one.
- The folder button opens the operating system's folder picker when nothing is unsaved. This harness does not drive that dialog. Open a folder by naming it at start, and see `UnsavedChangesTests` for the picker paths.
- Ctrl+S and Cmd+S are keys, which UI Automation patterns cannot press. `CanvasTests` and `AgentPickerTests` save with them headlessly.
- Saving rewrites the whole workflow file. Compare parsed values, not bytes, after an edit.
