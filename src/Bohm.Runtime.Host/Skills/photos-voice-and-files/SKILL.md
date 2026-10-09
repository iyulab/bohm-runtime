---
name: photos-voice-and-files
description: How to make an application that takes in photos, recorded voice or files the person picks — receipts, inspection photos, spoken notes, a spreadsheet or document to read. Load it when the person mentions taking or attaching a photo, recording or dictating, uploading, or a file to import.
---

# Taking things in

- **Picking a file.** A large drop zone that also opens the picker when clicked (`<input type="file">` with the right `accept`, `multiple` when several make sense). Highlight it while something is dragged over it. Show what was taken in — a thumbnail, a file name and size — before it is used, with a way to remove it.
- **Photos.** Keep them small enough to store: draw the image on a canvas at most about 1280 px on its longest side and keep it as a JPEG data URL (quality about 0.8). Show thumbnails in lists and the full picture when one is tapped.
- **Room to keep.** Everything is kept in localStorage, which holds a few megabytes. When a save fails for lack of room (a `QuotaExceededError`), say so plainly, keep what was entered on screen, and report it with `console.error`. Offer to export or remove old items rather than losing anything silently.
- **Recording voice.** One button that changes between "Record" and "Stop", a visible timer and level while recording, and the recording playable afterwards. The person is asked before the microphone opens; if they decline, say how to allow it and keep typing as a way in.
- **Reading a file's content.** For CSV or spreadsheets exported as CSV, show the first rows and which column becomes which field before taking them in. For text, show what was read. Never take a file in without the person seeing what it held.
- **While it works.** Reading, shrinking, recording and sending each show a state ("Reading 3 photos…"), and the controls that would start it again are disabled until it is done.
