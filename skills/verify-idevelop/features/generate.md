# Generate a workflow from a description

A user describes the work in the Generate sheet and chooses a planner agent. iDevelop places a Plan task in Chat mode with the description as its goal, selects it, and runs it. The planner's proposal shows as dashed cards, and nothing joins the workflow until the user accepts it in the planner's inspector.

## Sub-features

- `generate-empty` shows the `Start with a description` card on an empty workflow.
- `generate-sheet` opens the modal sheet, which offers only ready clients that have a read-only mode and keeps `Generate` off while the description is blank.
- `generate-submit` places a Plan task titled by the description's first line, with the whole description as its goal and Chat as its conversation, and runs it.
- `generate-progress` labels the toolbar button `Planning…` while the planner runs and `Review Proposal` while its proposal is open.
- `generate-accept` adds the proposed tasks through `Accept and Finish` or `Accept`, with the planner's agent while `New tasks use the planner's agent` is ticked.

## How to get to it (user POV)

- Choose `Generate` at the canvas's top right.
- On an empty workflow, choose `Generate Workflow…` in the card at the canvas's center.
- Right-click empty canvas and choose `Generate Workflow…` under `Actions` in the Add popover.
- Press Ctrl+Enter in the sheet to submit, or Escape to cancel.

## Driving it with real-window.psm1

Preconditions:

- A new session on an empty folder, started with `$s = Start-IDevelop -Empty`.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.
- `$s.Gate` does not exist. The fake Codex waits for it, then replays a turn that ends with `DONE` and holds no proposal.

- **Empty state.** Run `Assert-Step $s ($null -ne (Find-ById $s.Window 'GenerateEmptyState')) 'an empty workflow offers Generate'` and `Assert-Step $s ((Find-ById $s.Window 'TaskCount').Current.Name -eq '0') 'the workflow is empty'`.
- **Sheet.** Run `Invoke-Element (Find-ById $s.Window 'GenerateStart')`. Then run `Assert-Step $s ($null -ne (Find-ById $s.Window 'GenerateSheet')) 'the sheet opens'`, `Assert-Step $s ((Get-Value (Find-ById $s.Window 'GenerateClient')) -eq 'Codex') 'the planner defaults to the ready Codex'`, and `Assert-Step $s (-not (Find-ById $s.Window 'GenerateSubmit').Current.IsEnabled) 'Generate is off while the description is blank'`.
- **Submit.** Run ``Set-Text (Find-ById $s.Window 'GeneratePrompt') "Add CSV export to the reports page.`nCover it with tests."`` and `Invoke-Element (Find-ById $s.Window 'GenerateSubmit')`. Then run `Assert-Step $s ($null -eq (Find-ById $s.Window 'GenerateSheet' 1)) 'the sheet closes'`, `Assert-Step $s ((Find-ById $s.Window 'TaskCount').Current.Name -eq '1') 'one task is placed'`, `Assert-Step $s ((Get-Value (Find-ById $s.Window 'TaskTitle')) -eq 'Add CSV export to the reports page.') 'the planner is titled by the first line'`, `Assert-Step $s ((Find-ById $s.Window 'TaskType').Current.Name -eq 'Plan') 'it is a Plan'`, `Assert-Step $s ((Get-Value (Find-ById $s.Window 'TaskConversation')) -eq 'Chat') 'it converses in Chat mode'`, and `Assert-Step $s ((Find-ById $s.Window 'GenerateWorkflow').Current.Name -eq 'Planning…') 'the toolbar says Planning'`. Run `Save-Evidence $s 'planning'`.
- **Wait.** Run `New-Item -ItemType File $s.Gate`. Then run `Assert-Step $s ((Wait-Until { (Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Waiting for you' } 30) -eq $true) 'the Chat planner waits after its turn'` and `Assert-Step $s ((Find-ById $s.Window 'GenerateWorkflow').Current.Name -eq 'Generate') 'the toolbar reads Generate again'`.
- **Saved planner.** Run `Invoke-Element (Find-ById $s.Window 'Save')`, `$file = Wait-Until { Get-ChildItem "$($s.Project)\.idp\workflows" -Filter *.json -ErrorAction SilentlyContinue }`, and `$task = (Get-Content -Raw -Encoding UTF8 $file.FullName | ConvertFrom-Json).tasks[0]`. Then run `Assert-Step $s ($task.blueprint -eq 'idevelop.plan@1' -and $task.conversation -eq 'chat') 'the file holds a Plan in chat mode'` and ``Assert-Step $s (($task.fields.goal -join "`n") -eq "Add CSV export to the reports page.`nCover it with tests.") 'its goal is the whole description'``. Run `Save-Evidence $s 'generated'`.

## Gotchas

- The sheet covers the whole window, so `Save` and the sidebar take no input while it is open. Close it with `GenerateCancel` before driving anything else.
- The fake Codex's turn holds no proposal, so no dashed cards appear and `Accept and Finish` stays hidden. `GenerateTests` drives a scripted proposal headlessly, from the ghost cards through `Accept and Finish` and the planner's agent on the new tasks.
- The Add popover's `Generate Workflow…` entry needs a right-click, which UI Automation patterns cannot do. `GenerateTests` covers it headlessly.
- These steps were written from the source and checked by pointer in the Linux window on 2026-10-05. They have not run through `real-window.psm1` on Windows yet.
