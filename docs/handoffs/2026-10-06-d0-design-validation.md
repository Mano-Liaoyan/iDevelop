# D0 design validation

## Task

On 2026-10-06 the user authorized D0 and lifted the execution plan's deferral of implementation. D0 validates the designs before the [approved delivery slices](2026-10-06-workspace-execution-plan.md#approved-delivery-slices) build them.

D0 has three workstreams. The Git input delivery probe and the UI prototypes are done. The client capability probe has not run because its real-client runs wait for the user's explicit approval. C1 remains gated on that probe.

No application code, test, script, or skill changed on `main`. The UI prototypes live on the local branch `d0/ui-prototypes` at commit `65efddd`. That branch is never pushed or merged.

## Decisions and reasons

### Isolated task branches preserve the inputs defined by the graph

The revised contract uses strategy 1, separate task branches and worktrees with an explicit join. The review's corrections left this choice in place, and the user's six decisions build on it. Each task starts from the base its dependencies define. Independent tasks can write concurrently. A killed writer dirties only its own worktree. A fan-in conflict has named paths and requires a resolution before the successor can start.

Strategy 2 uses one run branch with serialized writers. C's base is B's result, not A's, even though the graph makes B and C independent. Only one writer runs at a time, and the second writer decides a conflict by itself instead of Git reporting it.

The probe did not set up strategy 2 fairly. The choice does not rest on its lost line or its timing. The evidence corrections below explain those limits.

### The revised E2 contract defines ownership and recovery

These names and lifecycle rules are the revised E2 contract. They are design decisions backed by the Git measurements below, not implemented application behavior.

The run record maps shortened keys to full IDs. `<run>` and `<task>` start as 8-hex keys from those IDs and lengthen on collision. Task keys are unique within their run. The refs have these names:

- A task branch is `idp/<run>/task/<task>`.
- A task with two or more code predecessors also has `idp/<run>/join/<task>`.
- No ref is named `idp/<run>`, because it would collide with the nested branch names.
- A result ref is `refs/idp/<run>/result/<task>/<attempt>`.
- A salvage ref is `refs/idp/<run>/salvage/<task>/<attempt>`.

Worktrees live at `<project>/.worktrees/<run>/<task>`. iDevelop appends `/.worktrees/` to the repository's `.git/info/exclude`. It never edits the project's tracked `.gitignore`.

Worktrees share every ref outside `refs/worktree/`, including `refs/stash`. An agent can therefore move another task's refs or consume its stash. The event log supplies authoritative commit IDs, and the runner checks refs for changes it did not make.

The lifecycle follows these steps:

1. At run approval, iDevelop records the chosen run base. With a clean project, that is HEAD's commit. With uncommitted changes, iDevelop warns and offers HEAD or a hidden snapshot commit. A temporary index builds the snapshot without changing the person's branch or index. Preflight states that ignored files never reach a task.
2. At task start, a task without predecessors uses the run base. One code predecessor supplies its accepted result. Two or more supply the join. The runner uses `worktree add -b`, then `worktree lock --reason`. Each worktree initializes its own submodules. A retry or review fix uses the latest accepted result of the same task as its attempt base.
3. At attempt end, the runner commits the writer's changes with `add -A` and the trailers `IDP-Run`, `IDP-Task`, and `IDP-Attempt`. HEAD must still name the task branch. `merge-base --is-ancestor <base> <tip>` must accept the attempt's own base as an ancestor. Exit 1 rejects a reset below that base. A failed ownership check leaves the attempt Blocked as uncertain ownership. The runner records `refs/stash` and the `idp/<run>/` refs before and after each attempt. A ref change it did not make also blocks the attempt as uncertain ownership. It creates the result ref by compare-and-swap from the zero object ID. Successors consume commit IDs recorded in the run's event log, not a branch tip or a ref. Refs only keep the commits reachable and readable by Git tools.
4. After interruption, recovery waits until the run lock is free and the process tree is gone. It first inspects the task branch tip. A commit with matching `IDP-Run`, `IDP-Task`, and `IDP-Attempt` trailers becomes the interrupted attempt's result if its ancestry passes the attempt-base check. This covers a crash after commit but before result recording. If recovery cannot adopt a committed attempt, it uses the salvage path. A temporary index captures tracked and untracked changes in the salvage ref. Recovery removes the stale `index.lock`. It resets to the base only when the next attempt starts.
5. Retries and review fixes use the task's own worktree and branch. An accepted review fix marks every transitive consumer stale. A running consumer is left alone. A finished consumer stays stale until the person chooses what to do. iDevelop offers a rebase onto the new join when it applies cleanly. It does not rerun automatically. A stale downstream approval returns to waiting. Old result refs remain available.

The runner orders predecessor results by task ID and chains `merge-tree` over them. One `commit-tree` call creates a join with every predecessor result as a parent. The input record stores the join, its parents, and each predecessor's base and result.

The runner publishes a new join with `update-ref` by compare-and-swap from the zero object ID. A repeated publication adopts an existing clean join only when its parents match and its tree equals the `merge-tree` tree for those parents. Matching parents alone could accept a join that drops an input. Different parents block adoption. A later join replaces the old one by compare-and-swap from that old join. The join ref never moves while its integration worktree is live.

`merge-tree` exits 0 for a clean merge and 1 for a conflict. Any other exit is an error. A conflict leaves the successor Blocked with the conflicting paths in its state and the event log. No join ref is published until a resolution is accepted. Predecessor branches stay intact, and unrelated branches can continue. The conflict tree is reproducible, so the contract does not store it.

A person can resolve the conflict in the integration worktree on the join branch. An agent can also resolve there, but its resolution merges only after a person reviews it, as question 3 below records. The resolution uses one `commit-tree` call with every predecessor result as a parent, including for a conflict with three inputs. Parent matching alone does not authorize an agent's resolution. No agent edits another task's branch.

Cleanup starts only when the person asks and the run has no live attempt. The runner checks each worktree with `git status --porcelain --ignored`. It removes a worktree with empty status by `unlock`, then `worktree remove` without `--force`. A worktree with tracked or untracked changes stays. If only ignored files remain, the runner lists them and removes the worktree only after the person confirms, because `worktree remove` deletes ignored files.

A worktree with initialized submodules needs a status check of the outer worktree and every submodule before `--force`. This guard is inferred from the measured refusal and forced removal, rather than validated by the probe. Cleanup deletes a branch only when its tip is an ancestor of a kept ref. It uses `update-ref -d <ref> <expected>`, never `branch -d`, whose decision depends on HEAD or upstream. Result and salvage refs remain until the person deletes the run. A second cleanup pass changes nothing.

### The user's six decisions settle the probe's options

The user decided these questions on 2026-10-06:

1. Worktrees live under `<project>/.worktrees/`, at `<project>/.worktrees/<run>/<task>`, so the project's work stays together. iDevelop appends `/.worktrees/` to the repository's `.git/info/exclude` and never edits the tracked `.gitignore`. The user first accepted an iDevelop data folder outside the project, then replaced that answer. The data folder, a location beside the project, and `.idp/worktrees/` are rejected.
2. After a predecessor's review fix, iDevelop marks a finished successor stale and asks the person. It offers a rebase onto the new join when that applies cleanly. Automatic rebase and automatic rerun are rejected. A rerun would spend quota without the person's say.
3. An agent may resolve a fan-in conflict. Its resolution commit has every predecessor result as a parent. The join ref is published and the successor released only after a person approves that commit. Until then, the successor stays Blocked and names the pending review. No agent edits another task's branch. A person can still resolve in the integration worktree. The user rejected the recommended person-only rule. An agent's resolution never merges without a person's review.
4. With uncommitted changes, Run Workflow warns and offers HEAD or a hidden snapshot commit built through a temporary index. The snapshot leaves the person's branch and index untouched. Ignored files never reach a task. Refusing to run is rejected. So is either choice alone, because the warning lets the person pick the base.
5. Task branches stay visible under `idp/`. The user rejected hiding them until publication, so a person can inspect them with ordinary Git tools.
6. The first release supports projects with submodules. Each worktree initializes its own submodules. Cleanup checks the outer worktree and every submodule before any forced removal. Refusing such projects at preflight is rejected. iDevelop's own repository has one, `.pstack/upstream`.

Questions 2 and 4 also settle the review's two scope findings. The original lifecycle chose a dirty-project policy before the user did and assumed an automatic new successor attempt. The revised lifecycle follows the user's choices.

### Two review findings did not require contract changes

The coordinator dismissed the run-key collision finding because 8-hex keys lengthen on collision. It dismissed the garbage-collection race finding because Git's prune grace period covers objects between commit creation and the ref write.

### Shared columns fix the inspector's constant insets

The unselected inspector has four right edges. The measured insets are identical at 280 and 520 px in both themes. Selected pickers sit at 58 px. The cause is XAML constants, not DPI or binding.

The line numbers match `main` at `11a0b22`. Paths in the table are relative to `src/IDevelop.Desktop/`.

| Right inset | Controls | XAML constants on `main` |
| --- | --- | --- |
| 8 px | Header and filter menu | `Inspector/InspectorStyles.axaml` lines 8 and 46 set padding `12,12,8,8` and `12,0,8,8`. |
| 12 px | Section content and overview counts | `Inspector/InspectorView.axaml` line 133 sets `Margin="12,0,12,16"`. Its line 679 and `Blueprints/PaletteView.axaml` line 27 use `Layout="Full"`. These rows span `ColumnDefinitions="2*,3*,46"` in `Inspector/InspectorStyles.axaml` line 160. |
| 36 px | Filter box | A 24 px menu button and 4 px spacing add to the 8 px padding from `Inspector/InspectorStyles.axaml` line 46. |
| 38 px | Library Derive action | `Blueprints/PaletteView.axaml` line 29 sets `Auto,*,Auto,Auto,24`. The hidden Edit column still reserves 24 px. |
| 58 px | Selected pickers | `Inspector/InspectorStyles.axaml` line 160 reserves the trailing 46 px in `2*,3*,46`. The section inset adds 12 px. |

U1 uses one shared column definition for the header, the filter, and every row. Separate padding adjustments would retain the competing alignment rules.

### Layout A keeps inspector actions visible

The user chose inspector layout A, the aligned grid. The prototypes use one fake task, six sections, Fluent icons, and a 12 px outer inset. Each layout has exactly two right edges in both themes and at both measured widths.

| Layout | Right insets | Height at 440 px width | Height at 280 px width | Measured tradeoff |
| --- | --- | --- | --- | --- |
| A, aligned grid | Actions at 12 px and values at 64 px | 1,120 px | 1,329 px | Revert, info, and Place stay visible. Pickers stack below labels under 320 px. The layout costs 28 px of value width. |
| B, dense property sheet | 12 and 36 px, with counts on the left | 1,022 px | 1,118 px | At 280 px, values clip to "gpt-6.1-s" and Place moves behind More. |
| C, grouped settings | 12 and 36 px | 1,222 px | 1,279 px | Values sit far from their labels. |

Layout A beats B's lower height because its values fit and its actions stay visible at the narrow width. It beats C because labels stay close to their values.

### Content-sized cards preserve room for answers

The user chose cards sized to content and capped at 320 px. The prototype laid out a six-task diamond at a 158 px row pitch.

| Card state | Content-sized height | Fixed height |
| --- | --- | --- |
| Idle | 149 px | 312 px |
| Blocked | 186 px | 312 px |
| Running | 216 px | 312 px |
| Finished | 228 px | 312 px |
| Question | 311 px | 312 px |

Only the idle card fits the row pitch, so overlap between expanded cards is unavoidable. A 268 px fixed height clips the answer box. A running card grows 95 px when a question arrives. Content sizing avoids the unused space of a 312 px fixed card while leaving room for that answer.

The selected card draws whole on top. Other headers stay above other bodies. Raising the selected card alone can hide a neighbor completely. Keeping only headers above bodies cuts across the question being answered. The chosen policy preserves the selected interaction and the other task headers.

### Conversation uses the main area by default

The user chose conversation in the main area by default, with a dock option. The measured transcript heights favor the main area.

| Window size | Dock transcript height | Main-area transcript height | Canvas height with the dock |
| --- | --- | --- | --- |
| 1280 by 800 px | 243 px | 661 px | 434 px |
| 1024 by 700 px | 243 px | 561 px | 334 px |

At 1024 px, the dock header truncates the breadcrumb before the task name. A dock as the default loses transcript space and task context. It remains an option when the person wants the canvas visible.

### The redesign decisions now have user approval

On 2026-10-06 the user confirmed the "New tasks use the planner's agent" box, decision D8 in the [node system redesign record](2026-10-05-node-system-redesign.md#decisions). Its rejected alternatives stay rejected. Always falling back would override the rule that a new node takes its blueprint's default agent. Never falling back would leave every generated node reading "Choose an agent".

The user also approved giving blueprints their own icon and color. The file-format field arrives within W1's format and migration review. The user rejected a separate format change. W1 already needs that review, so one review covers both changes. Until W1 adds the field, a project or personal blueprint shows its base kind's tile with a library badge.

## What was measured and what was inferred

### The Git and UI probes measured bounded cases

The Git probe ran on Linux with Git 2.55.0 and the `files` ref backend. It isolated configuration with `GIT_CONFIG_NOSYSTEM=1` and a scratch `GIT_CONFIG_GLOBAL`. Pinned dates made object IDs reproducible. Scripted writers represented agents in a demo-shaped diamond. Plan precedes A, A feeds B and C, and B and C feed D. Cases covered disjoint files, separate lines in one file, and the same line in one file.

These measured facts support the contract:

- Strategy 1 produced joins whose parents were exactly B and C in all three cases. D's result had that join as its parent. Clean cases contained both edits and Plan's file.
- A conflict made `merge-tree` exit 1. Evidence included paths from `--name-only` and `-z`, stage 1, 2, and 3 blobs, and a `CONFLICT (content)` line. The conflict tree ID repeated, and B and C stayed intact.
- Killing B dirtied only B's worktree. Main, Plan, A, and C stayed clean. B's stale `.git/worktrees/b/index.lock` made `add` exit 128 while C and main still worked.
- A temporary-index salvage captured tracked and untracked changes. `cmp` returned 0 for the unchanged real index and files. The salvage survived `gc --prune=now`. `git stash create` omitted untracked files.
- A second worktree on B's checked-out branch failed with exit 128. B2 did not appear in D's original base or result. Rebase failed while D was dirty and succeeded after D committed. The probe's `refs/idp/results/r1/d/1` kept the old D result reachable through garbage collection.
- A repeated join creation through `update-ref` from zero failed with exit 128 and "reference already exists". Chained `merge-tree` and `commit-tree -p B -p C -p E` matched the octopus-merge tree. Changing merge argument order did not change that tree. The three-input conflict failed at the second step with exit 1 and no join.
- Uncommitted files and ignored `build/out.bin` in main did not reach a new worktree. `idp/r1` could not coexist with `idp/r1/task/b`. GUID names passed `check-ref-format`. Checking out a branch held by another worktree failed with exit 128. A reset behind the base made `merge-base --is-ancestor <base> <tip>` exit 1.
- `worktree remove` exited 0 for a clean worktree and 128 for a dirty, locked, or initialized-submodule worktree. `--force` removed the submodule worktree and its `modules/` copy. `branch -d` refused a branch unmerged to `main` but accepted one merged to its upstream with a warning. The cleanup probe retained dirty worktrees, unmerged branches, and salvage refs. Its second pass made no changes.
- `submodule update --init` cloned into `.git/worktrees/<n>/modules/`. Main's `.git/config` and `.git/modules` did not change. Two worktrees each made 50 concurrent commits with no failures under default settings or `gc.auto=1`.
- In a worktree, `rev-parse --git-path index` printed an absolute path. The earlier `.idp/worktrees/n` probe appeared as untracked in main and triggered an embedded-repository warning. Adding `worktrees/` to `.idp/.gitignore` removed both in that discarded layout.

The final nested-worktree probe used isolated Git configuration and `.worktrees/r1/b`. Before the exclude line, main reported `?? .worktrees/`, and `git add -A --dry-run` warned "adding embedded git repository". After appending `/.worktrees/` to `.git/info/exclude`, main's status was empty. `git add -A --dry-run` added nothing and exited 0. The nested worktree still reported its own `?? f.txt`.

The UI measurements came from the Release app on Linux under Xvfb. The reproduction used `samples/storage-change` plus five placed blueprints, at inspector widths of 280 and 520 px in both themes. Prototype bounds and screenshots supplied the layout, card, and conversation measurements above. They do not establish Windows behavior or interaction correctness.

### The remaining platform and implementation claims are inferred

- `GitTree.Snapshot` is expected to work unchanged from a worktree root because Git returned an absolute index path. The probe did not run that method there.
- Checking the outer worktree and every submodule before `--force` is an inferred cleanup guard. The probe measured only refusal and forced removal.
- The .NET SDK's default `**/.*/**` exclusion is expected to keep nested worktree sources out of a project at the repository root. The probe did not build that case.
- Nesting worktrees adds roughly 27 characters per path. That estimate matters on Windows without long-path support and remains unmeasured there.
- Moving the project folder is expected to require `git worktree repair`, as with other worktree locations. The probe did not test a move.
- The minimum Git version is believed to be 2.38 for `merge-tree --write-tree`. D0 did not verify that minimum.
- The inspector's constant-based cause is expected to hold on Windows. D0 did not observe Segoe UI at 125% or 150% scaling.

### The review corrected five evidence claims

1. `d_started_before_resolution=no` is a value the script writes, not a measurement. D waiting for resolution is a contract rule.
2. `join_second_candidate` equals `join_commit` because dates are pinned. New commit IDs on real reruns are inferred, not measured.
3. `bc_wall_ms` was 2,008 ms for two 2 s scripted sleeps in parallel and 4,019 to 4,020 ms under a lock. Those numbers measure the sleeps, not throughput. Strategy 1's parallelism is a structural property, not a measured speedup.
4. Strategy 2 skipped strategy 1's salvage and reset, and `review_unlocked_*` violated its own one-writer rule. Its lost line 10 came from a scripted writer that overwrites blindly. A real agent on a serialized branch sees the earlier edit and decides itself. `review_b2_in_original_d_result` was `no` in both strategies.
5. "No join ref on conflict" applies only until a resolution is accepted. The recorded `join_ref_already_at_resolution=yes` requires that qualification.

## Changed artifacts

This documentation change touches only these repository files:

- `docs/handoffs/2026-10-06-d0-design-validation.md` records the decisions, corrected evidence, and remaining gates.
- `docs/product-direction.md` records D0's state and the accepted direction.
- `docs/handoffs/2026-10-06-workspace-execution-plan.md` updates authority, slice status, and next actions.
- `docs/handoffs/2026-10-05-node-system-redesign.md` records the user's two decisions on its open issues.

The evidence sits in the D0 session's throwaway scratch folder outside the repository. It is not committed and may disappear. These paths are relative to that folder:

- `git/report.md`, `git/probe/run-all.sh`, and the other scripts under `git/probe/` hold the Git report and probe.
- `git/results/facts.tsv`, `git/facts-final.tsv`, and `git/results/*.log` hold the Git facts and command output.
- `git/nested-probe/` holds the final worktree-location experiment.
- `ui/report.md`, `ui/index.html`, and `ui/shots/` hold the UI report and the 50-shot comparison page.
- `ui/evidence/` and `ui/tools/` hold the bounds, page renders, and capture tools.
- `ui/wt/` holds the throwaway prototype worktree on `d0/ui-prototypes`.

## Commands and observed results

| Command or check | Observed result |
| --- | --- |
| Probe construction through Codex | GPT-6.1 Sol at high built the scripts in two sessions. The Git workstream review found one brief error and seven coverage gaps. The second session fixed them. The error based A on the run base instead of Plan's result, which omitted Plan's file from D. |
| `bash probe/run-all.sh` | About 26 seconds and 616 facts per run. Two runs outside Codex's sandbox matched Codex's run except `gcauto_packs_after`, which was 1 versus 2 depending on garbage-collection interleaving. |
| Independent `probe/run-all.sh` rerun | A fresh Opus session that did not write the probe reproduced all 616 facts except wall times and `gcauto_packs_after`. Its verdict was sound with fixes. The coordinator accepted the six contract revisions and five evidence corrections recorded above. |
| Nested-worktree status and `git add -A --dry-run` | Before the exclude line, main reported `?? .worktrees/` and an embedded-repository warning. After `/.worktrees/` entered `.git/info/exclude`, main was clean and the dry run added nothing with exit 0. The nested worktree retained `?? f.txt`. |
| UI Release build | The build reported 0 warnings. Tests and script checks did not run on the throwaway branch. |
| Inspector reproduction under Linux Xvfb | `samples/storage-change` plus five placed blueprints showed the same four unselected right edges at 280 and 520 px in both themes. Selected pickers had a 58 px right inset. |
| `ui/index.html` and its 50 shots | The page compared the three inspector layouts, expanded-card sizes and overlap, and dock and main-area conversation. Evidence includes page renders at 375 and 1280 px. |
| Fairness fixes before the UI comparison | The UI workstream attached cards before measurement to retain button styles. It removed a leaked width that laid out dark 280 px shots at 320 px. It fixed status text overflow and a dock breadcrumb that overlapped the attempt selector. |
| Source and fact checks against `main` at `11a0b22` | The XAML constants and port locations matched the report. The facts file had 616 rows. The recorded conflict-wait flag, resolution ref, wall times, and absence of B2 in D's original result matched the report. The corrections above limit what those values prove. |
| `node scripts/check-handoffs.mjs` and `git diff --check` on this change | The handoff check reported seven records, each linked from `docs/context.md` or `docs/product-direction.md`, and exited 0. `git diff --check` reported nothing and exited 0. |

## Open issues

1. The client capability probe has not run. Its real-client runs wait for the user's explicit approval, so C1 is gated on it. The conversation prototype used a synthetic transcript, hand-built Markdown, and a placeholder capability line. It proves no client capability.
2. Neither workstream ran in a real Windows window. The Git workstream still needs file-handle, `autocrlf`, and Job Object kill checks. U1 needs Segoe UI at 125% and 150% scaling in both themes.
3. The UI prototypes did not test drag, focus, or typing. U1, C1, and N1 need interaction evidence against their implemented controls.
4. The Markdown renderer is not chosen. C1 must select and validate it against the plan's rendering, history, and content-safety requirements.
5. Four of 16 inspector rows use the neutral fallback icon. Model, Reasoning, Report, and Attempt have candidates Brain Circuit, Gauge, Document Text, and History. U1 must check those candidates against the pinned Fluent commit before adding them.
6. N1 must anchor ports to the header row. `src/IDevelop.Desktop/Canvas/CanvasTemplates.axaml` lines 86 and 96 center them vertically on `main`, so a taller card moves every wire. The disclosure chevron costs about 32 px of title width.
7. Nested worktrees add about 27 characters per path by inference. E2 must test Windows path length without assuming long-path support.
8. D0 did not establish behavior for rename, delete, binary, or LFS conflicts, network submodules, real agents, the minimum Git version, large repositories, or concurrency limits. E2 and E3 need evidence before claiming support for those cases.

## Next action

The user decides whether to approve the client probe's real-client runs. The client workstream then records the capability table before C1 starts.

U1 can start from layout A and the shared column definition. W1 can start and includes the blueprint icon and color field in its format and migration review. E2 builds on this contract after E1. E3 still needs its writer-isolation and join gate verified on the demo-shaped diamond in the implementation.

Delete the local `d0/ui-prototypes` branch and its worktree once U1 no longer needs them. The branch is never pushed or merged.
