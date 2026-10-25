# A review round that repeats an earlier round waits for the person

When a review loop stops making progress, the run waits for the person on the review node. The owner was asked "When a review loop stops making progress, what should happen?" on 2026-10-25 and chose "Pause for me — After a round repeats an earlier round, the run waits for you on the review node and shows both sides." This changes their rule of 2026-10-04 that the loop goes on until both agents agree, without the person. In the owner's project, a Review and a Merge node went 111 rounds in 54 minutes with the same three findings and the same disputes, and the only answer was a notice to the reviewer that the round repeated round 1.

## How it works

In Implement → Review, the reviewer keeps finding 1 in round 1 and round 2, and the implementer answers it fixed both times, so round 2 repeats the positions of round 1:

- **The run waits on the review.** Once the implementer's fix of round 2 ends, the review starts no reviewer turn. Its card says Waiting for you, and its inspector says that round 2 repeats round 1, beside its Findings, which list each finding with both sides' latest words.
- **Guidance resumes it.** The person's message to the review is guidance, as it is at any time in the loop. Guidance sent after the repeating fix ended starts the reviewer's next turn, and it reaches both agents in their next message. Cancel and Stop Workflow end the review as before. Nothing new was added to go on.
- **The wait holds nothing.** No client and no lock wait. The review's step reads the wait from its log, so it survives a restart, and a later round that repeats an earlier one waits again.

Source: the owner's decision of 2026-10-25.
