# OpenXML Template Reporting Guide (Excel & Word)

DocFrame includes a template-driven reporting engine that generates production-ready `.xlsx` spreadsheets and `.docx` Word documents directly from database queries.

---

## 1. Zero External Dependencies

Reports are generated using built-in .NET XML manipulation:
- **`System.IO.Compression`** + **`System.Xml.Linq`**
- No commercial licensing overhead (e.g., EPPlus, Aspose).
- Free from Office Interop or heavy runtime libraries.

---

## 2. Template Tag Syntax

Placeholders in Word and Excel templates are evaluated during generation:

| Tag Syntax | Resolution Behavior | Example |
|---|---|---|
| `{{Tag}}` | Single scalar value (parameters, header query columns, system variables). | `{{Department}}`, `{{Today}}` |
| `{{Sum:Column}}` | Aggregate operations over detail query result rows (`Avg`, `Min`, `Max`, `Count`). | `{{Sum:GrandTotal}}` |
| `[[Column]]` | Repeating row block (repeats Excel rows, Word table rows, or paragraphs). | `[[ItemName]]`, `[[Quantity]]` |
| `[[No]]` | Automatic 1-based row counter. | `[[No]]` |
| `{{Tag\|format}}` | .NET standard format strings. | `{{Date\|yyyy-MM-dd}}`, `[[Amount\|N0]]` |

---

## 3. Spreadsheet Dynamic Expansion (Excel)

When generating Excel reports:
- Template rows expand downward dynamically based on query record count.
- Formulas situated below repeating tables automatically shift down, and formula ranges expand to encapsulate generated rows (`=SUM(D6:D6)` expands to `=SUM(D6:D105)`).
- Cell types are preserved: numeric values remain numbers, dates remain Excel dates, and chart series references expand automatically.
- Merged cells, conditional formatting, and print ranges adjust automatically.

---

## 4. Security & Guardrails

- **Formula Injection Mitigation**: Exported cells prepended with `=`, `+`, `-`, or `@` are stored strictly as text values to prevent formula injection.
- **Resource Caps**: Enforced via `Reports:MaxRowsExcel` (default 50,000) and `Reports:MaxRowsWord` (default 5,000). Excess rows trigger structured exceptions rather than silent truncation.
- **XXE & Zip-Bomb Protection**: Templates with VBA macros are rejected, XML DTD processing is disabled, and uncompressed stream sizes are capped at 5 MB.

