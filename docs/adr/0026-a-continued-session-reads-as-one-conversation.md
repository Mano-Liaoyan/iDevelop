# A continued session reads as one conversation

Each message starts a client process that resumes the node's own client session by its id, and no client process stays up between messages. The conversation view reads the turns that go on with one session as one conversation. Pi cannot continue a conversation after the project folder moved, so iDevelop refuses that message with a note. The owner decided this on 2026-10-09 in issue [#92](https://github.com/Mano-Liaoyan/iDevelop/issues/92): keep resuming the same session for each message, with no long-lived idle client processes, and show a continued session as one conversation. On 2026-10-11 the owner decided the Pi case in the same issue: refuse the message, with a note that Pi can't continue this conversation after the folder moved and a new conversation is needed. The [client sessions research](../research/2026-10-09-client-sessions-and-skills.md#probe-results-2026-10-09) holds the probe that found Claude Code, Codex, and Antigravity CLI resuming the same session in a new process, and Pi doing so in the session's own folder.

## How it works

- **A message resumes the session in a new process.** A person's message and a feedback edge's findings or note are the next turn of the node's session, which resumes it with the message alone ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)). A Retry and an upstream change start a new attempt in a fresh session.
- **The view reads one conversation.** The turns of one session read as one conversation. A New session marker shows where a fresh session began.
- **A fresh session records its folder.** The node's log records the folder a fresh session runs in, which is where a client that keeps sessions per folder finds it again.
- **Pi refuses a conversation whose folder moved.** Pi finds a session only in the folder where it ran. When a message would resume a Pi session from another folder than the session's, because the project folder moved or was renamed since the session started, the engine refuses it with the note "Pi can't continue this conversation after the project folder moved, so it needs a new conversation." Nothing launches. Open in terminal is off for such a conversation, because Pi's own command would open an empty session there too. A Retry starts a fresh session, and a feedback edge to such a node starts one with the findings or the note. Folders compare by their full paths, without case on Windows and macOS, whose file systems usually ignore it, and with case on Linux. Claude Code, Codex, and Antigravity CLI find a session by its id from any folder, so they resume it from anywhere.

## Considered options

- One live client process per conversation, taking each message on its open input. The owner decided against long-lived idle client processes, which would hold a process while a node waits.
- For Pi after a move, `--fork <id>`, which copies the history into a new session with a new id. The owner ruled out a fork. `--session-dir` does not find a session that ran in another folder, and `--session <file>` resumes the session but works in its old folder.

Source: issue [#92](https://github.com/Mano-Liaoyan/iDevelop/issues/92), decided by the owner on 2026-10-09 and 2026-10-11, and its pull request.
