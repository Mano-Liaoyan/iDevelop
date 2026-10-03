# iDevelop project context

## Purpose

Build a graphical interface for coordinating multiple coding agents, including Codex, Pi, Claude Code, and Gemini CLI. Shared project records should let agents continue each other's work. The product should eventually support generating a usable multi-model configuration from a natural-language request and manually editing it afterward.

## Current scope

Only the repository and PStack development environment are being established. No GUI framework, application language, storage layer, protocol, or scheduling architecture has been chosen.

## Working decisions

- PStack is installed within this project from the official `cursor/plugins/pstack` source.
- All clients follow the same PStack workflow and shared handoff convention.
- Model selection favors quality and assigns explicit roles where the current host supports them. Configuration stays editable.
- Other projects retain their existing skills and settings. Isolation changes belong to this checkout.
- Concurrent code writers use separate worktrees. Durable history belongs in task-specific handoff records, not copied private chat transcripts.

## Next product step

Clarify the first user workflow and its observable acceptance criteria before selecting a technical stack.
