# Talk to a node's agent

A user writes to a node's agent from the composer in the `Talk to the Agent` section of the inspector. A message continues the node's session: while a turn runs it waits for that turn to end, and Stop and send stops the turn first. A Chat node waits for the person after each turn, and `Approve` makes its last reply the result. Open in terminal copies the client's own command for the session, after a change into the node's folder. Claude Code in a May ask or Chat node asks while its turn runs, and Send answers with the chosen choices and the draft. Any other client asks as text in its reply, and the person answers it with a message.

## Sub-features

- `talk-terminal` copies `Set-Location -LiteralPath '<folder>'; codex resume <session>` and says so in the status bar.
- `talk-continue` sends a message to a node whose turn ended, which continues its session.
- `talk-queue` holds a message sent during a turn until the turn ends.
- `talk-stop` stops the running turn's process tree and continues the session with the message at once.
- `talk-approve` makes a Chat node's last reply its result with `Approve`.
- `talk-keys` sends with Ctrl+Enter, while Enter starts a new line. It needs keys, so `ConversationTests.Ctrl_Enter_in_the_composer_continues_a_finished_session_and_Enter_starts_a_new_line` covers it headlessly.

## How to get to it (user POV)

- Choose a node in the sidebar or on the canvas, write in the composer in the `Talk to the Agent` section, and choose `Send` or press Ctrl+Enter.
- While the node runs, write in the composer and choose `Stop and send`.
- Once a turn has ended, choose `Open in terminal`.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`.
- `$s.Gate` exists. Run `New-Item -ItemType File $s.Gate`. Every turn of the fake Codex, fresh or continued, starts a sleeper, writes its id to `$s.Run\fake-bin\sleeper.pid`, and waits until `$s.Gate` exists. Then it replays a successful turn that reports session `01a104d5-d442-71a1-9b08-8938c119e5ae` and ends with `DONE`.

- **First run.** Choose the second node, set its conversation to Chat, and run it. Run `Select-Element (Get-SidebarTasks $s.Window)[1]`, `Select-PickerEntry $s 'TaskConversation' 'Chat'`, and `Invoke-Element (Find-ById $s.Window 'RunTask')`. Then run `Assert-Step $s ((Wait-Until { (Find-ById $s.Window 'RunTaskStatus').Current.Name -eq 'Input required · reply' } 30) -eq $true) 'the Chat node waits for a reply'` and `Assert-Step $s ((Get-Value (Find-ById $s.Window 'LastReply')) -eq 'DONE') 'its reply is DONE'`.
- **Open in terminal.** Run `Invoke-Element (Find-ById $s.Window 'OpenInTerminal')`. Then run `Assert-Step $s ((Wait-Until { (Get-Clipboard) -like '*codex resume 01a104d5-d442-71a1-9b08-8938c119e5ae' } 10) -eq $true) 'the clipboard holds the command'` and `Assert-Step $s ((Find-ById $s.Window 'Status').Current.Name -like 'Copied the command*') 'the status bar names the command'`.
- **Approve.** Run `Invoke-Element (Find-ById $s.Window 'Approve')` and `Assert-Step $s ((Wait-Until { (Find-ById $s.Window 'RunTaskStatus').Current.Name -eq 'Completed' } 10) -eq $true) 'Approve makes the reply the result'`. Run `Select-PickerEntry $s 'TaskConversation' 'Autonomous'`, so the next turn does not wait. The edit makes the result `Stale`.
- **Send during a turn.** Remove the gate with `Remove-Item $s.Gate`, run `Invoke-Element (Find-ById $s.Window 'RetryTask')`, and wait for `RunTaskStatus` to read `Running`. Run `Set-Text (Find-ById $s.Window 'Composer') 'and an apple'` and `Invoke-Element (Find-ById $s.Window 'SendMessage')`. The composer clears and the node still runs. Run `Save-Evidence $s 'waiting'`.
- **Stop and send.** Read the running turn's sleeper with `$sleeper = [int](Get-Content "$($s.Run)\fake-bin\sleeper.pid")`. Run `Set-Text (Find-ById $s.Window 'Composer') 'Stop. Use a pear.'` and `Invoke-Element (Find-ById $s.Window 'StopAndSend')`. Then run `Assert-Step $s ((Wait-Until { -not (Get-Process -Id $sleeper -ErrorAction SilentlyContinue) } 30) -eq $true) 'Stop and send stops the turn and what it started'`.
- **Finish.** Run `New-Item -ItemType File $s.Gate`. Then run `Assert-Step $s ((Wait-Until { (Find-ById $s.Window 'RunTaskStatus').Current.Name -eq 'Completed' } 30) -eq $true) 'the continued turn completes'`. Run `Save-Evidence $s 'conversation'`. The node's log holds a `personMessage` line with ``"and an apple`n`nStop. Use a pear."``.

## Gotchas

- Never paste the copied command into a terminal. `codex` there is the user's real Codex, which spends quota and has no session with the fake's id.
- Open in terminal replaces the text on the Windows clipboard. While another program holds the clipboard, the status bar starts with `Couldn't copy the command.` and shows the command.
- The gate decides when each turn ends. While it exists, every turn ends at once, so no message can wait and `Stop and send` never shows.
- Each turn rewrites `sleeper.pid`. Read it after the turn starts and before the next one. A turn that ends on its own leaves its sleeper running, and `Stop-IDevelop` stops it.
- `StopAndSend` and `LastReply` exist only while shown, so `Find-ById` returns `$null` for them otherwise. Pass a short timeout.
