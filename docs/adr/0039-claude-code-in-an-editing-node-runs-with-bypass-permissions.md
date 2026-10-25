# Claude Code in an editing node runs with bypassPermissions

Claude Code in an editing node, one that is neither read-only nor a reviewer, runs with `--permission-mode bypassPermissions`, so it runs any tool without asking. Read-only nodes and reviewers keep `plan`, and the other clients keep their modes. The owner decided this on 2026-10-25. Asked "What may a Claude Code agent in an editing node do without asking?", they chose "Allow all tools — Run it with bypassPermissions inside its own worktree, so it can run git, dotnet and anything else." Under `acceptEdits` in `-p` mode, nobody could answer a permission prompt, so every Bash call was denied, and in the owner's project a Merge node could not run `git merge`, `dotnet build`, or `dotnet test` for 111 review rounds.

This changes the [conversation record](../handoffs/2026-10-07-conversation.md)'s rule that permission requests are declined, for Claude Code editing nodes. The node's limitations text, in the conversation view and the attempt's record, says that every tool runs without asking for permission.

## Consequences

- Claude Code's own documentation names `--permission-mode bypassPermissions` as enough on its own, equivalent to `--dangerously-skip-permissions`. It refuses the mode under root or sudo outside a recognized sandbox, under `--restricted`, and when managed settings set `permissions.disableBypassPermissionsMode` to `"disable"`. Deny rules still apply, and the few actions that still prompt in this mode are declined as before.
- A standalone task run works in the project folder itself, not in a worktree, so its editing Claude Code agent runs every tool there.
