# Git calls are contained only to stop them on a timeout

Every Git call iDevelop makes starts contained where the platform allows: in its own session and process group through the launcher of [ADR 0001](0001-unix-workflow-turns-lead-their-own-session.md) on Linux and macOS, and in a Job Object on Windows. Only a timeout uses that containment, at once and without a grace: on Linux and macOS the call's process tree is stopped and then the group gets `SIGKILL`, and on Windows the job is terminated and then the tree is stopped. A call that ends on its own leaves its hooks' background work running, as before.

## Why

The E2 record had rejected binding `merge-tree` to a job or group, because an orphaned `merge-tree` writes only objects and binding would protect nothing. E3a.4 carried in the need to stop what a timed-out call's hooks started, which tree stopping misses once a hook's parent has exited.

## Details

- Without the Unix launcher, Git starts through `Process.Start` with no group, and a timeout stops only the process tree.
- The Windows job of a Git call is made without kill-on-close, unlike a client's job. If iDevelop exits, crashes, or is killed during a call, Git and its hooks finish instead of dying mid-write with `index.lock` held.
- On Linux and macOS, `git` must be found on PATH. Otherwise the call returns "git was not found on PATH." and starts nothing, because `Process.Start` would also search beside the app and in the current folder, which may be the repository.

## Considered options

- Kill-on-close for Git jobs, as client jobs have. An exit during a call would kill Git mid-write with `index.lock` held.
- Stopping only the process tree on a timeout. It misses a hook's child whose parent has exited.

Source: pull request [#70](https://github.com/Mano-Liaoyan/iDevelop/pull/70) (E3a.4), its Decision 6.
