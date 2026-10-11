# A node's skill choice is exactly its skills, and each client gets it its own way

A node's agent can use a choice of skills. The node's skill picker lists the skills that the node's client loads on this machine, and the person ticks the ones the agent may use. Claude Code and Pi are limited to exactly the chosen skills. Codex turns off the skills that were not chosen. Antigravity CLI's picker is off, with the note "Antigravity CLI uses all of its installed skills; it can't limit them per task." The owner decided this on 2026-10-09 in issue [#93](https://github.com/Mano-Liaoyan/iDevelop/issues/93). The same day the owner settled what a node starts with, what counts as installed, and what happens to a chosen skill that is no longer installed. The [research note](../research/2026-10-09-client-sessions-and-skills.md#skills-by-client) records how each client finds and limits skills.

## How it works

- **No choice means every skill.** A node starts with no choice. Its agent then uses every skill its client loads, including skills installed later, and the picker shows every listed skill ticked. The first untick keeps exactly the skills still ticked. Unticking every skill means no skills. The Skills row's revert arrow clears the choice, so the node uses every skill again. Choosing another client clears the choice, because each client names and finds its skills its own way. Save as Blueprint keeps the choice, as it keeps the model.
- **The choice is part of the agent.** A task's `execution` in the workflow file, and a blueprint's default, gain an optional `skills` array of the client's own names. It is written sorted, and only when set, so a file without a choice keeps every byte and the format stays `idevelop.workflow/3`. An older build refuses a file that uses it, as it refuses a workflow's `name`. A run's revision and an attempt log name the skills only when set too, so every earlier revision keeps its id.
- **The client lists its own skills, without a model turn.** The client directory asks the client, the way it asks for models, but in the project folder, so the listing holds the person's own skills, the plugins enabled for the user or by the project's settings, the project's skills, and the client's built-in skills where its listing can tell them apart. A listing is kept for each client and folder until the client's next refresh. The first request after a refresh lists the skills again and keeps the last listing until the new one is in, so a start during a refresh, such as a run's next task, still has it.
  - Claude Code: `claude -p --input-format stream-json --output-format stream-json --verbose` with an `initialize` control request and then a `reload_skills` request. The `reload_skills` answer lists the session's skills alone, its bundled skills among them, which is the set the `initialize` allow-list chooses from. A Claude Code that cannot answer `reload_skills` falls back to the commands of the `initialize` answer. Those mark Claude Code's own rows, its built-in commands, such as `/compact`, and its bundled skills alike, so the fallback leaves them out, and the open picker says so in one line: "Claude Code doesn't list its built-in skills, so a choice leaves them out."
  - Codex: `codex app-server`, which iDevelop initializes before it sends `skills/list` with the project as its folder. The list holds every scope, including Codex's bundled system skills. A skill the person's configuration turned off is left out, because Codex does not load it.
  - Pi: `pi --mode rpc --no-session` with `get_commands`. The listing keeps the entries whose source is a skill, by the name after `skill:`, with the file each one loads from.
  - Antigravity CLI: none, because it has no per-run control.
- **Only what needs the listing asks for it.** The picker asks when it opens and while the node has a choice, whose missing skills it names. A start asks only for a node with a choice. Each turn of a run uses the project folder's listing, the one the person chose from.
- **Every turn passes the choice.** A client's resume does not keep it, so each turn's launch carries it:
  - Claude Code gets the chosen names as the `skills` allow-list of the `initialize` request that iDevelop already sends.
  - Pi gets `--no-skills` and `--skill <file>` for each chosen skill.
  - Codex gets `skills.config` in the `config` of `thread/start` or `thread/resume`, with `enabled = false` for each listed skill that the node did not choose.
  - Antigravity CLI gets nothing.
- **A chosen skill that is no longer installed is skipped.** The turn runs with the chosen skills the client still lists. The Skills row names the missing ones, for example "gone is not installed, so the task runs without it.", and the attempt's activity says "Claude Code runs without gone, which is not installed." The picker keeps a missing skill ticked as "gone (not installed)", so the person can see it and untick it. Pi gets only the files that exist.
- **A start with a choice waits for its listing.** While the client lists its skills, the start says "iDevelop is still listing the skills Claude Code loads, which this task's choice needs." When the listing failed, it says why.

## Considered options

- **Reading the skill folders directly.** It would miss plugins enabled by settings and the bundled skills, and copy each client's own discovery rules into iDevelop, where each client's next release could change them.
- **Codex's `-c skills.config=[...]` on the command line.** It is the same session overrides as the thread's `config`, but its TOML array cannot pass an npm `codex.cmd` shim on Windows, so iDevelop would refuse to start the task. The thread's `config` is JSON on stdin.
- **Refusing a start whose chosen skill is not installed,** as a model that the machine does not offer refuses it. The owner chose to skip the skill and run.
- **Claude Code's `initialize` commands alone.** They mark the built-in commands and the bundled skills alike, so the picker could not offer a bundled skill, and any choice would drop every one of them. The owner asked for the built-in skills wherever a client lists them without a model turn, which `reload_skills` does. The `initialize` commands stay only as the fallback.
- **Listing Claude Code's own `initialize` rows anyway.** They mix built-in commands, which the allow-list does not govern, with its bundled skills.

## Consequences

- A skill installed after the person made a choice is not used until they tick it.
- No real client ran for this decision. The Claude Code listing rests on the request and answer types that Claude Code 2.1.296 ships. A real-client probe, which needs the owner's approval, still has to show that each of these works as described:
  - Claude Code, driven directly, answers `initialize` and `reload_skills` without a turn, and its `skills` allow-list holds on every resumed turn and hides the bundled skills that were not chosen.
  - Codex applies `skills.config` from a thread's `config` on both `thread/start` and `thread/resume`.
  - Pi's `--no-skills --skill` holds on a resumed session.
- A listing starts the client in the project folder, so whatever the client does on starting there may run for a listing too.
- On Windows, a Pi skill whose file path holds a space cannot pass `pi.cmd`, so the start says that cmd.exe could misread it.

Source: issue [#93](https://github.com/Mano-Liaoyan/iDevelop/issues/93) and the owner's decisions of 2026-10-09.
