# A continued session reads as one conversation

Each message still starts a client process that resumes the task's own client session by its id, and no client process stays up between messages. The conversation view reads the attempts that go on with one session as one conversation, with no New attempt marker between them. The owner decided this on 2026-10-09 in issue [#92](https://github.com/Mano-Liaoyan/iDevelop/issues/92): keep resuming the same session for each message, with no long-lived idle client processes, and show a continued session as one conversation, with no "New attempt" divider when the session is the same. The [client sessions research](../research/2026-10-09-client-sessions-and-skills.md#probe-results-2026-10-09) holds the probe that found Claude Code, Codex, and Antigravity CLI resuming the same session in a new process, and Pi doing so in the session's own folder.

## How it works

- **A message resumes the session in a new process, as before.** A message to a task that is not running starts a new attempt whose first turn resumes the latest attempt's session with the message alone. A reply to a waiting attempt is the next turn of the same attempt. In a run, Continue fix and a review's fix rounds start a new attempt that resumes the earlier attempt's session. The launches do not change.
- **An attempt knows whether it went on with the session.** For an attempt that resumes another's session, the attempt's record says so once its first turn's client reports the session it runs: the same id means it resumed it. A client that reports another session, or a first turn that ends without reporting one, means the attempt started over. The reducer reads this from the events every log already has, so the log format does not change and an older log folds the same way.
- **The view reads one conversation.** A conversation is a chain of attempts, each resuming the one before. An attempt that resumed the earlier attempt's session goes on with its conversation, and its New attempt marker goes. When it runs another model, reasoning level, or conversation mode than the earlier attempt last ran, a Settings changed marker with its settings takes the marker's place. An attempt that started over keeps its New attempt marker, and so does the first attempt of a chain. While the first turn starts, the attempt reads as going on. This holds for a task's own conversation and for a run's.
- **The attempts stay apart.** Each attempt keeps its own log, its place in the run journal, its lock, and its entry in the attempt picker. Picking an earlier attempt shows the conversation up to it.

## Considered options

- One live client process per conversation, taking each message on its open input. The owner decided against long-lived idle client processes, which would hold a process, the task's lock, and the run's client slot while a node waits.
- A message to a task that is not running goes on as the next turn of the latest attempt instead of a new attempt. It would change the settled attempt's record, its lock, and the run journal's rules for a settled attempt, while the owner's decision names what the view shows.
- Drop the marker in the desktop alone. Both conversation views and the attempt picker read the history that Core projects, so the rule lives where the chain is read.

## Consequences

- A client that answers a resume with another session, or loses it, still shows a New attempt marker, so the person sees that the conversation started over.
- Pi finds a session only in the folder where it ran. A task's own attempts all run in the project folder, and a run's attempts of a task all run in that task's checkout, so Pi resumes its session. When the project's folder moved or was renamed, Pi opens an empty session under the same id. It reports that id, so the view reads the attempt as going on. No Pi option resumes the same session from another folder: `--session-dir` does not find it, `--session <file>` resumes it but works in the session's old folder, and `--fork` copies the history into a new session with a new id. What a Pi message does after the project moved waits for the owner in [#92](https://github.com/Mano-Liaoyan/iDevelop/issues/92).

Source: issue [#92](https://github.com/Mano-Liaoyan/iDevelop/issues/92), decided by the owner on 2026-10-09, and its pull request.
