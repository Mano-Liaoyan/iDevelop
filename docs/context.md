# iDevelop project context

## Purpose

Build a graphical interface for coordinating multiple coding agents, including Codex, Pi, Claude Code, and Antigravity CLI. Shared project records should let agents continue each other's work. The product should eventually support generating a usable multi-model configuration from a natural-language request and manually editing it afterward.

## Current scope

The repository contains the PStack development environment and initial product research. No application has been implemented. The user selected C# and .NET for iDevelop's application code, Avalonia for the desktop UI, and the proposed NodifyAvalonia canvas option. Storage, protocol, scheduling details, and exact dependency versions remain open.

## Confirmed product direction

- The primary interface is a Miro or Unreal Blueprint-style node canvas, not a kanban board. AI generates a draft workflow that users can edit, including task relationships, agent, model, and supported reasoning settings.
- Users review generated workflows before execution.
- Both standalone solo mode and real-time team collaboration are first-release requirements. Solo mode needs no server installation or connection.
- Team mode uses an optional self-hosted synchronization service. All coding agents run on members' local machines. The service shares workflow state and does not execute agents.
- Multiple projects, workflow hierarchy navigation, dependency blocking, GitHub issues and pull-request review, and portable Markdown session records belong to the intended product.
- Provider integrations should use subscriptions or coding plans where supported, with API access also available. Provider policy and protocol compatibility must be assessed separately.
- Windows, macOS, and Linux are required. The user prefers a responsive modern UI without a JavaScript or TypeScript application stack.
- Use C# and Avalonia. The user is familiar with both and explicitly declined further framework comparisons. Use BAndysc's `NodifyAvalonia` as the starting node-editor component. Keep the local runner and optional synchronization service in C# and .NET.
- Prefer permissive dependencies such as MIT, Apache-2.0, and BSD for commercial distribution and company use without mandatory framework fees. Preserve the option of a proprietary product. No product license has been chosen.

The [product direction](product-direction.md) records the selected stack, proposed runtime design, and acceptance cases. Framework selection is settled. Runtime design details remain provisional.

## Working decisions

- The project's working language is English, as defined in `AGENTS.md`.
- PStack is installed within this project from the official `cursor/plugins/pstack` source.
- The official upstream is a Git submodule at `.pstack/upstream`. Sparse checkout excludes the other plugin directories. Upgrades are explicit, reviewed changes to its recorded commit.
- All clients follow the same PStack workflow and shared handoff convention.
- Development model assignments are explicit in `.pstack/models.json`. Until the app prototype works, Claude Opus 5.5 does all frontend and backend implementation, reviews, judgment, and exploration. Each review runs in a fresh Opus session that did not write the change. After the prototype works, the user will choose each part's owner inside the app. A pending Opus route blocks work without substitution.
- Development invocations use explicit effort below max. Opus runs at xhigh. The unselected Astra and Gemini entries keep xhigh and high. The ceiling overrides upstream skill defaults. Claude Opus 5.5 passed a subscription-authenticated request with explicit xhigh through Claude Code 2.1.288. Gemini CLI was uninstalled after it stopped serving individual Google accounts. Antigravity CLI 1.2.16 (`agy`) replaces it. A signed-in request with `--model gemini-3.8-flash --effort high` reached Gemini 3.8 Flash (High). Project configuration does not change an existing chat's app-level setting.
- Other projects retain their existing skills and settings. Isolation changes belong to this checkout.
- Concurrent code writers use separate worktrees. Durable history belongs in task-specific handoff records, not copied private chat transcripts.

## Next product step

Implement the Avalonia application shell and editable NodifyAvalonia canvas in C#. Validate the selected implementation against the product direction's acceptance cases without comparing other frameworks. Include solo use without a server, two-client collaboration, one supported local subscription integration, and one API integration as the first working slice develops. Pin compatible dependency versions and check their licenses before packaging.
