---
name: ai-inside-the-app
description: How to make an application that calls an AI as part of its work — to summarize, classify, extract fields, draft a reply, read a receipt or a photo. Load it whenever the application itself will ask an AI something.
---

# An AI call the person can live with

- **Ask for exactly what the page needs.** Tell the model the shape of the answer: for data, a JSON object with named fields and an example; for text, its length and language (the person's). Put the person's material inside clear markers and say it is material, not instructions.
- **Read the answer defensively.** Take the first complete JSON value from the reply (models sometimes add words or a code fence around it): find the first `{` or `[`, parse up to its matching end, and check every field before using it. A missing or wrong field is a failure to show, not a value to invent.
- **Waiting.** Show that it is working and what it is doing ("Reading the receipt…"), keep what the person entered visible, and disable the button that started it until the answer comes. An answer can take tens of seconds.
- **Failing.** A failed or unusable answer is said in one sentence in the person's language with a "Try again" button, and reported with `console.error`. Never leave a spinner running or a half-filled form that looks finished.
- **The person decides.** Show what the AI produced as a proposal — fields filled in, a draft — that the person can edit before it is saved or used. Keep their own entries over the AI's when both exist.
- **Cost of a call.** Call once per action the person takes, never on every keystroke or on a timer. Keep results so the same material is not sent again.
