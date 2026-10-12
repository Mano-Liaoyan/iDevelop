# A finding has a severity and a location, and an Approval reads a handed-on review as its findings

The reviewer's verdict contract asks for a severity and a location on each finding. The Review node's inspector and an Approval's inspector show both. An Approval shows each review it hands on as that review's verdict, shown as a status, then every finding the review raised and how each was settled. The review's recorded report, with its raw JSON, sits behind a disclosure. The owner decided this on 2026-10-12 in issue [#101](https://github.com/Mano-Liaoyan/iDevelop/issues/101). The reviewer gives a severity (e.g. "high") and a location (e.g. "src/Calc.cs:12"). A verdict without them stays readable and is never refused. The Approval shows every finding the review raised, like the Review node's inspector, and how each was settled: fixed, or withdrawn with its reason. Before this, the Approval showed a review's result as its raw verdict block, so the person approving had to read JSON.

## How it works

- **The contract asks for both.** `ReviewWork.VerdictContract` shows `"severity": "high", "location": "src/Calc.cs:12"` in its example finding. It says: "Give each finding a severity, "high", "medium", or "low", and its location, the file and line it concerns, such as src/Calc.cs:12." The three severities are the High, Medium, and Low scale that the project's earlier review records use. They also fit the owner's example.
- **Reading never fails on them.** `ReviewLedger.ReadVerdict` keeps a finding's severity in lower case and its location as written, trimmed. A severity or a location that is missing, empty, or not text is left out, with no repair turn. A verdict written before the contract asked for them therefore reads as before. A severity outside the three is kept as the reviewer wrote it, in lower case. A finding in the ledger keeps the severity and the location the reviewer last gave it, including after it is settled, and a finding listed again without them keeps the earlier ones.
- **The inspector shows them on each finding's card.** The Review node's Findings section and an Approval share one card for each finding. The card has the finding's heading with its severity in a chip at the card's right edge, then its location in the code font, then the reviewer's latest words, then how the finding was or would be settled: the change that would settle it, the implementer's answer, and "Withdrawn:" with the reviewer's reason. A withdrawn finding keeps what it raised, and the reason shows beside it. The chip keeps to 96 px, so an odd long severity wraps inside it. A finding without a severity or a location shows neither. In the Review node, a settled finding fades beside the open ones. In an Approval every finding is settled, so none fades.
- **An Approval reads a review's whole record.** A review hands on only once it agrees with no finding left standing. That means its final verdict always lists no findings. The Approval folds the review attempt's own log into the findings ledger. For a result carried from an earlier run, it reads that run's journal and log ([ADR 0022](0022-a-node-s-run-carries-the-current-results-of-earlier-runs.md)). The Approval's Hands on row shows, for each review input:
  - the input's heading, which every input starts with at the row's edge
  - the verdict as a status, such as "Approved in round 2"
  - every finding the review raised, with its state, such as Resolved or Withdrawn, and the implementer's answer, or "The reviewer raised no findings."
  - a collapsed Raw data disclosure that holds the review's recorded report: the reviewer's last message with its verdict block, the JSON indented.

  Any other input shows its heading, then its report as Markdown in a card. So does a review whose log cannot be read.
- **What approving records does not change.** The approval's report and what its dependents get are each input's report under its task's title, as [ADR 0007](0007-a-gate-approval-is-a-human-origin-result.md) records it. The new view is only how the inspector shows those inputs.

## Considered options

- Show only the handed-on verdict. It lists no findings once a review agrees, so the person approving would not see what the review raised. The owner chose every finding the review raised.
- Refuse a verdict without a severity or a location and ask the reviewer again. The owner asked to keep verdicts without them readable and never fail on them.
- Drop a severity outside "high", "medium", and "low". Keeping the reviewer's word loses nothing and costs no repair turn.

## Consequences

- A fix round's prompt still lists each finding with its text and the change that would settle it, without the severity and the location. The owner asked for them in the two inspectors only.
- The expanded card and the hover peek show a review's round summary as before, without its findings.
- An older build reads a verdict that has a severity and a location and ignores both.

Source: issue [#101](https://github.com/Mano-Liaoyan/iDevelop/issues/101), decided by the owner on 2026-10-12, and its pull request.
