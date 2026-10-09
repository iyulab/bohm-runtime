---
name: printable-page
description: How to make an application whose result is printed or saved as a PDF — a quote, an invoice, a receipt, a report, a form, a label. Load it when the person mentions printing, paper, A4, PDF, or a document to hand to someone.
---

# A page that prints well

The person prints from the application with the browser's own print (Ctrl+P, or a "Print" button that calls `window.print()`). What is on paper is the document, not the tool around it.

- **Two faces, one file.** Keep the editing controls (inputs, buttons, lists of saved items, hints) on screen, and in `@media print` show only the document. Hide controls with `@media print { .no-print { display: none !important; } }` rather than duplicating the document.
- **The sheet.** `@page { size: A4; margin: 0 }` and give the document its own padding (about 15–20 mm), so the browser prints no header or footer (no address, no date) on top of it. Use a fixed width that fits A4 (about 180 mm of content) and `box-sizing: border-box`.
- **What prints.** Black text on white; borders and light fills that survive printing (`-webkit-print-color-adjust: exact; print-color-adjust: exact` on anything with a background colour). No shadows or rounded cards in print.
- **Long documents.** `break-inside: avoid` on table rows and on blocks that must stay together (a signature box, a totals block); repeat a table's header on each page with a `<thead>`.
- **Numbers.** Right-align amounts, show the unit or currency once per column, and format with the person's locale (`toLocaleString`). Totals are computed, never typed.
- **On screen too.** Show the document as it will print — a white sheet on a grey background — so the person sees the result before printing.
