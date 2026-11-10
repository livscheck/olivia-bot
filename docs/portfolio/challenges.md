<!-- CHALLENGES: 1–3 real problems. This is where you show how you think.
     Use the same shape for each one. Failed attempts count. -->

## Making it sound like me

**Problem:** The first replies sounded like a cheerful assistant ("easy peasy 😎"), which is not how I text.

**What I tried:** Describing my style in general terms ("casual, friendly") wasn't enough. The model filled the gaps with its own idea of casual.

**Fix:** I wrote the persona from my real messages: all lowercase, dropped apostrophes, "LOL" and "fr????", short bursts, almost no emoji, plus a handful of real texts as examples. Then I added specific bans for things I never do, like saying "hey", starting every question with "wait", or giving advice nobody asked for. It also asks way more than it tells, because that's how I actually talk to friends.

## Catching what it gets wrong

**Problem:** One-off test messages looked fine, but longer chats showed problems: it made up details about my life, offered to "run over to your desk", and once leaked its own self-correction ("wait, that breaks my persona rule, let me fix it") into the chat.

**What I tried:** Testing by hand was too slow to cover enough situations.

**Fix:** I asked Claude to play out full conversations between the bot and a simulated friend (a coworker with a bug, someone sharing big news, someone asking if it's a bot...) and write them to a file I could read through. Each round showed something to tighten: rules against inventing facts or being somewhere in person, joking dodges for personal questions ("a very important project called answering my phone"), and parsing that keeps only the bot's final JSON array, so stray notes never show.
