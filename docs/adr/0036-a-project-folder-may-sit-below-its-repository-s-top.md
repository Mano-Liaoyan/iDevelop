# A project folder may sit below its repository's top

A workflow run needs the project folder to be in a Git work tree, not at its top, so a service in a large repository can be its own project. The owner decided this on 2026-10-20, after the preflight refused `C:/dev/fsharp`'s subfolder: "as long as this repository is in the Git project, then it will be fine."

## How it works

For a project at `services/api` in a repository at `C:/dev/mono`:

- **Git runs at the top.** Each repository-wide command runs from `C:/dev/mono`, and a run's base, refs, merges, and each turn's commit cover the whole repository, as they would for a project at the top.
- **Worktrees stay in the project.** A node's worktree is a whole checkout of the repository at `services/api/.worktrees/<run>/<node>`, so everything a run adds is under the folder the person opened. `info/exclude` ignores `/services/api/.worktrees/` and the project's `.idp/inputs/` and `.idp/outbox/`.
- **The agent works in the project's folder of its worktree.** Its working folder is `services/api/.worktrees/<run>/<node>/services/api`, where its inputs and outbox are, so it sees the same folder the person opened, and the rest of the repository around it.
- **The preflight names the project's changes.** Its list of changes the agents will not see covers the project's folder. A folder in no Git work tree is still refused, and the journal keeps the problem's old name, `NotRepositoryRoot`.

## Considered options

- **Worktrees at the repository's top**, `C:/dev/mono/.worktrees/`, would add folders outside the opened project, which two projects of one repository would share.

Source: the owner's request of 2026-10-20.
