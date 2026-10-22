# A run's planner waits on its proposal, and the person accepts it into the run

A planner that runs inside a workflow run and ends a turn with a proposal waits for the person, whatever its conversation mode, so nothing after it starts before the person decides. Its card shows Proposing, its inspector's Proposal section and ghost cards show the proposal as Generate's review does, with each new task's agent that the person can change, and Accept amends the run against the revision the person saw. The open workflow then takes the amendment as one unsaved edit that Undo can reverse. The owner decided this on 2026-10-12 in issue [#84](https://github.com/Mano-Liaoyan/iDevelop/issues/84), taking Q1 B, Q2 A, Q3 A, and Q4 B there, because Plan and Architect default to May ask and handed on their result before anyone looked.

## How it works

For a Plan node "Plan" in a run, whose turn proposes "Export API" after it, and a task "Ship" after Plan:

- **The planner waits.** Plan's attempt waits with a pending proposal, so Ship stays pending and the run's waiting pill counts Plan. A Chat planner waits after every turn, as it always did. The attempt's request says so, so a run's log written before this decision folds as it did then. A planner that runs on its own, outside a run, ends its turn as before, and so does one whose client reported no session, since it has nothing to wait in.
- **The review is Generate's.** Plan's inspector reads the proposal from its newest attempt in the run, with the run's blueprints. Each row and ghost card shows the agent "Export API" takes, the planner's usable choice or the person's own, and the box "Fall back to the planner's agent" covers a task without one ([ADR 0028](0028-a-run-s-planner-and-its-amendment-choose-agents-and-so-does-a-fill.md)).
- **Accept amends the run, then lets it go on.** Accept goes through `WorkflowRunCoordinator.Amend` with the run's revision when the proposal was read ([ADR 0012](0012-an-amendment-adds-and-fills-only-tasks-whose-start-is-not-planned.md)). Once the run records it, the workflow follows through `AmendmentProjection` as one edit on the Undo stack, so its title shows unsaved changes, and Undo takes nothing back from the run. Then Plan is marked done, and Ship and Export API start. A run that another amendment moved since the proposal showed refuses it, and the review then takes the newer revision for the person to accept again.
- **A Chat planner keeps talking.** Its Accept amends the run and leaves the conversation open, and Accept and Finish amends it and marks the planner done.
- **Dismiss lets the run go on without the proposal.** It hides the proposal and marks a waiting planner done. A Chat planner's Dismiss only hides it. A planner marked done without an amendment of its proposal keeps it closed, also in a window opened later.
- **After Stop, or once the run has ended, the proposal stays.** Accept then reads "Accept for Next Run" and edits only the workflow, as a proposal outside a run does, and a note says why. A window that only reads the run, because another window controls it, shows the same, since only the controlling window amends.

## Considered options

- **No wait**, so most fills show as started and only new tasks can still be added. The owner chose the wait, as the product direction says every planner proposes graph edits that the person approves.
- **Holding only the tasks a proposal fills**, so the planner's other successors go on.
- **A list opened from the run's bar**, or a card in the run's conversation, in place of the inspector's Proposal section.
- **Applying and saving the amendment at once**, outside Undo.
- **Closing an open proposal with the run**, or keeping it read-only once the run has ended.

## Consequences

- Hiding the settled run, with the run bar's dismiss, hides its planners' proposals too, since the proposal is read from the run the canvas shows.
- A document that no longer holds a revision of the run's amendment chain, because the person edited it during the run, takes nothing when the person accepts. The run goes on as amended, and a notice says so.
- The review checks the chosen tasks against the open workflow, as Generate's does, while the run applies them to what it executes, so a workflow edited during the run can show a problem that the run would not have.

Source: the owner's answers on issue #84 on 2026-10-12, and the pull request that closes it.
