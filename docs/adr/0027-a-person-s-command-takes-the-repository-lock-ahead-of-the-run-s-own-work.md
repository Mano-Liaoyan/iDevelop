# A person's command takes the repository's lock ahead of the run's own work

A person's command, such as Mark done, Cancel, Preserve, Restore, the rebase preview and approval, or Continue fix and Retry fix, takes the repository's mutation lock ahead of the run's own scheduled work. This amends [ADR 0018](0018-every-ready-task-of-a-run-starts-at-once.md), whose waiters took the lock in no fixed order. Since every ready task of a run starts at once, the run's steps could take the lock again right after letting it go, and a person's command could lose every try of its one-second wait and show "The run's records are busy." Issue [#105](https://github.com/Mano-Liaoyan/iDevelop/issues/105) asked that the command get the lock ahead of the run's work or within a bounded wait. Pull request [#114](https://github.com/Mano-Liaoyan/iDevelop/pull/114) settled it on 2026-10-12.

## How it works

- **A waiting command holds `person.lock`.** While a person's command waits for `mutation.lock`, it holds an exclusive lock on `person.lock`, next to it in `<git-common-dir>/idevelop/`. It lets go of that file once it has the lock or stops waiting.
- **The run's work tries the lock under a shared hold.** A scheduled step tries `mutation.lock` only while it holds a shared lock on `person.lock`, and it keeps that hold through the try. So it never takes the lock while a person's command waits, and the command gets the lock once the current holder lets it go.
- **This works across windows.** Both are operating-system file locks, `flock` on Unix and share modes on Windows. So scheduled work in another iDevelop window on the same repository lets the command go first too. Neither file is ever deleted.
- **The lock order gains `person.lock` just before the repository's mutation lock.** A step only tries `person.lock` and never waits for it, and it holds the file only while it waits for or tries the mutation lock, so ADR 0018's lock order still cannot deadlock.
- **One choice sets both the wait and the place in line.** A materializer works either for the run's scheduled work or for a person's command. That one choice sets how long it waits, 30 seconds or one second as in ADR 0018, and whether it goes ahead.
- **What stays the same.** A command still reports a busy repository after one second when a single step holds the lock for longer than that. Two commands, or two scheduled steps, take the lock in no fixed order. If a scheduled step is checking `person.lock` at the moment a command starts to wait, the command's claim fails once, and the command claims the file on its next 20 ms try.

## Considered options

- A first-in, first-out lock. The issue allowed it, but across processes it needs a queue or ticket counter shared through files. ADR 0018 already separates the run's scheduled work from a person's commands, so priority needs only one more lock file.
- A longer wait, or more tries in the app, for a person's command. Under sustained lock work, the run's steps still win every try.

Source: issue [#105](https://github.com/Mano-Liaoyan/iDevelop/issues/105) and pull request [#114](https://github.com/Mano-Liaoyan/iDevelop/pull/114), its Decisions 1 to 4.
