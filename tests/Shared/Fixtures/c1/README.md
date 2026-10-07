# C1 protocol excerpts

Static excerpts from the D0 raw evidence in `.git/d0-evidence/clients/results/raw` of the source checkout. Line numbers are one-based physical raw-log positions. Only fields used by the adapters remain. Project paths become `/project`; usage, signatures, installation/environment identifiers, account details and catalogs are omitted. The original phase 3 fixtures are unchanged.

| Fixture | Raw log | Lines |
| --- | --- | --- |
| claude-stream.jsonl | claude-stream.jsonl | 6, 15–18, 46 |
| claude-question.jsonl | claude-question.jsonl | 56 |
| claude-permission.jsonl | claude-permission.jsonl | 104 |
| claude-cancel.jsonl | claude-stale-after-stop.jsonl | 46 |
| claude-stop.jsonl | claude-stop.jsonl | 8, 20–21, 32–33, 38 |
| codex-stream.jsonl | codex-app-question.jsonl | 15, 37–43, 47 |
| codex-approval.jsonl | codex-app-approval.jsonl | 42 |
| pi-stream.jsonl | pi-stream.jsonl | 15–18, 41 |
| agy-stream.jsonl | agy-stream.jsonl | 4–12 |

D0 did not record an ExitPlanMode denial. Its test uses a synthetic request with the verified can_use_tool envelope. Numeric command approval `0` comes from the recorded Codex stream. Protocol initialization tests use the local schema's exact field names with synthetic request identifiers.
