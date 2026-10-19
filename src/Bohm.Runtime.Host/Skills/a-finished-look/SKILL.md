---
name: a-finished-look
description: What a finished-looking application tends to have at first sight and in use — layout, an empty first screen, waiting, mistakes. Load it when the person asks for a polished or tidy look, or when you want a checklist to look your application over with.
---

# A finished look

One way an application looks finished and is obvious the first time. Suggestions, not rules: where the request or your own judgment says otherwise, follow that.

- One clear size hierarchy for titles and text, generous spacing, one consistent style for cards, buttons and colours. Numbers and states read at a glance: amounts aligned right with their unit, totals and counts where the eye lands first.
- When there is nothing yet, a line or two says what to do first. While it waits (an AI answer, reading a file, saving), it shows what it is doing. A deletion can be undone right away.
- Nothing is shown as wrong before the person has done anything: a required field is marked when a save is tried and it is empty, not on first sight.
- It fits the window it is in, narrow or wide.
- Everyone can read and use it. Text stands out from what is behind it (a contrast ratio of at least 4.5:1, 3:1 for large text) — pale grey on white, white on a light colour and placeholder-grey hints are where it most often falls short. Pick colours that pass without measuring: on white, muted text no lighter than #4b5563 (slate #475569); behind white text, the deep shade of a hue rather than its mid one (#1d4ed8 not #3b82f6, #b91c1c not #cd5c5c, #3f6212 not #6f814e); on a dark background, text near white — never a dark shade on a dark one. Every field and drop-down has a name a screen reader can say (a `<label>`, or `aria-label` when there is no room for one), and a link inside a sentence is told apart by more than its colour.
