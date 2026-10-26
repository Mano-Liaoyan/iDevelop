# Development environment

The agents that develop iDevelop use [Matt Pocock's skills](https://github.com/mattpocock/skills), installed as the `mattpocock-skills` Claude Code plugin. The project's issue tracker is GitHub Issues.

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

- Plan large work as a map issue with child tickets (`/wayfinder`), or turn a settled plan into tickets (`/to-tickets`). Execution v2 is map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
- Build a ticket with `/implement`, which works test-first at the agreed seams and ends with `/code-review`. Use `/tdd` for a single behavior and `/diagnosing-bugs` for a defect.
- Settle vocabulary and design decisions with `/grill-with-docs` or `/domain-modeling`. They write `GLOSSARY.md` and architecture decision records under `docs/adr/`, creating them when the first term or decision is resolved.
- Write pull request descriptions with `/pr`. Hand a long session to a fresh agent with `/handoff`.
- `/ask-matt` routes a request to the right skill.

[`AGENTS.md`](../AGENTS.md) is the shared entry point. `CLAUDE.md` imports it and points the skills at `docs/agents/`. [`docs/context.md`](context.md) and [`docs/product-direction.md`](product-direction.md) hold the settled project facts. The decision records under `docs/handoffs/` predate the ADRs. `node scripts/check-handoffs.mjs` keeps every record linked from those two files.

## Writing skills

Two project skills set how agents write. [`ste`](../.agents/skills/ste/SKILL.md) applies to all Chinese text, replies to the owner included. [`asd-ste100`](../.agents/skills/asd-ste100/SKILL.md) applies to all other English text. The files are copies of [dualface/ste-zh](https://github.com/dualface/ste-zh) at commit `27d57e4d6609ce88e14dd15cbae355223a83b246` and [danyuchn/asd-ste100-skill](https://github.com/danyuchn/asd-ste100-skill) at commit `8da6b6340aa22faf46fd07db8efbdea010cd38d9`. Each folder keeps its MIT `LICENSE`.

The skills live in `.agents/skills/`, which Codex and Pi read. `.claude/skills/<name>` (Claude Code) and `.agent/skills/<name>` (Antigravity) are relative symbolic links to the same folders. On Windows, Git creates the links only when Developer Mode is on and `core.symlinks` is `true`:

```bash
git config --global core.symlinks true
```

To update a skill, copy the upstream files over its folder and record the new commit here.

## Verify in the real window

On Windows, the project skill [`.claude/skills/verify-idevelop`](../.claude/skills/verify-idevelop/SKILL.md) launches the built app, drives it through UI Automation, and captures screenshots and the files the app writes. `scripts/check-real-window.ps1` runs the scripted subset.

## References

- [mattpocock/skills README](https://github.com/mattpocock/skills) and its [setup skill](https://github.com/mattpocock/skills/tree/main/skills/engineering/setup-matt-pocock-skills).
- [Claude Code plugins](https://code.claude.com/docs/en/plugins) and [skills](https://code.claude.com/docs/en/skills).
