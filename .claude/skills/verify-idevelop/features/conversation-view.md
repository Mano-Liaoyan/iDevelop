# The conversation view

A user reads and continues a node's whole conversation in the main area, or in a dock under the canvas. The view shows every line of the node's log in order, renders Markdown, folds tool activity into one-line rows, and marks new sessions, waits, failures, stops, and send backs. The composer keeps one draft per node across node, workflow, and layout switches. The header names the node, its project and workflow, and its state. A line under the header says that the agent's questions arrive in its reply.

## Sub-features

- `view-open` opens the selected task's conversation from `OpenConversation` in the inspector's `Talk to the Agent` section, at the latest message.
- `view-attention` opens it from the card's `CardAttention` glyph, which shows instead of `CardStateGlyph` while the task waits, failed, or was interrupted.
- `view-history` shows the latest page of the log and earlier pages with `LoadEarlier`, and reads the same after a restart.
- `view-send` sends the composer's text with `ConversationSend`, which continues the node's session, and shows a refusal in the status bar.
- `view-draft` keeps each task's draft when the person selects another task, switches between `ConversationLayout` layouts, or closes and reopens the view.
- `view-dock` moves the view between the main area and a dock under the canvas with `ConversationLayout`, and `CloseConversation` or Escape closes it.
- `view-follow` follows streamed output at the end, keeps the reading position elsewhere, and offers `JumpToLatest`. It needs a wheel or a click in the scroll track, so `ConversationViewTests.Prepending_an_earlier_page_keeps_the_line_being_read_and_its_selected_text_in_place` and `ConversationViewTests.A_click_in_the_scroll_track_while_output_streams_stops_following_the_end_among_rows_of_different_heights` cover it headlessly.
- `view-links` opens a web link only after `OpenLink` in its confirmation, which shows the address in `LinkDestination`, and offers only `CopyLink` for any other link. It needs a pointer on message text, so `MarkdownViewTests` covers it headlessly.

## How to get to it (user POV)

- Choose a task, then choose `Open conversation` in the inspector's `Talk to the Agent` section.
- Choose the question glyph on a card that waits for you, or the failure glyph on a card that failed.
- With the view open, choose another task in the sidebar or, while docked, on the canvas. The view follows the selection.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`, and `New-Item -ItemType File $s.Gate` has run, so each turn ends at once with `DONE`.

- **Open.** Run the second node with `Select-Element (Get-SidebarTasks $s.Window)[1]` and `Invoke-Element (Find-ById $s.Window 'RunTask')`, then `Wait-Until { (Find-ById $s.Window 'RunTaskStatus').Current.Name -eq 'Completed' } 30`. Run `Invoke-Element (Find-ById $s.Window 'OpenConversation')`. Then run `Assert-Step $s ((Find-ById $s.Window 'ConversationTitle').Current.Name -eq 'Implement atomic save') 'the view names the node'`, `Assert-Step $s ((Find-ById $s.Window 'ConversationBreadcrumb').Current.Name -eq 'project › Workflow') 'the view names its project and workflow'`, and `Assert-Step $s ((Find-ById $s.Window 'ConversationStatus').Current.Name -eq 'Completed') 'the view shows the state'`. Run `Save-Evidence $s 'conversation-open'`.
- **Send.** Run `Set-Text (Find-ById $s.Window 'ConversationComposer') 'banana'` and `Invoke-Element (Find-ById $s.Window 'ConversationSend')`. Then run `Assert-Step $s ((Wait-Until { (Get-Value (Find-ById $s.Window 'ConversationComposer')) -eq '' } 10) -eq $true) 'the composer clears once the message goes'`. Run `Save-Evidence $s 'conversation-sent'`. The node's log holds a `personMessage` line with `banana`.
- **Drafts and the dock.** Run `Set-Text (Find-ById $s.Window 'ConversationComposer') 'Check edge cases'`, `Invoke-Element (Find-ById $s.Window 'ConversationLayout')`, and `Assert-Step $s ((Find-ById $s.Window 'ConversationLayout').Current.Name -eq 'Expand to the main area') 'the view sits in the dock'`. Run `Select-Element (Get-SidebarTasks $s.Window)[0]` and `Assert-Step $s ((Find-ById $s.Window 'ConversationTitle').Current.Name -eq 'Design the workflow file format') 'the dock follows the selected task'` and `Assert-Step $s ((Get-Value (Find-ById $s.Window 'ConversationComposer')) -eq '') 'the other task has its own draft'`. Run `Select-Element (Get-SidebarTasks $s.Window)[1]` and `Assert-Step $s ((Get-Value (Find-ById $s.Window 'ConversationComposer')) -eq 'Check edge cases') 'the draft came back'`. `Get-Value (Find-ById $s.Window 'Composer')` in the inspector reads the same text. Run `Save-Evidence $s 'conversation-dock'`.
- **Close.** Run `Invoke-Element (Find-ById $s.Window 'CloseConversation')` and `Assert-Step $s ($null -eq (Find-ById $s.Window 'ConversationView' 1)) 'the view closed'`. The task keeps running or waiting as before.

## Gotchas

- iDevelop asks no live question. A question a client asks arrives as text in its reply, and the person answers with a message.
- `CardAttention` exists only while a card needs the person, so `Find-ById $s.Window 'CardAttention' 1` returns `$null` otherwise. UI Automation may not reach a button inside a card; `ConversationWindowTests.A_failed_card_opens_its_conversation_from_its_attention_glyph` covers it headlessly.
- `ConversationStopAndSend`, `ConversationApprove`, `ConversationStop`, `LoadEarlier`, and `JumpToLatest` exist only while shown. Pass a short timeout.
- Message text is Markdown rendered into several text elements, so read a message's whole source with its `CopyMessage` button and the clipboard, not from one element.
- A message longer than 20,000 characters, with more than 1,000 table cells or 500 links, or with blocks that weigh more than 1,000, where a code block weighs 10 and any other block 1, shows as its source in one text element, unformatted.
