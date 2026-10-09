---
name: records-and-tables
description: How to make an application that keeps a growing list of records — a ledger, an inventory, a log, a list of customers, orders or tasks — and shows them as a list or table with totals. Load it when the person will add entries day after day and look back over them.
---

# Records that stay readable as they grow

The list starts empty and grows for months. Design for the hundredth entry, not the first.

- **Adding.** One short form at the top (or a clear "Add" button that opens it), the most-used field focused, Enter to save, the form cleared after saving and the new row briefly highlighted. Sensible defaults: today's date, the last category used.
- **The list.** Newest first unless the person asked otherwise. A table when records have several columns to compare; cards on a narrow window. Columns aligned: text left, numbers and amounts right, dates in the person's locale (`toLocaleDateString`). A header that stays visible while scrolling (`position: sticky`).
- **Finding.** A search box that filters as one types, and filters for the obvious dimensions (a category, a month, a status). Say how many rows are shown out of how many ("12 of 340").
- **Totals.** Sums and counts for what is shown — computed, never typed — in a totals row or summary cards above the list; they follow the filters.
- **Changing.** Each row can be edited in place or in the same form; deleting asks nothing but offers "Undo" for a few seconds. Never lose an entry to a mistyped number: validate on save and say what is wrong next to the field.
- **Going out and coming in.** Offer "Export CSV" (UTF-8 with a BOM so spreadsheet programs read non-Latin text) and, when it makes sense, "Import CSV" that shows what will be added before adding it.
- **Keeping.** Store records as one array of objects under one localStorage key, each with a stable `id` and the time it was created; read it back on open. Write after every change.
