# The conversation view

A user reads and continues a task's whole conversation in the main area, or in a dock under the canvas. The view shows every message of the selected attempt's continuation chain in order, renders Markdown, folds tool activity into one-line rows, and marks attempt boundaries, failures, and stops. The composer keeps one draft per task across task, workflow, and layout switches. Questions show at their place in the history. The header names the task, its project and workflow, its status, and an attempt picker that also lists independent and cancelled earlier attempts. A line under the header says what the task's client cannot do here.

## Sub-features

- `view-open` opens the selected task's conversation from `OpenConversation` in the inspector's `Talk to the Agent` section, at the latest message.
- `view-attention` opens it from the card's `CardAttention` glyph, which shows instead of `CardStateGlyph` while the task waits, failed, or was interrupted.
- `view-history` lists the attempts in `AttemptPicker`, oldest first, and shows an earlier attempt read-only with `HistoricalNote` and `ReturnToCurrent`.
- `view-send` sends the composer's text with `ConversationSend`, clears it only after iDevelop recorded it, and shows a refusal in `ConversationNotice`.
- `view-draft` keeps each task's draft when the person selects another task, switches between `ConversationLayout` layouts, or closes and reopens the view.
- `view-dock` moves the view between the main area and a dock under the canvas with `ConversationLayout`, and `CloseConversation` or Escape closes it.
- `view-follow` follows streamed output at the end, keeps the reading position elsewhere, and offers `JumpToLatest`. It needs a wheel or a click in the scroll track, so `ConversationViewTests.Prepending_an_earlier_page_keeps_the_entry_being_read_and_its_selected_text_in_place` and `ConversationViewTests.A_click_in_the_scroll_track_while_output_streams_stops_following_the_end_among_rows_of_different_heights` cover it headlessly.
- `view-links` opens a web link only after `OpenLink` in its confirmation, which shows the address in `LinkDestination`, and offers only `CopyLink` for any other link. It needs a pointer on message text, so `MarkdownViewTests` covers it headlessly.

## How to get to it (user POV)

- Choose a task, then choose `Open conversation` in the inspector's `Talk to the Agent` section.
- Choose the question glyph on a card that waits for you, or the failure glyph on a card that failed.
- With the view open, choose another task in the sidebar or, while docked, on the canvas. The view follows the selection.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`, so runs go through the fake Codex.
- `Wait-Until { (Find-ById $s.Window 'AgentCodex').Current.Name -like 'Ready*' } 60` has returned `$true`, and `New-Item -ItemType File $s.Gate` has run, so each turn ends at once with `DONE`.

- **Open.** Run the second task with `Select-Element (Get-SidebarTasks $s.Window)[1]` and `Invoke-Element (Find-ById $s.Window 'RunTask')`, then `Wait-Until { (Find-ById $s.Window 'LastRunStatus').Current.Name -eq 'Succeeded' } 30`. Run `Invoke-Element (Find-ById $s.Window 'OpenConversation')`. Then run `Assert-Step $s ((Find-ById $s.Window 'ConversationTitle').Current.Name -eq 'Implement atomic save') 'the view names the task'`, `Assert-Step $s ((Find-ById $s.Window 'ConversationBreadcrumb').Current.Name -eq 'project › Workflow') 'the view names its project and workflow'`, and `Assert-Step $s ((Find-ById $s.Window 'ConversationStatus').Current.Name -eq 'Succeeded') 'the view shows the status'`. `ClientLimitations` reads `Questions arrive as text. Permission requests are declined.` Run `Save-Evidence $s 'conversation-open'`.
- **Send.** Run `Set-Text (Find-ById $s.Window 'ConversationComposer') 'banana'` and `Invoke-Element (Find-ById $s.Window 'ConversationSend')`. Then run `Assert-Step $s ((Wait-Until { (Get-Value (Find-ById $s.Window 'ConversationComposer')) -eq '' } 10) -eq $true) 'the composer clears once the message is recorded'` and `Assert-Step $s ((Wait-Until { (Get-Value (Find-ById $s.Window 'AttemptPicker')) -eq 'Attempt 2 (current) · Succeeded · Codex, continues 1' } 30) -eq $true) 'Send continues the session in a new attempt'`. Run `Save-Evidence $s 'conversation-sent'`. The newest attempt's `requested` line has `prompt` `banana`.
- **Earlier attempt.** Choose the first attempt with `Select-PickerEntry $s 'AttemptPicker' 'Attempt 1 · Succeeded · Codex'`. Then run `Assert-Step $s ($null -ne (Find-ById $s.Window 'HistoricalNote' 5)) 'an earlier attempt is read-only'` and `Assert-Step $s (-not (Find-ById $s.Window 'ConversationComposer').Current.IsEnabled) 'its composer takes no text'`. Run `Invoke-Element (Find-ById $s.Window 'ReturnToCurrent')` and `Assert-Step $s ((Find-ById $s.Window 'ConversationComposer').Current.IsEnabled) 'the current conversation takes text again'`.
- **Drafts and the dock.** Run `Set-Text (Find-ById $s.Window 'ConversationComposer') 'Check edge cases'`, `Invoke-Element (Find-ById $s.Window 'ConversationLayout')`, and `Assert-Step $s ((Find-ById $s.Window 'ConversationLayout').Current.Name -eq 'Expand to the main area') 'the view sits in the dock'`. Run `Select-Element (Get-SidebarTasks $s.Window)[0]` and `Assert-Step $s ((Find-ById $s.Window 'ConversationTitle').Current.Name -eq 'Design the workflow file format') 'the dock follows the selected task'` and `Assert-Step $s ((Get-Value (Find-ById $s.Window 'ConversationComposer')) -eq '') 'the other task has its own draft'`. Run `Select-Element (Get-SidebarTasks $s.Window)[1]` and `Assert-Step $s ((Get-Value (Find-ById $s.Window 'ConversationComposer')) -eq 'Check edge cases') 'the draft came back'`. `Get-Value (Find-ById $s.Window 'Composer')` in the inspector reads the same text. Run `Save-Evidence $s 'conversation-dock'`.
- **Close.** Run `Invoke-Element (Find-ById $s.Window 'CloseConversation')` and `Assert-Step $s ($null -eq (Find-ById $s.Window 'ConversationView' 1)) 'the view closed'`. The task keeps running or waiting as before.

## Gotchas

- The fake Codex reports no question, and Claude Code is `Not ready` in a session, so a structured question cannot appear on Windows. `ConversationViewTests.A_stale_answer_stays_as_a_draft_and_moves_into_the_composer_once_when_the_question_is_deferred` covers the question controls, `QuestionOption`, `QuestionOther`, `SubmitAnswer`, and `RequestStatus`, headlessly.
- iDevelop asks no live question yet. A question a client asks moves to the person's next message at once, and the card then shows the waiting glyph.
- `CardAttention` exists only while a card needs the person, so `Find-ById $s.Window 'CardAttention' 1` returns `$null` otherwise. UI Automation may not reach a button inside a card; `ConversationWindowTests.A_failed_card_opens_its_conversation_from_its_attention_glyph` covers it headlessly.
- `HistoricalNote`, `ConversationStopAndSend`, `ConversationCancel`, `ConversationMarkDone`, `ConversationTerminal`, `LoadEarlier`, `JumpToLatest`, and `ConversationNotice` exist only while shown. Pass a short timeout.
- Message text is Markdown rendered into several text elements, so read a message's whole source with its `CopyMessage` button and the clipboard, not from one element.
- A message longer than 20,000 characters, with more than 1,000 table cells or 500 links, or with blocks that weigh more than 1,000, where a code block weighs 10 and any other block 1, shows as its source in one text element, unformatted.
