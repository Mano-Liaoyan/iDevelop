# The AGENTS section and the agent pickers

The sidebar's AGENTS section shows whether Claude Code, Codex, Pi, and Antigravity CLI are ready on this machine, and why a client is not. The inspector's Agent pickers show the selected task's client, model, and reasoning level, offer only what each client offers here, and save the user's choice into the task.

## Sub-features

- `agents-rows` lists the four clients with a readiness summary and, for a client that is not ready, the reason.
- `agents-refresh` checks the clients again from the refresh button beside `AGENTS`.
- `picker-shows` shows the selected task's saved client, model, and level, and marks a model this machine does not offer.
- `picker-follow` follows the task chosen in the sidebar and edits nothing.
- `picker-client` offers `None` and the four clients, marks the ones that are not ready, and takes the chosen client's first model at its default level.
- `picker-model` and `picker-level` take a model and a level, and the level list follows the model.
- `permission-note` states what the chosen client may do without asking.
- `card-agent` shows the chosen agent on the task's card.
- `agent-save` makes the choice an unsaved edit that saves into the task's `execution`.

## How to get to it (user POV)

- Read the AGENTS section at the bottom of the sidebar.
- Choose the refresh button beside `AGENTS`.
- Choose a task in the sidebar or on the canvas, then use the `Client`, `Model`, and `Reasoning` pickers in the `Agent` section of the inspector.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so Codex is the fake and the other clients are blocked.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.

- **Rows.** Read each row. Run `foreach ($id in 'AgentClaudeCode', 'AgentCodex', 'AgentPi', 'AgentAntigravity') { $row = Find-ById $s.Window $id; "$id $($row.Current.Name) $($row.Current.HelpText)" }`. Codex reads `Ready · 3 models`. The other three read `Not ready`, and each reason names the probe that failed, such as `claude auth status exited with code 99`.
- **Refresh.** Run `Invoke-Element (Find-ById $s.Window 'RefreshAgents')` and `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60`. Codex reads `Ready · 3 models` again.
- **Saved agent.** Choose the second task. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`. `Get-Value` on `TaskClient`, `TaskModel`, and `TaskReasoning` gives `Codex`, `GPT-6-Sol`, and `medium`. `(Find-ById $s.Window 'PermissionNote').Current.Name` is `Codex may edit files in the project folder. Its commands run in its workspace sandbox.`. The window title stays `project - iDevelop`.
- **Model not offered.** Choose the first task. Run `Select-Element (Get-SidebarTasks $s.Window)[0]`. The pickers give `Claude Code · not ready`, `claude-opus-5-5 (not offered on this machine)`, and `high`. The window title stays `project - iDevelop`.
- **No agent.** Choose the third task. Run `Select-Element (Get-SidebarTasks $s.Window)[2]`. `TaskClient` gives `None`, and `Find-ById $s.Window 'TaskModel' 1` returns `$null`.
- **Choose a client.** Run `Select-PickerEntry $s 'TaskClient' 'Codex'`. `TaskModel` gives `GPT-6.1-Sol`, `TaskReasoning` gives `low`, and the window title becomes `project* - iDevelop`.
- **Choose a model and a level.** Run `Select-PickerEntry $s 'TaskModel' 'GPT-5.5'` and `Select-PickerEntry $s 'TaskReasoning' 'high'`. The third card's `CardAgent` text reads `Codex · GPT-5.5 · high`. `Select-PickerEntry $s 'TaskReasoning' 'ultra'` throws, because GPT-5.5 offers only `low`, `medium`, `high`, and `xhigh`.
- **List the entries.** Open a picker and read its list. Run `$picker = Find-ById $s.Window 'TaskClient'`, `(Wait-Until { Get-PickerEntries $picker }).Current.Name`, and `$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()`. It lists `None`, `Claude Code · not ready`, `Codex`, `Pi · not ready`, and `Antigravity CLI · not ready`.
- **Save.** Run `Invoke-Element (Find-ById $s.Window 'Save')` and `Save-Evidence $s 'agent-saved'`. In the copied workflow file the third task's `execution` is `{"client":"codex","model":"gpt-5.5","reasoning":"high"}`.

## Gotchas

- A picker only takes a choice made in its open list. `Select-PickerEntry` opens the list first. Setting a picker's value any other way changes no task.
- A picker's list closes as soon as the window loses the foreground, such as when the user clicks another application. `Get-PickerEntries` and `Select-PickerEntry` open it again on each try.
- An entry's name is its label, followed by ` · ` and a note for a client that is not ready. Pass `Select-PickerEntry` the label alone.
- The `Checking…` summary after a refresh can be too brief to see. Wait for the settled text instead of asserting it.
- The fake clients make every row except Codex `Not ready`. To read this machine's real clients, start with `Start-IDevelop -RealClients` and run no task. The probes start each client to read its sign-in state and models, as `scripts/check-real-window.ps1` does.
