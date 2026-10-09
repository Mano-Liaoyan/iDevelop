# The Desktop uses Core's internal run API

`IDevelop.Core` grants `InternalsVisibleTo` to `IDevelop.Desktop` and its tests, so the app calls `OpenRun`, the preflight, the workflow start, the run coordinator, and `ProjectRuns.OpenConversation(coordinator, task)` as Core declares them, instead of through a public facade. E3c.1's interface notes expected this. E3g.1 added to Core only `ProjectRuns.ActiveRunOf(WorkflowId)` and the coordinator's read-only `GateReport(GateId)`, and the run types stay invisible outside Core, the Desktop, their tests, and `IDevelop.RunRacer`.

Source: pull request [#80](https://github.com/Mano-Liaoyan/iDevelop/pull/80) (E3g.1), its Decision 1, and pull request [#76](https://github.com/Mano-Liaoyan/iDevelop/pull/76) (E3c.1), its interface notes.
