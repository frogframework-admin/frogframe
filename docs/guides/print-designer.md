# Letterheads & Print Formats

**Letterheads & Print Formats** (`/admin/print-designs`, System Manager) lets each tenant design its company letterhead and how every document type prints, without code. The live preview uses the same renderer as **Print** and **Download PDF**, so what you see is what prints. Designs are stored per tenant in `frogframe_print_designs` (migration 039).

## Letterheads

- **Content:** company name, address, contact, tax ID, and a logo (PNG, JPEG, WebP or SVG, at most 300 KB). Logos are printed as images, so an SVG cannot run scripts.
- **Look:** alignment (left, center or right), colour of the name and rule, and whether to draw a line under the letterhead.
- **Footer text:** printed at the bottom of every page (for example registered office or bank account).
- **Default:** a tenant can keep several letterheads (for example per branch). One is the default; the first one saved becomes it automatically.

Without any designed letterhead, the one from configuration (`Print:Letterheads:{tenant}`) is used, as before.

## Print formats

A print format belongs to one DocType. A DocType can have several formats (for example *Standard* and *Customer copy*); one is the default.

| Part | Options |
|---|---|
| Page | paper (A4, A5, Letter, Legal), portrait or landscape, accent colour, fields per row (1–3) |
| Letterhead | on or off, default or a named letterhead |
| Title | text on paper (default: the DocType title) and the DRAFT / NOT APPROVED watermark |
| Field blocks | titled blocks of fields, in any order; add, reorder and remove fields and blocks |
| Tables | show or hide each child table, and choose its columns and their order |
| Totals | number fields shown in the totals box, plus the amount in words (Rupiah) |
| Signatures | boxes signed by whoever moved the document to the chosen workflow states |
| Note | text at the bottom |

A new format starts from the DocType's built-in `printFormat` (or its form layout). Every reference is checked against the DocType when saving: fields, table columns, number fields and workflow states. Colours must be `#rrggbb` values.

## Printing

- Print and PDF use the default format of the DocType. `/print/{doctype}/{name}?format=Customer%20copy` and `/api/print/{doctype}/{name}/pdf?format=…` choose another. The print page shows a format switcher when there are several.
- Field permissions still apply: a field in a block is printed only for users allowed to read it.
- Without any designed format, the DocType's metadata `printFormat` is used, exactly as before.
