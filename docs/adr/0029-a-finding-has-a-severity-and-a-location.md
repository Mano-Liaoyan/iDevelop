# A finding has a severity and a location

The Review blueprint's output format is a verdict, approve or reject, with a list of findings, and each finding has a severity and a location. The Review node's inspector and an Approval's inspector show both. An Approval shows each review among its inputs as that review's verdict and its findings, and keeps the review's report, with its raw verdict block, behind a disclosure. The owner decided this on 2026-10-12 in issue [#101](https://github.com/Mano-Liaoyan/iDevelop/issues/101). The reviewer gives a severity, such as "high", and a location, such as "src/Calc.cs:12". A verdict without them stays readable and is never refused. Review is a blueprint run by `CliAgent`, so this format is the blueprint's output format, not a code type ([ADR 0042](0042-execution-v2-a-node-is-an-agent-and-the-engine-only-routes-results.md)).

## How it works

- **The format asks for both.** The blueprint's example finding shows `"severity": "high", "location": "src/Calc.cs:12"`, and its template says: "Give each finding a severity, "high", "medium", or "low", and its location, the file and line it concerns, such as src/Calc.cs:12."
- **Reading never fails on them.** A finding's severity is kept in lower case and its location as written, trimmed. A severity or a location that is missing, empty, or not text is left out, and nothing asks the reviewer again. A severity outside the three is kept as the reviewer wrote it, in lower case.
- **A reject's findings are the feedback.** A reject sends its findings along the feedback edge to the review's predecessor ([ADR 0041](0041-feedback-goes-to-the-direct-predecessor.md)), each with its text, severity, location, and the change that would settle it.
- **The inspector shows them on each finding's card.** The Review node's Findings section and an Approval share one card for each finding: its heading with its severity in a chip at the card's right edge, then its location in the code font, then the reviewer's words. The chip keeps to 96 px, so an odd long severity wraps inside it. A finding without a severity or a location shows neither.
- **An Approval reads a review's result.** For each review among its inputs, the Approval's Hands on row shows the input's heading, the verdict as a status, such as "Approved in round 2", the findings of the review's rounds, or "The reviewer raised no findings.", and a collapsed Raw data disclosure that holds the review's report. Any other input shows its heading, then its report as Markdown in a card.

## Considered options

- Refuse a verdict without a severity or a location and ask the reviewer again. The owner asked to keep verdicts without them readable and never fail on them.
- Drop a severity outside "high", "medium", and "low". Keeping the reviewer's word loses nothing.

Source: issue [#101](https://github.com/Mano-Liaoyan/iDevelop/issues/101), decided by the owner on 2026-10-12, and map issue [#179](https://github.com/Mano-Liaoyan/iDevelop/issues/179).
