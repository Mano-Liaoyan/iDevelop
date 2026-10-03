# Implementation workflow explanation

## Task

Explain the next work and the project's Poteto execution workflow after the user selected C# and Avalonia. This turn supplies an implementation sequence and process explanation. It does not start application development or an automated pull-request program.

## Investigation checklist

- [x] Route through the **how** skill. For motivation questions, also route through the **why** skill. Used the simple explainer path for the workflow question. No separate historical rationale investigation is needed.
- [x] Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only investigation`.
- [x] Produce the `how`-shaped output (Overview / Key Concepts / How It Works / Where Things Live / Gotchas), or a recommendation with a tradeoffs table if the request is a decision between alternatives. Explained the next deliverable, execution process, and observable milestones.
- [x] Apply the **unslop** skill to the reply.

## Settled constraints

C# and .NET, Avalonia, and NodifyAvalonia remain selected. The user explicitly declined comparisons with other UI frameworks. Solo use requires no team server. Team collaboration belongs in the first release, with an optional self-hosted synchronization service and local agent execution.

The repository still contains setup and research documents. No runnable application exists. The immediate implementation task needs a bounded design and a working slice with observable behavior.

## Workflow

Start with a bounded feature contract and C# type sketch for opening a project, editing and saving a task graph, and reviewing its execution version. Keep Task, Run, and Attempt separate. Define shared execution ownership before adding concurrent agents.

Each feature follows this process:

1. Run How over the affected requirements and code.
2. Run Architect with at least two distinct C# designs for contracts and ownership. Synthesize the design within the selected stack.
3. Identify blocking work, independent work, shared state, and the smallest safe decomposition. Keep coupled code with one owner. Isolate concurrent writers in separate worktrees and branches.
4. Delegate implementation with named files, contracts, and observable acceptance conditions. The coordinator integrates and verifies the result.
5. Run meaningful behavior tests, drive the actual application, obtain independent review, and resolve findings. Keep delivery in small verifiable units.
6. Record the handoff and update context with settled facts. PR and release work use their corresponding playbooks when that work begins.

The recommended milestones are:

| Milestone | Observable completion condition |
| --- | --- |
| Editable local canvas | Open a project, create and connect tasks, edit properties, save, and reopen the graph without a team service. |
| Shared graph review | Two clients edit through the optional service. Conflicts, reconnects, and version-specific approval have defined behavior. |
| Complete execution loop | Generate and review a draft, run dependencies locally, preserve ownership, demonstrate a supported subscription route and an API route, export Markdown records, and attach GitHub review to the actual code revision. Deliver this loop in smaller verified features. |
| Release readiness | Complete the recorded interaction, recovery, performance, accessibility, and platform checks. Add provider adapters as their supported access and runtime behavior are verified. |

Platform builds and interaction checks start with the first applicable feature. The release milestone closes the remaining checks. This sequence does not remove team collaboration, project management, or requested provider coverage from the intended product.

Model the Domain puts task and execution types before UI behavior. Sequence Work into Verifiable Units makes each milestone observable. Prove It Works requires evidence from the actual artifact and distinguishes a build result from live UI verification.

## Delegation

A fresh read-only How explainer, `workflow_explainer`, inspected the project instructions and returned the workflow. The coordinator checked its account against the Feature and Architect playbooks. No delegate edited files. No implementation or architectural design candidates were launched in this explanatory turn.

## Changed artifacts

This task owns this handoff record. No application code or dependencies are changed.

## Commands and observed results

Read the project adapter, model configuration, Poteto entrypoint, Feature and Investigation playbooks, How and Architect instructions, current context, and the Avalonia selection record. The multi-phase-plan playbook was inspected but not selected because the request asks for the workflow, not an executable pull-request program.

- `dotnet --list-sdks` reported SDK `10.0.203` installed. This does not select the application's target framework or verify package compatibility.
- `rg --files` found no application `.csproj`, `.sln`, `.slnx`, or `global.json` outside the excluded upstream directory.
- The handoff whitespace and completed-checklist scan passed. `git diff --check` exited 0 with no findings.

No application build, native UI test, provider authentication, or performance benchmark ran.

## Next action

Define the first feature's C# contracts and acceptance scenario, then implement it under the Feature playbook. Keep internal design exploration within the selected C# and Avalonia stack.
