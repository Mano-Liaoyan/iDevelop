# Development environment

The agents that develop iDevelop use [Matt Pocock's skills](https://github.com/mattpocock/skills), installed as the `mattpocock-skills` Claude Code plugin. The project's issue tracker is GitHub Issues. The project replaced its earlier PStack setup with these skills on 2026-10-08.

## Install the skills

Install Node.js 22 or later, Git, the .NET SDK, the GitHub CLI (`gh`), and Claude Code. Clone the repository as [Build and run the application](../README.md#build-and-run-the-application) shows. `.claude/settings.json` enables the plugin for this project, so Claude Code offers to install it when you open the repository. To install it yourself:

```bash
claude plugin install mattpocock-skills@claude-plugins-official --scope project
```

The plugin updates itself. Other clients install the same skills with the commands in the [skills README](https://github.com/mattpocock/skills#installation-30-second-setup). Codex, for example:

```bash
codex plugin marketplace add mattpocock/skills
codex plugin add mattpocock-skills@mattpocock
```

`/setup-matt-pocock-skills` has already run for this repository. Its answers live in `docs/agents/`: [`issue-tracker.md`](agents/issue-tracker.md) (GitHub Issues through `gh`), [`triage-labels.md`](agents/triage-labels.md) (the five default labels, created on GitHub), and [`domain.md`](agents/domain.md) (one `GLOSSARY.md` and `docs/adr/` at the root). Edit those files to change a choice. Rerun the setup skill only to switch issue trackers.

## Follow the workflow

- Plan large work as a map issue with child tickets (`/wayfinder`), or turn a settled plan into tickets (`/to-tickets`). The workflow execution program is map issue [#54](https://github.com/Mano-Liaoyan/iDevelop/issues/54).
- Build a ticket with `/implement`, which works test-first at the agreed seams and ends with `/code-review`. Use `/tdd` for a single behavior and `/diagnosing-bugs` for a defect.
- Settle vocabulary and design decisions with `/grill-with-docs` or `/domain-modeling`. They write `GLOSSARY.md` and architecture decision records under `docs/adr/`, creating them when the first term or decision is resolved.
- Write pull request descriptions with `/pr`. Hand a long session to a fresh agent with `/handoff`.
- `/ask-matt` routes a request to the right skill.

[`AGENTS.md`](../AGENTS.md) is the shared entry point. `CLAUDE.md` imports it and points the skills at `docs/agents/`. [`docs/context.md`](context.md) and [`docs/product-direction.md`](product-direction.md) hold the settled project facts. The decision records under `docs/handoffs/` predate the ADRs. `node scripts/check-handoffs.mjs` keeps every record linked from those two files.

## Verify in the real window

On Windows, the project skill [`.claude/skills/verify-idevelop`](../.claude/skills/verify-idevelop/SKILL.md) launches the built app, drives it through UI Automation, and captures screenshots and the files the app writes. `scripts/check-real-window.ps1` runs the scripted subset.

## References

- [mattpocock/skills README](https://github.com/mattpocock/skills) and its [setup skill](https://github.com/mattpocock/skills/tree/main/skills/engineering/setup-matt-pocock-skills).
- [Claude Code plugins](https://code.claude.com/docs/en/plugins) and [skills](https://code.claude.com/docs/en/skills).
