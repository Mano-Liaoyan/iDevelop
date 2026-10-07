# Workspace sessions

## Task

W1 is the slice "Open multiple projects and workflows" of the [approved execution plan](2026-10-06-workspace-execution-plan.md#approved-delivery-slices). Its completion condition is that two projects with several workflows each stay open, and workflow rows hide their nodes by default. Switching keeps unsaved state, undo history, and run ownership, and existing projects still load.

Pull request [#37](https://github.com/Mano-Liaoyan/iDevelop/pull/37) built it and merged to `main` as `e9c8f5c`. The sidebar is a project tree. Each project lists its workflows, and a workflow row lists its tasks only when expanded. **Open Folder** adds a project. Each project row has **New workflow** and **Close project**, and F2 or **Rename** renames a workflow inline. The [D0 record](2026-10-06-d0-design-validation.md) placed the blueprint icon and color fields in W1's format review.

GPT-6 Astra critiqued the design note before implementation and made six findings, all accepted in full or in part. An Opus frontend verification made seven findings and an Astra difficult-task review made three on head `d387412`. The fix round at `c1b489e` closed all ten. A later commit, `6305ea2`, closed two low findings from the re-verification.

## Decisions and reasons

### The local session owns open projects and nothing about execution

`MainWindowViewModel` is the local open-project session. It holds the open projects in the order they were opened, the shown workflow, and view preferences such as which workflow rows are expanded. It never starts, stops, or reassigns a run, except through the confirmation that **Close project** and window close ask.

The window writes `session.json` beside `settings.json` in the per-user application data folder, and opens those projects again at the next start. A folder's identity is its full path without a trailing separator, compared without case on Windows and macOS. The file is written atomically. Quitting keeps a project in the list, and **Close project** removes it. A remembered folder that does not open stays in the sidebar as **Folder not found** or **Couldn't open**, with a **Forget** button. An unsaved new workflow is not restored.

Selection, expansion, pan, zoom, and conversation drafts are view state, so they stay in Desktop view models. Each workflow keeps its own unsaved edits, undo history, selection, pan, zoom, and drafts across switches.

### One runner per project folder follows every workflow

`ProjectViewModel` holds one `ProjectRuns` per open folder, one canvas per workflow file, and a map from each task to the workflow that holds it. The project calls `ProjectRuns.Follow` for every workflow and again on each change. A review loop therefore goes on while its workflow is hidden. Canvases are created for every workflow when the project opens.

`WorkflowDocument.OpenProject` opens every top-level JSON file in `.idp/workflows/`. It refuses a folder whose files repeat a workflow ID or share a task ID, because attempts and the run lock are keyed by task ID within a project folder. Titles are never identifiers, so two projects can each hold a workflow named "Build" with a task named "Implement". The [E1 record](2026-10-07-run-and-input-records.md#open-issues) relies on this uniqueness. Its landing check on the merged tree found that `OpenProject` still refuses shared IDs and that no command duplicates or imports a whole workflow.

### Run ownership survives every switch

Switching a project or workflow keeps every runner and canvas, and a running task keeps running. If the open conversation belongs to another workflow, the window closes it and disposes its view model. The task's draft stays in the project session. A canvas's run bar shows only the active attempts of tasks that its workflow has held during the session, and that set only grows. A running task deleted from its workflow therefore still shows in its own workflow's bar. A workflow row and a project row show a running mark while any of their tasks runs.

**Close project** asks before it stops a running task or drops unsaved edits. It gathers both answers before either acts, completes any requested saves, and only then disposes the runner. Window close asks the same questions across every open project and awaits every runner's disposal. A review that rests between turns holds no process, so the close question does not count it as running. It resumes when its project opens again, as it already did when a person opened the folder by hand.

### A workflow name and a blueprint icon and color need no format bump

The format stays `idevelop.workflow/3`, and blueprint files stay `idevelop.blueprint/1`. A workflow gains an optional `name`. A blueprint, embedded or in a library, gains an optional `icon` and `color`. Each property is written only when set.

The values are closed sets that the reader parses into `BlueprintIcon` and `BlueprintColor`. The icons are `code`, `taskList`, `ruler`, `glasses`, `personAvailable`, and `documentSearch`. The colors are `indigo`, `cyan`, `purple`, `mint`, `brown`, and `gray`. Each set is the six node kinds' existing glyphs or hues. An unknown name fails the open with a message that names the file and the value. `Blueprint.Equals` compares both fields, so an embedded copy and a library copy still conflict on any difference.

Existing files need no migration. A file that uses none of the new properties saves byte for byte as before, and a test pins that with the sample.

The downgrade limit is that an older build refuses a file that uses any new property. The reader already rejects unknown properties, so the older build refuses the file rather than dropping the value. Its message says the property could not be mapped instead of naming a newer format. A bump would have made every save unreadable by older builds, even a save that sets nothing new, and would have added a third reader path.

W1 carries the fields through the model, both file formats, library saves, and the blueprint editor's edit and derive paths. Drawing a tile in the custom look and the editor's pickers come later. Until then a library blueprint keeps its kind's tile.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| A Core `Workspace` that owns projects, documents, selection, and persistence | Selection, expansion, viewport, and drafts are view state. Moving them into Core leaks presentation into Core or splits each one across two layers. |
| One `ProjectRuns` per workflow | Attempts, locks, and crash reconciliation are per project folder. Two runners on one folder would reconcile each other's attempts and race on review advancement. |
| Create a canvas only when its workflow is first selected | The project calls `Follow`, so a lazy canvas would not stall a review loop. Eager canvases are simpler, and expanded tree rows read their outline from the canvas. Their cost at 500 tasks is unmeasured. |
| Bump to `idevelop.workflow/4` | It makes every save unreadable by older builds and adds a reader path, and it only improves the downgrade message. |
| Persist nothing and reopen projects by hand | The plan places the open-project list in local app settings, and reopening is in its verification table. |
| Store the workflow name outside the file | A name belongs with the workflow it names, travels with Git, and is shared by everyone who opens the project. |

## Changed artifacts

- `src/IDevelop.Core/Workflows/` adds `Workflow.Name` with an undoable rename, `BlueprintIcon` and `BlueprintColor`, and the optional file properties.
- `WorkflowDocument.OpenProject` and `WorkflowDocument.Create` replace the single-workflow open, and `Save` no longer refuses to write beside another workflow file.
- `ProjectRuns` follows several workflows and resolves each task through the workflow that holds it.
- `src/IDevelop.Desktop/` adds the project tree, `ProjectViewModel`, `WorkspaceSession` for the session file, and the close questions across projects.
- `skills/verify-idevelop/features/workspace.md` is the workspace navigation guide.

## Commands and observed results

| Command or check | Observed result |
| --- | --- |
| `dotnet build -c Release` at `c1b489e` | 0 warnings. |
| `dotnet test -c Release` at `c1b489e` | Core passed 371 tests with 9 platform skips. Desktop passed 356. |
| Each fix-round behavior test against `d387412` | Each failed before its fix and passes after it. A mutation check that made `Rename` and `Save` do nothing failed both strengthened Core tests. |
| The two low fixes at `6305ea2` | The button alignment test failed at -8 px before the fix and passes at 0. The **Forget** test failed before the fix. |
| Release build under Xvfb with a fake Codex | Screenshots in light and dark show collapsed workflow rows, a run kept across switches and a kept-open close, unsaved marks, and a restart that restores projects, selection, and expansion. |
| Landing check at `01cdc44`, rebased onto U1 | One conflict in `InspectorView.axaml`, resolved with U1's grid and W1's workflow name. Build with 0 warnings. Core passed 371 with 9 skipped, and Desktop passed 365. The license, token, icon, handoff, and PStack checks pass. |

## Open issues

1. No Windows real-window run happened. The workspace guide's PowerShell steps have not run.
2. Two windows overwrite each other's session file.
3. A missing folder keeps only its path, so its selection and expanded rows are lost.
4. No test runs an older build against a file with a new property.
5. The cost of eager canvases at 500 tasks is unmeasured.
6. Window close prompts are proven headless only, because the close request never reached the app on bare Xvfb.
7. **Close project** reuses the run dialog's **Stop and leave** and **Keep running** buttons, so its stop button says leave.
8. A workflow file copied in by hand that shares a task ID is caught only when the project next opens.

## Next action

C1 routes each conversation through the open-project session, as the [conversation record](2026-10-07-conversation.md) describes. A later slice draws custom blueprint tiles and adds the icon and color pickers.
