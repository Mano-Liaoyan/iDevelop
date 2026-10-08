# Unix workflow turns lead their own session and process group

On Linux and macOS, a workflow turn's client root starts through iDevelop's own `posix_spawn` launcher with `POSIX_SPAWN_SETSID`, so it leads a new session and a process group whose ID is its own before the command runs, and cleanup and Stop signal that group. [Design v5](https://github.com/Mano-Liaoyan/iDevelop/issues/54#issuecomment-6065458422) named `POSIX_SPAWN_SETPGROUP` with group 0. The coordinator added the new session so that a client has no controlling terminal: when iDevelop runs in a terminal, a client that reads the terminal fails at once, as under a desktop launch, instead of stopping on `SIGTTIN` or `SIGTTOU`.

## The launch

`POSIX_SPAWN_SETPGROUP` with group 0 is the fallback when `posix_spawn` refuses the session flag, and the turn's containment detail says why the child shares iDevelop's session. The launcher keeps what `Process.Start` gives a child: the working folder, arguments, environment, three redirected pipes, the signal mask and ignored signals, the exit code (128 plus the signal for a signaled root), and the failure message. Standalone runs keep `Process.Start` and their Cancel, Stop and send, and leave behavior.

## Group IDs stay reserved while iDevelop may signal them

The exited root is not reaped (`waitid` with `WNOWAIT`), so its process ID, and with it the group ID, cannot be reused until cleanup or disposal reaps it. After the reap, every signal follows a check that the group still has members, and a group once seen empty is never signaled again. Disposal signals nothing. As design v5 requires, iDevelop never signals a group ID read from a journal after a restart.

## Cleanup and Stop

Cleanup sends `SIGTERM` to the group and waits until the group is empty or a two-second grace has passed on the turn's `TimeProvider`. If members remain, it sends `SIGKILL` and waits at most two more seconds of real time, so a frozen test clock cannot hang it. Cancelling the turn ends the grace early. Cleanup is `Completed` only when it saw the group empty, and it records each signal and why it failed. Stop stops the root's process tree first, while the root is still unreaped, and then sends `SIGKILL` to the group, because once the root has exited its children belong to init.

## A missing launcher never refuses a turn

When the launcher is unavailable, for example on glibc older than 2.29 or macOS older than 10.15, the turn starts through `Process.Start` and records containment `None` with the cause. The user decided on 2026-10-07 that containment serves cleanup only and that no platform waits for it, so a missing group and a failed cleanup are diagnostics, never gates on publication or scheduling.

## Considered options

- `POSIX_SPAWN_SETPGROUP` alone, as design v5 wrote it. A child in iDevelop's session stops in the background when it reads the terminal iDevelop runs in.
- `setpgid` from the parent after launch. The child would run before its group exists, and design v5 says that is not the contract.
- Refusing workflow turns without a launcher. That contradicts the user's decision that every platform gets workflow execution.

## Consequences

- Ctrl+C and a closing terminal no longer reach workflow clients or the Git calls of [ADR 0002](0002-git-calls-are-contained-only-to-stop-them-on-a-timeout.md).
- A process that calls `setsid` or `setpgid` leaves the group and escapes cleanup. The design accepts that residual risk.
- After a group's last member exits, the system would have to reuse its ID within one 20 ms poll for a signal to reach a stranger.
- Nothing on macOS has run. Its `POSIX_SPAWN_SETSID` value, 0x400, comes from XNU's `bsd/sys/spawn.h`, and a stopped root there is untested.

Source: pull request [#70](https://github.com/Mano-Liaoyan/iDevelop/pull/70) (E3a.4), its Decisions 1, 2, 4, 5, and 7, the last of them the coordinator's.
