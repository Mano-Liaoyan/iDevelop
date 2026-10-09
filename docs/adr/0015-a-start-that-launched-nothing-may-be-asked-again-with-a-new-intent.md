# A start that recorded and launched nothing may be asked again with a new intent

`ProjectRuns.StartTurn` keeps each start's intent in memory under its operation, and refuses a different intent for the same operation with `OperationConflict`. A start that ended without a launch, because it was refused or faulted, no longer holds its intent there, so a retry of the same operation may ask with a different intent. The journal stays the guard: whatever the earlier start recorded under the operation, its receipts still refuse a changed intent. So only a start that recorded and launched nothing may be retried with a new intent, while a start that is in flight, or that launched, still refuses one.

## Why

A turn whose start failed before anything was recorded should take what arrived meanwhile. A continuation whose start was refused takes the text queued since, and a fix round refused before its reservation takes the guidance written since. A reserved fix keeps the guidance count it recorded, so its prompt is rebuilt the same.

## Considered options

- A cache of each review step's first intent, which E3c.2 first used. It held the first prompt for as long as the window ran, even when nothing was recorded, so a failed start ignored guidance written meanwhile, and it covered review steps only.

Source: pull request [#79](https://github.com/Mano-Liaoyan/iDevelop/pull/79) (E3c.2), its Decision 4, the coordinator's answer to its open question, and its review round 1.
