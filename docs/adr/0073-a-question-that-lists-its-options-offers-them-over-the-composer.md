# A question that lists its options offers them over the composer

An agent that asks in its reply can write its options as a list. The conversation shows each option as a quick-reply button above the composer. A click or a digit from 1 to 9 puts the option's text in the composer. The person presses Send. This extends the numbered choices of the attention card to clients that ask in their text, and it changes neither the engine nor the log format.

## How it works

- **Source.** The view reads the words that ask: the wait's own reason, or the agent's last reply. It reads them only while the attention card is a Reply card.
- **A list of options.** A Markdown bullet list or numbered list with two to nine items, and with no check boxes. It is the message's last list, and at most one paragraph follows it. An indented line belongs to the item above it, and a list in a code fence does not count.
- **A question beside the list.** The paragraph before the list ends in a question mark, or ends in a colon and names a choice ("Please choose one:"). Otherwise the paragraph after the list ends in a question mark and names a choice ("Which do you prefer?"). "I changed these files:" and "Shall I go on?" offer nothing.
- **Fill, not send.** An option replaces an empty draft or another option. After the person's own words, it goes on a new line, so a click never deletes what the person wrote. A digit works only while the composer is empty.
- **Shown once.** While the message asks, the list shows only as the buttons. The bubble keeps the question's words, unless an option has detail lines that a button cannot show.
- **Display only.** The parse decides what the buttons show. It changes no state, no card, and no log line.

## Considered options

- **Infer options from prose ("the longer one, or each provider's own").** A guess can offer words that the agent did not offer. Only an explicit list counts.
- **Send on click.** The person keeps a last look at the words, and can add to them.
- **More than nine options.** A digit has no key past 9, and a list that long reads as content.

## Consequences

- A question in prose looks as it did, with no buttons.
- Most messages that wait for a reply have no list, and these rows take no height.
