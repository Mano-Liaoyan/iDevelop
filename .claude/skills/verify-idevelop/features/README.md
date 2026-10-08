# iDevelop verification map

This directory is the maintained source for verifying iDevelop's user-facing behavior in its real window on Windows. Read the index before driving the app, then use the matching feature file as the recipe.

## Baseline preconditions

- The Release build is current. Run `dotnet build -c Release` after every change.
- A session started with `Start-IDevelop` runs, and `Test-IDevelop` ends with `Doctor: worth driving`.
- The session opened its own copy of `samples/storage-change`. Its window title is `project - iDevelop`.
- The sample holds three tasks in this sidebar order. `Design the workflow file format` asks for Claude Code with `claude-opus-5-5` at `high`. `Implement atomic save` asks for Codex with GPT-6-Sol at `medium`. `Review the storage change` has no agent.
- Its three connections are a dependency from the first task to the second, a context connection from the first to the third, and a dependency from the second to the third. Every task is an Implement node in Autonomous conversation.
- The fake Codex is `Ready · 3 models`. Claude Code, Pi, and Antigravity CLI are `Not ready`.

## Driving conventions

- Start each call with `Import-Module ./scripts/real-window.psm1` and `$s = Connect-IDevelop`.
- Find elements by automation id. Find a card by its title text through `Find-NameOutside $s.Window <title> 'SidebarTasks'`.
- Choose a task through `Select-Element (Get-SidebarTasks $s.Window)[<index>]`. UI Automation cannot select a card.
- Wait for state with `Wait-Until`, not a fixed sleep.
- Start a recipe that needs a clean sample in a new session. `Stop-IDevelop` the old one first.

## Proof and skip reporting

- Record each expectation with `Assert-Step`, so `transcript.txt` holds the PASS and FAIL lines.
- Run `Save-Evidence` before and after the action. The screenshots show the action and the resulting state.
- Check side effects with a second view. Read the saved workflow JSON after a save, and `events.jsonl` after a run.
- Report the run folder `.verify/<run>/` and what it holds.
- Report a sub-feature that needs a pointer or a key as covered by the named headless test, not as verified in the real window.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing the user-visible behavior. It then uses exactly four H2 sections in this order.

1. `Sub-features` lists short IDs with one line for each behavior.
2. `How to get to it (user POV)` lists every user entry point.
3. `Driving it with real-window.psm1` starts with `Preconditions:` and uses labeled bullets that pair each user action with an exact command and observable result.
4. `Gotchas` lists traps that can waste or invalidate a verification run.

Keep implementation details out of the map. Name only user paths, stable handles, required state, commands, and observable proof.

## Features

- [Open a project and save edits](./open-and-save.md) covers opening a folder, editing a task, saving, and the unsaved-changes prompt.
- [Keep several projects and workflows open](./workspace.md) covers the project tree, New workflow, switching workflows with unsaved edits and undo, a run that stays with its own workflow, Close project's questions, and reopening the previous projects.
- [The canvas](./canvas.md) covers the cards, adding tasks through the Add popover, the card and connection menus, the canvas keys, undo and redo, the zoom and fit buttons, the minimap, and the sample's connections.
- [The Agents section and the agent pickers](./agents.md) covers client readiness and choosing a task's client, model, and reasoning level.
- [Run and cancel a task](./run-and-cancel.md) covers running, cancelling, and finishing a task through the fake Codex, and a refused start.
- [Talk to a task's agent](./conversation.md) covers Open in terminal, Continue, the conversation after Continue, Send during a turn, and Stop and send through the fake Codex. It drives `Composer`, `SendMessage`, `StopAndSend`, `OpenInTerminal`, `Conversation`, `TurnReply<n>`, `WaitingMessages`, and `TerminalNote`.
- [The conversation view](./conversation-view.md) covers opening a task's conversation from the inspector and from card attention, sending, earlier attempts, drafts across task and layout switches, the dock, and closing. It drives `OpenConversation`, `CardAttention`, `ConversationComposer`, `ConversationSend`, `AttemptPicker`, `ReturnToCurrent`, `ConversationLayout`, and `CloseConversation`.
- [Run a whole workflow](./workflow-run.md) covers Run Workflow's preflight, starting a run, the run bar's status, Resume, and Stop Workflow, the cards and inspector of tasks the run owns, a run task's conversation, an Approval node's request, and a run that goes on while another project is shown. It drives `RunWorkflow`, `PreflightSheet`, `PreflightStart`, `PreflightCancel`, `WorkflowRunBar`, `RunStatus`, `RunProgress`, `RunActivity`, `ResumeRun`, `StopWorkflow`, `DismissRun`, `RunTaskStatus`, `ApproveGate`, and `SendBackGate`.
- [Recover a workflow run's tasks](./workflow-recovery.md) covers a blocked task's evidence and Preserve and restore, Close as stopped for a turn a crash left uncertain, a stopping run that waits for it, a stale task's rebase, and a run's interrupted review fix. It drives `RecoveryPaths`, `RecoveryClient`, `RecoveryShowOwner`, `PreserveCheckout`, `RestoreCheckout`, `RecoveryReason`, `CloseAsStopped`, `ReviewUpdatedInputs`, `ApproveRebase`, `ContinueFix`, and `RetryFix`.
- [Generate a workflow from a description](./generate.md) covers the empty-state card, the Generate sheet, and the Chat-mode Plan task it places and runs through the fake Codex.
- [The inspector](./inspector.md) covers the empty, task, and connection views, their shared right edges, the filter, the folds, the reverts, and the library's More menu, in both themes.
- [The theme switch](./theme.md) covers the System, Light, and Dark segments and their persistence across a restart.
