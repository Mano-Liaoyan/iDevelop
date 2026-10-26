# Workspace sessions

## Task

W1 is the slice "Open multiple projects and workflows" of the workspace milestone the owner approved on 2026-10-06. Its completion condition is that two projects with several workflows each stay open, and workflow rows hide their nodes by default. Switching keeps unsaved state, undo history, and running nodes, and existing projects still load.

Pull request [#37](https://github.com/Mano-Liaoyan/iDevelop/pull/37) built it and merged to `main` as `e9c8f5c`. The sidebar is a project tree. Each project lists its workflows, and a workflow row lists its tasks only when expanded. **Open Folder** adds a project. Each project row has **New workflow** and **Close project**, and F2 or **Rename** renames a workflow inline. The blueprint icon and color fields that the [D0 record](2026-10-06-d0-design-validation.md) approved are part of W1's format.

## Decisions and reasons

### The local session owns open projects and nothing about execution

`MainWindowViewModel` is the local open-project session. It holds the open projects in the order they were opened, the shown workflow, and view preferences such as which workflow rows are expanded. It never starts or stops a node, except through the confirmation that **Close project** and window close ask.

The window writes `session.json` beside `settings.json` in the per-user application data folder, and opens those projects again at the next start. A folder's identity is its full path without a trailing separator, compared without case on Windows and macOS. The file is written atomically. Quitting keeps a project in the list, and **Close project** removes it. A remembered folder that does not open stays in the sidebar as **Folder not found** or **Couldn't open**, with a **Forget** button. An unsaved new workflow is not restored.

Selection, expansion, pan, zoom, and conversation drafts are view state, so they stay in Desktop view models. Each workflow keeps its own unsaved edits, undo history, selection, pan, zoom, and drafts across switches.

### One project holds every workflow

`ProjectViewModel` holds one canvas per workflow file and a map from each task to the workflow that holds it. Canvases are created for every workflow when the project opens.

`WorkflowDocument.OpenProject` opens every top-level JSON file in `.idp/workflows/`. It refuses a folder whose files repeat a workflow ID or share a task ID, because each node's log is keyed by task ID within a project folder. Titles are never identifiers, so two projects can each hold a workflow named "Build" with a task named "Implement".

### Running nodes survive every switch

Switching a project or workflow keeps every canvas, and a running task keeps running. If the open conversation belongs to another workflow, the window closes it and disposes its view model. The task's draft stays in the project session. A workflow row and a project row show a running mark while any of their tasks runs.

**Close project** asks before it stops a running task or drops unsaved edits. It gathers both answers before either acts, completes any requested saves, and only then closes the project. Window close asks the same questions across every open project. A node that waits for the person holds no process, so the close question does not count it as running.

### A workflow name and a blueprint icon and color need no format bump

The format stays `idevelop.workflow/3`, and blueprint files stay `idevelop.blueprint/1`. A workflow gains an optional `name`. A blueprint, embedded or in a library, gains an optional `icon` and `color`. Each property is written only when set.

The values are closed sets that the reader parses into `BlueprintIcon` and `BlueprintColor`. The icons are `code`, `taskList`, `ruler`, `glasses`, `personAvailable`, and `documentSearch`. The colors are `indigo`, `cyan`, `purple`, `mint`, `brown`, and `gray`. Each set is the six node kinds' existing glyphs or hues. An unknown name fails the open with a message that names the file and the value. `Blueprint.Equals` compares both fields, so an embedded copy and a library copy still conflict on any difference.

Files need no migration. A file that uses none of these properties saves byte for byte, and a test pins that with the sample.

An older build refuses a file that uses any of these properties. The reader rejects unknown properties, so the older build refuses the file rather than dropping the value. Its message says the property could not be mapped. A format bump would make every save unreadable by older builds, even a save that sets none of them, and would add a third reader path.

W1 carries the fields through the model, both file formats, library saves, and the blueprint editor's edit and derive paths. A library blueprint shows its kind's tile.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| A Core `Workspace` that owns projects, documents, selection, and persistence | Selection, expansion, viewport, and drafts are view state. Moving them into Core leaks presentation into Core or splits each one across two layers. |
| Create a canvas only when its workflow is first selected | Eager canvases are simpler, and expanded tree rows read their outline from the canvas. Their cost at 500 tasks is unmeasured. |
| A new format, `idevelop.workflow/4` | It makes every save unreadable by older builds and adds a reader path, and it only improves the downgrade message. |
| Persist nothing and reopen projects by hand | The plan places the open-project list in local app settings, and reopening is in its verification table. |
| Store the workflow name outside the file | A name belongs with the workflow it names, travels with Git, and is shared by everyone who opens the project. |

## Open issues

1. No Windows real-window run happened. The workspace guide's PowerShell steps have not run.
2. Two windows overwrite each other's session file.
3. A missing folder keeps only its path, so its selection and expanded rows are lost.
4. No test runs an older build against a file with a new property.
5. The cost of eager canvases at 500 tasks is unmeasured.
6. Window close prompts are proven headless only, because the close request never reached the app on bare Xvfb.
7. **Close project** reuses the running-nodes dialog's **Stop and leave** and **Keep running** buttons, so its stop button says leave.
8. A workflow file copied in by hand that shares a task ID is caught only when the project next opens.
