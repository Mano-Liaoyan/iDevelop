# Project name casing

## Task

Change the local project folder from `IDevelop` to `iDevelop` and use `iDevelop` as its Codex project name.

## Decisions and reasons

Preserve the existing project identity and chat assignments. Only the folder casing and the corresponding saved project name and root path need changing. The GitHub repository already uses `iDevelop`.

Windows rejected the direct folder rename because a running process holds the directory open. Prepare a temporary one-time helper that waits for the desktop app to close, then renames the folder and updates the saved project entry. It backs up the saved state, verifies that unrelated entries are unchanged, and verifies the resulting values. It does not terminate applications.

The PStack Prove It Works principle requires distinguishing a validated helper from a completed rename. Completion remains pending until the directory and saved project name actually read `iDevelop`.

## Workflow and observed results

- Read the PStack entrypoint, compatibility rules, model settings, project context, and relevant bootstrap handoff.
- Frame and design. The task has two observable names and one existing project identity. Use direct inspection and a narrowly scoped helper. Skip delegation and architecture work for this mechanical local change.
- Run the loop. `Rename-Item` from the parent directory failed with a file-in-use error. The failed command made no changes.
- Inspect the installed app-server schema. A read of the mapped project identifier returned project not found, so no app-server mutation was attempted.
- Prepare and validate. `node --check`, the helper's read-only `--check`, and the PowerShell parser all passed. The scope check permits changes only to the target project's name, root paths, and update timestamp.
- Keep the audit trail. This record captures the decisions. Temporary helper files and their execution log stay outside the repository because they contain machine-specific configuration paths.
- Verify and hand back. The hidden helper process was observed running. Its log read `Waiting for Codex to close. No changes made yet.`

## Changed artifacts

This handoff is the only repository artifact added by this task. The temporary helper consists of `idevelop-case-rename.ps1`, `idevelop-case-rename.cjs`, and `idevelop-case-rename.log` in the Windows user temporary directory. It waits up to 30 minutes for the desktop app to close, then up to five minutes for the folder lock to clear. It records failures rather than terminating other processes.

Existing uncommitted product research and context edits were preserved. No setup implementation changed, so PStack setup checks and the skill isolation audit were not rerun.

## Open issue and next action

Fully quit Codex while the helper is waiting. Check the temporary log for `COMPLETE` before reopening. A successful helper run verifies the on-disk folder spelling and saved project entry. After reopening, inspect the visible project name or call `list_projects` to confirm the application loaded the new name. If the helper times out, inspect its log and rerun it after releasing the remaining directory lock.
