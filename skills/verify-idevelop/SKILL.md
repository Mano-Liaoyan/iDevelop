---
name: verify-idevelop
description: Launch the built iDevelop desktop window on Windows, drive it through UI Automation the way a user does, capture screenshots and the files the app writes, and clean up. Use to verify any change to the app in its real window after the build and headless tests pass. Task runs go through a fake Codex unless a real-client run is needed.
---

# Verify iDevelop

`scripts/real-window.psm1` drives the Release build of iDevelop in its real window. It uses UI Automation patterns only, so it never moves the mouse or types keys. The window still opens on the desktop.

On macOS and Linux no real-window harness exists. Run `dotnet test -c Release`, whose tests in `tests/IDevelop.Desktop.Tests` drive the main window headlessly, and stop there.

## Isolation

- Run one session at a time per Windows account. The theme preference `%APPDATA%\iDevelop\settings.json` is per user, and the user's own iDevelop shares it. A session backs it up at start and restores it at stop. `Start-IDevelop` and `scripts/check-real-window.ps1` refuse while another live run holds the backup.
- A session opens a copy of `samples/storage-change` inside its run folder, never a user project.
- Task runs go through a fake Codex. The session puts fake clients ahead of the user's PATH. Codex replays recorded output. Claude Code, Pi, and Antigravity CLI answer nothing, so iDevelop marks them `Not ready` and refuses to run them. No run spends quota.
- Never find or kill iDevelop or a fake client by process name. `Stop-IDevelop` ends only what its session started.

## Launch

Work from the repository root in PowerShell 7.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet build -c Release
Import-Module ./scripts/real-window.psm1
$s = Start-IDevelop
```

`Start-IDevelop` creates `.verify/<yyyyMMdd-HHmmss>/`, copies the sample into its `project` folder, launches `src/IDevelop.Desktop/bin/Release/net10.0/IDevelop.Desktop.exe` on it, and waits for the window. The window is ready when `$s.Window.Current.Name` is `project - iDevelop`. `$s` carries `Run`, `Project`, `Gate`, `Process`, and `Window`.

- `-Empty` opens an empty folder instead of the sample.
- `-Reopen` reopens the newest session after its window closed, and `-Run <run folder>` reopens a named one. Both keep the project, the preference backup, and the evidence, so they check what survives a restart.
- `-Project <folder>` opens another folder. Use only a scratch copy.

Each tool call is a new shell. Begin every later call with these two lines. `Connect-IDevelop` attaches to the newest session in `.verify`, or to the one `-Run` names.

```powershell
Import-Module ./scripts/real-window.psm1
$s = Connect-IDevelop
```

## Doctor

Run `Test-IDevelop` first, and again whenever anything looks off. It changes nothing.

It checks that the session's process is alive and runs this checkout's Release exe. It checks that each assembly in the build folder is newer than every file of its project under `src/`. It prints the window title, each Agents row, the session's fake processes, and who holds the theme backup. The last line is `Doctor: worth driving` or `Doctor: not worth driving`.

- A stale assembly needs `Stop-IDevelop`, `dotnet build -c Release`, and a new session.
- A `WARN` about a leftover backup means a run was killed. The next `Start-IDevelop` restores it first.

## Drive

Elements are found by automation id and driven through patterns. An element's text is `.Current.Name`.

| Command | Use |
| --- | --- |
| `Find-ById $s.Window <id> [seconds]` | Wait up to 10 seconds for a descendant with that automation id. |
| `Find-NameOutside $s.Window <title> 'SidebarTasks'` | Find a card's title text, not the sidebar's copy. |
| `Find-InProcessWindows $s.Process <id>` | Find a button in a dialog, which opens as its own window. |
| `Get-SidebarTasks $s.Window` | Expand the first workflow's row if it is collapsed, and list its task rows, in order. |
| `Select-PickerEntry $s <picker id> <label>` | Open a picker, choose the entry with that label, and close it. |
| `Wait-Until { Get-PickerEntries <picker> }` | Open a picker's list and return its entries. |
| `Invoke-Element`, `Select-Element`, `Set-Text`, `Get-Value`, `Test-Selected` | Invoke a button, select a row or segment, set or read a text box or picker. |
| `Close-Window $s.Window` | Close the window as its close button does. |
| `Wait-Until { <probe> } [seconds]` | Poll until the probe returns a value. It returns `$null` on timeout. |

These ids cover most drives. The feature map lists the rest.

- Sidebar: `AddTask` (the Add Node button, which opens the Add popover), `OpenFolder`, `Projects` (the tree of open projects), `RefreshAgents`, `AgentClaudeCode`, `AgentCodex`, `AgentPi`, `AgentAntigravity`, `ThemeSystem`, `ThemeLight`, `ThemeDark`. Each project row has `ProjectName`, `ProjectRunning`, `NewWorkflow`, and `CloseProject`. Each workflow row has `WorkflowExpand` (a toggle, which takes `TogglePattern`), `WorkflowRow` (named by the workflow, which shows it), `WorkflowName`, `WorkflowUnsaved`, `WorkflowRunning`, and `TaskCount`. F2 or the row's `WorkflowRename` menu item opens `WorkflowNameBox`. An expanded workflow row lists its tasks in `SidebarTasks`. A window started by `Start-IDevelop` has one project with one workflow, so each of these ids names one element. With several projects or workflows, `Find-AllById` returns every match in sidebar order.
- Add popover: `AddNodePopover`, `AddNodeSearch`, `AddNodeList`, `AddNodeItem` (one per type, named `Add <type>`, such as `Add Implement`), `AddNodeAction` (one per action, named by its label), `AddNodeConnect`, `AddNodeDerive`, `AddNodeEdit`, `AddNodeDescription`, `AddNodeProblems`.
- Canvas: `Editor`, `TaskCard`, `CardKind`, `CardTitle`, `CardTitleBox` (the title box that F2 and Rename open on the card, which hides `CardTitle` until it closes), `CardStatus`, `CardAgent`, `CardStateGlyph`, `CardUnderReview`, `GhostCard`, `GhostLabel`, `GhostTitle`, `GhostWire` (a proposed connection), `DanglingWire` (a wire dropped on empty canvas, shown while the Add popover is open), `NextWaiting`, `WaitingCount`, `ZoomIn`, `ZoomOut`, `FitToScreen`, `Minimap`, `RunBar`, `RunBarTask`, `RunBarAgent`, `RunBarElapsed`, `RunBarActivity`, `RunBarCancel`, `GenerateWorkflow`, `GenerateEmptyState`, `GenerateStart`.
- Breadcrumb: `Breadcrumb`, `BreadcrumbProject`, `BreadcrumbWorkflow`, `Undo`, `Redo`, `UnsavedChanges`, `Save`, `Status`.
- Generate sheet: `GenerateSheet`, `GeneratePrompt`, `GenerateClient`, `GenerateModel`, `GenerateReasoning`, `GenerateProblem`, `GenerateCancel`, `GenerateSubmit`.
- Inspector header: `InspectorHeader`, `TaskTitle`, `InspectorKind`, `TaskType`, `TaskTypeVersion`, `InspectorMore`, `InspectorFilter`, `InspectorTools`, and the `ExpandAllSections` and `CollapseAllSections` entries of the `InspectorTools` menu.
- Inspector sections: `Section<Key>` for `Task`, `Agent`, `Run`, `Proposal`, `Findings`, `Conversation`, `Activity`, `Blueprint`, `Connection`, `Overview`, and `Library`. The titles of the Agent section and the Talk to the Agent section are `AgentHeading` and `ComposerHeading`.
- Inspector rows: `Task<Field>` (such as `TaskInstructions` and `TaskAcceptanceCriteria`), `Revert<Field>`, `TaskClient`, `TaskModel`, `TaskReasoning`, `RevertAgent`, `PermissionNote`, `TaskConversation`, `RevertConversation`, `ConversationNote`, `RunTask`, `CancelRun`, `StartProblem`, `LastRunStatus`, `LastRunConfiguration`, `LastRunTiming`, `LastRunDetail`, `LastRunResult`, `LastRunActivity`, `ToolCallsToggle`. `PermissionNote` and `ConversationNote` are the info glyphs after the Client and Conversation pickers. Each is named by its note's short line, and its help text holds the whole note. `ToolCallsToggle` shows the tool calls that `LastRunActivity` leaves out, each task keeps it open or closed across its runs, and a toggle takes `TogglePattern`, not `InvokePattern`. While a proposal is open, the Run section shows only `LastRunStatus`, `LastRunDetail` after a turn that failed or was interrupted, or that stopped because the client reported no session, and `CancelRun` during a turn.
- Proposal and review: `Proposal`, `ProposalItem`, `ProposalConnectionsToggle` (which shows `ProposalConnections`), `ProposalConnections`, `ProposalProblem`, `ProposalUsePlannerAgent`, `ProposalAcceptFinish`, `AcceptProposal`, `DismissProposal`, `Review`, `ReviewSummary`, `Findings`.
- Connection and workflow inspectors: `ConnectionFrom`, `ConnectionTo`, `KindDependency`, `KindContext`, `DeleteConnection`, `WorkflowKinds`, `InspectorHint`, `Palette`, `PlaceBlueprint`, `BlueprintMore`, `ReloadBlueprints`, `BlueprintProblems`. `DeriveBlueprint` and `EditBlueprint` are entries of a blueprint's `BlueprintMore` menu, which opens as its own window, so `Find-InProcessWindows` finds them.
- Conversation: `Conversation`, `TurnReply<n>`, `WaitingMessages`, `TerminalNote`, `Composer`, `SendMessage`, `StopAndSend`, `OpenInTerminal`, `SendProblem`, `PendingQuestion`, `MarkDone`.
- Dialogs: `Question` (the dialog's text), `SaveChanges`, `DiscardChanges`, `CancelChanges`, `StopAndLeave`, `KeepRunning`.

UI Automation cannot drag, right-click, press keys, or select a card or connection on the canvas. Choose a task through its sidebar row. The node menu (`NodeMenu<Action>`) and the connection menu (`ConnectionMenu<Action>`) open only on a right-click, so the headless tests cover them with the other pointer and keyboard input.

## Evidence

- `Assert-Step $s <bool> '<what>'` appends `PASS <what>` or `FAIL <what>` to the run's `transcript.txt`, prints it, and throws on `FAIL`. Pass a boolean, such as `($null -ne $x)`.
- `Save-Evidence $s <name>` saves `<name>.png` of the window while it is open. It copies the project's `.idp/workflows/*.json` and every `.idp/attempts/**/events.jsonl` into `<name>/`.
- Drive the user's path, not internal setters. Save evidence before and after the action, so it shows the action and the resulting state.
- Check side effects next to the screen. Read the saved workflow JSON after a save. Read `events.jsonl` after a run. Its `requested` line names the command that ran, which proves the fake client ran.
- Evidence stays in `.verify/<run>/`, which Git ignores. Report that path and what it holds.

## Cleanup

```powershell
Import-Module ./scripts/real-window.psm1
Stop-IDevelop
```

`Stop-IDevelop` closes the window. It answers `Stop and leave` for a running task and `Don't save` for unsaved edits. It kills only the session's process id if the window has not closed within 15 seconds. It stops every fake client process that runs from the session's folder, such as the sleeper a finished run leaves. It restores the theme preference, says whether it did, and warns about a backup another run left. It marks the session stopped and never deletes `.verify/<run>/`. Run it after every attempt, failed ones too, then list the run folder to confirm the evidence is still there.

## Features

[`features/README.md`](features/README.md) indexes the verification map. Drive every entry point that a change touches.

## Real-client run (spends quota)

Use a real client only when the change touches a client integration, such as `src/IDevelop.Core/Execution/Clients/`, or when the user asks. A real run spends the client's subscription quota, and the agent edits the project folder. Keep it in the session's scratch copy of the sample. Stop any fake session first.

```powershell
Import-Module ./scripts/real-window.psm1
$s = Start-IDevelop -RealClients
Test-IDevelop
```

1. Pick a client whose Agents row reads `Ready`.
2. Select the third task with `Select-Element (Get-SidebarTasks $s.Window)[2]`.
3. Set a small instruction with `Set-Text (Find-ById $s.Window 'TaskInstructions') 'Create hello.txt containing hi. Change nothing else.'`.
4. Choose the client, its cheapest model, and its lowest reasoning level with `Select-PickerEntry` on `TaskClient`, `TaskModel`, and `TaskReasoning`.
5. Run it with `Invoke-Element (Find-ById $s.Window 'RunTask')`, and wait up to a few minutes for `LastRunStatus` to leave `Running`. Choose `RunBarCancel` if it runs long.
6. Run `Save-Evidence $s 'real-run'`. Check that `events.jsonl` names the real client's command and that `hello.txt` exists in `$s.Project`.
7. Run `Stop-IDevelop`.
