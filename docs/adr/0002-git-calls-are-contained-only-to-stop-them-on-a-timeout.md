# Git calls are contained only to stop them on a timeout

Every Git call iDevelop makes starts contained: in its own session and process group on Linux and macOS, through the launcher of [ADR 0001](0001-unix-workflow-turns-lead-their-own-session.md), and in a Job Object on Windows. Only a timeout uses that containment. The group gets `SIGKILL`, or the job is terminated, and then the process tree is stopped, at once and without a grace. A call that ends on its own leaves its hooks' background work running, as before. The E2 record had rejected binding `merge-tree` to a job or group because "it protects nothing". E3a.4 carried in the need to stop what a timed-out call's hooks started, which tree stopping misses once a hook's parent has exited.

The Windows job of a Git call is made without kill-on-close, unlike a client's job. If iDevelop exits, crashes, or is killed during a call, Git and its hooks finish instead of dying mid-write with `index.lock` held.

On Linux and macOS, `git` must be found on PATH. Otherwise the call returns "git was not found on PATH." and starts nothing. The coordinator decided this because `Process.Start` also searches beside the app and in the current folder, which may be the repository, so it could run a `git` the repository supplies.

## Considered options

- Kill-on-close for Git jobs, as client jobs have. An exit during a call would kill Git mid-write and could leave `index.lock` behind.
- Stopping only the process tree on a timeout. It misses a hook's child whose parent has exited.
- Keeping `Process.Start`'s search outside PATH on Unix. It could run a program from the project folder.

Source: pull request [#70](https://github.com/Mano-Liaoyan/iDevelop/pull/70) (E3a.4), its Decision 6, and the coordinator's decision against a PATH fallback.
