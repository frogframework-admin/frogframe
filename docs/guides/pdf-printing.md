# PDF Printing & Server-Side Rendering Guide

FrogFrame enables printing documents with customizable letterheads, tabular layouts, and pixel-perfect server-side PDF exports.

---

## 1. Print Format Architecture

FrogFrame print templates are defined using metadata structures:
- **Header & Letterhead**: Dynamic company logos, addresses, and tax identifiers retrieved from `ILetterheadProvider`.
- **Document Sections**: Grouped field key-value pairs formatted with clean typographic borders.
- **Child Tables**: Formatted line items with aligned numerical amounts, tax summaries, and grand totals.
- **Signatures**: Dynamic signature boxes bound to workflow approvers and audit timestamps.

---

## 2. Server-Side Chromium PDF Renderer

To ensure identical rendering across client devices and operating systems, FrogFrame renders PDFs using headless Chromium (`IPdfRenderer`):

```csharp
public interface IPdfRenderer
{
    Task<byte[]> RenderHtmlToPdfAsync(string htmlContent, PdfPrintOptions options, CancellationToken ct);
}
```

### PDF Configuration Options
- **Page Size**: A4, Letter, Legal.
- **Orientation**: Portrait or Landscape.
- **Margins**: Customizable top, right, bottom, and left margins in millimeters.
- **Header/Footer**: Page numbering (`Page 1 of 3`) and document version watermarks.

---

## 3. Direct Browser Printing vs. Server Export

1. **Browser Interactive Print**: Users can preview documents in the Blazor UI (`/print/{DocType}/{Name}`) and trigger native browser printing (`window.print()`).
2. **REST PDF Export**: Headless PDF generation via `GET /api/data/{DocType}/{Name}/print.pdf`. This route supports both session cookie authorization and integration API keys.

