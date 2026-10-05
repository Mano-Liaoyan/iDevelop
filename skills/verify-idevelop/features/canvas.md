# The canvas

The canvas shows each task as a card with its title, status, agent, type, and instructions, joined by dependency and context connections. A user adds tasks, zooms in and out, fits every task on the screen, and sees the whole workflow in the minimap at the bottom right.

## Sub-features

- `add-task` adds a task from `New task` in the sidebar, selects it, and marks the project unsaved.
- `card-content` shows each task's title, status, agent, and instructions on its card.
- `zoom` steps the canvas larger with `Zoom in` and smaller with `Zoom out`.
- `fit` brings every card into view with `Fit to screen`.
- `sidebar-reveal` scrolls the canvas to a card chosen in the sidebar when it is out of view.
- `minimap` shows the whole workflow at the bottom right.
- `connections` draws the sample's dependency and context connections, which the saved file keeps.

## How to get to it (user POV)

- Choose `New task` at the top of the sidebar.
- Right-click the canvas and choose `Add task`.
- Choose the `Zoom in`, `Zoom out`, and `Fit to screen` buttons at the canvas's bottom left.
- Choose a task's row in the sidebar.
- Click, drag, or turn the wheel over the minimap.
- Drag from a task's output handle onto another task's input handle to connect them. Click or right-click a connection to change its kind.

## Driving it with real-window.psm1

Preconditions:

- A new session on the sample, started with `$s = Start-IDevelop`.
- The doctor reports `Doctor: worth driving`.
- These measures are defined in the shell that runs a bullet. `$title` reads the second card's title height, `$editor` is the canvas rectangle, and `$card` reads the third card's title rectangle.

```powershell
$title = { (Find-NameOutside $s.Window 'Implement atomic save' 'SidebarTasks').Current.BoundingRectangle.Height }
$editor = (Find-ById $s.Window 'Editor').Current.BoundingRectangle
$card = { (Find-NameOutside $s.Window 'Review the storage change' 'SidebarTasks').Current.BoundingRectangle }
```

- **Cards.** Read the three cards. Each card shows one subtitle: an idle card shows its agent as `CardAgent`, and every other card shows its status or what it needs as `CardStatus`. Run `(Find-ById $s.Window 'Editor').FindAll('Children', [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CardAgent')).Current.Name`. It lists `Codex · GPT-6-Sol · medium` alone. With `CardStatus` in place of `CardAgent` it lists `Claude Code isn't ready` and `Choose an agent`, because Claude Code is not ready under the fake clients and the third task has no agent.
- **Connections.** Show them and read them from the file. Run `Invoke-Element (Find-ById $s.Window 'FitToScreen')` and `Save-Evidence $s 'connections'`. Each line takes the color of the card it leaves, and the sample's three cards are Implement tasks, so the screenshot shows a solid indigo line with an arrow from the first card to the second, a dashed indigo line from the first card to the third that passes under the second card, and a solid indigo line from the second card to the third. The copied file under `connections\workflows` lists connection kinds `dependency`, `context`, and `dependency`.
- **Zoom in.** Run `$before = & $title`, `Invoke-Element (Find-ById $s.Window 'ZoomIn')`, and `$after = Wait-Until { $v = & $title; if ($v -gt $before) { $v } } 5`. The title grows by about a quarter, one step of 2^(1/3).
- **Zoom out.** Run `Invoke-Element (Find-ById $s.Window 'ZoomOut')` and `Wait-Until { $v = & $title; if ($v -lt $after) { $v } } 5`. The height returns to `$before`. The window title stays `project - iDevelop`, because the view is not part of the file.
- **Fit.** Zoom in until the third card leaves the canvas, then fit. Run `1..4 | ForEach-Object { Invoke-Element (Find-ById $s.Window 'ZoomIn') }`. `$editor.Contains((& $card))` is `$false`. Run `Invoke-Element (Find-ById $s.Window 'FitToScreen')`, and `Wait-Until { $editor.Contains((& $card)) } 5` returns `$true`.
- **Reveal from the sidebar.** Zoom in four times again, so the third card leaves the canvas, then choose its row. Run `Select-Element (Get-SidebarTasks $s.Window)[2]`. `Wait-Until { $editor.Contains((& $card)) } 5` returns `$true`, and `Get-Value (Find-ById $s.Window 'TaskTitle')` is `Review the storage change`.
- **Add a task.** Run `Invoke-Element (Find-ById $s.Window 'AddTask')`. `TaskCount` reads `4`, `TaskTitle` reads `New task`, the last sidebar row is `New task`, and the window title becomes `project* - iDevelop`. Run `Set-Text (Find-ById $s.Window 'TaskTitle') 'Write the changelog'`. `Find-NameOutside $s.Window 'Write the changelog' 'SidebarTasks'` finds the new card. Run `Save-Evidence $s 'added-task'`.
- **Minimap.** Run `(Find-ById $s.Window 'Minimap').Current.BoundingRectangle`. It lies at the bottom right of `$editor`, and the screenshot shows one gray block per task in it.

## Gotchas

- UI Automation sees no card, connection, or minimap item as an element. It sees each card's text. Measure zoom and position from a card title's `BoundingRectangle`.
- Every element reports `IsOffscreen` as false. Compare a card's rectangle with `$editor` to decide whether it is in view.
- The `New task` button's label is also `New task`, outside the sidebar list. Retitle a new task before finding its card by name.
- Creating or selecting a connection needs a pointer drag or click, which UI Automation patterns cannot do. The real-window path uses the sample's existing connections. `ConnectionTests` covers dragging an output onto an input, the cycle refusal, clicking and right-clicking a connection, and deleting one.
- The canvas menu's `Add task`, dragging a card, the minimap's click and wheel, and the Delete key need a pointer or a key. `CanvasTests` covers each of them headlessly. No test drags in the minimap.
