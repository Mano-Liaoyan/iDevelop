# Avalonia stack selection

## Task

Record the user's decision to use C# and Avalonia throughout iDevelop. The user is familiar with this stack and explicitly declined further framework comparisons.

## Investigation closeout checklist

- [x] Route through the **how** skill. For motivation questions, also route through the **why** skill. Skip further investigation and delegation because the user has selected the stack and declined comparisons.
- [x] Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only investigation`.
- [x] Produce the `how`-shaped output (Overview / Key Concepts / How It Works / Where Things Live / Gotchas), or a recommendation with a tradeoffs table if the request is a decision between alternatives. Skip another comparison. Record the accepted decision instead.
- [x] Apply the **unslop** skill to the reply.
- [x] Update current context and product direction.
- [x] Check the edited documents for stale decisions, whitespace, and local links.

This closes the preceding investigation with documentation edits. No application code or new dependency research is part of this update.

## Decision and reason

C# and .NET are the implementation stack for iDevelop's application code, including the local runner and optional synchronization service. Avalonia is the desktop UI framework. BAndysc's `NodifyAvalonia` is the selected starting component for the node canvas. The user's familiarity with C# and Avalonia is the reason to commit to this stack now.

Further Rust, C++, Flutter, and other framework comparisons are removed from the active plan. Performance, compatibility, input, accessibility, and packaging checks apply to the chosen Avalonia implementation. Those checks no longer gate the framework decision.

The product still requires standalone solo operation, optional self-hosted team synchronization, local agent execution, and Windows, macOS, and Linux builds. The permissive dependency preference remains in force. Exact .NET and package versions, storage, and collaboration protocol details remain to be settled during implementation.

## Changed artifacts

- `docs/context.md` records the selected stack and the next implementation step.
- `docs/product-direction.md` replaces the active comparison with the accepted Avalonia direction.
- This handoff records why the earlier comparison plan is superseded. Earlier handoffs remain historical records.

## Commands and observed results

- Read `.pstack/compatibility.md`, `.pstack/models.json`, the Poteto entrypoint, the current context, and the preceding language-comparison handoff.
- Used `rg` to inspect selection and comparison wording in both active product documents. Framework selection is settled, and earlier comparison work is linked only as history.
- A PowerShell scan passed for trailing whitespace and local Markdown link targets across all three changed documents, including untracked files.
- `git diff --check` reported no findings. `git status --short` showed only current and earlier research documentation changes.

No application build or performance test ran. No application code, dependency installation, or setup configuration changed. Setup checks were not rerun because this task only changed documentation.

## Next action

Implement the Avalonia application shell and editable NodifyAvalonia canvas in C#. Keep the task and workflow model independent of UI controls. Pin a compatible dependency set, then validate this implementation against the recorded product acceptance cases.
