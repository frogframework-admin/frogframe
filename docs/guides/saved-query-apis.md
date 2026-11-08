# Saved Query APIs Guide

FrogFrame enables publishing read-only SQL queries as secure, parameter-validated REST API endpoints without writing custom server code.

---

## 1. Overview & Publishing Flow

Published query endpoints are hosted under:
```http
GET /api/query/{slug}?Parameter1=Value1&Parameter2=Value2
```

### Two Creation Methods
1. **Dedicated Query API Wizard**: Navigate to **Query APIs** → Create New. Follow the step-by-step wizard: write query → configure parameters & preview → publish.
2. **One-Click Publishing from Widgets or Reports**: When saving a Dashboard Widget or Report Template, check the box **"Also publish as API"**. The query, parameters, and role restrictions will automatically sync with the API endpoint. Unchecking the box deletes the API.

---

## 2. Parameter Binding & Security

FrogFrame enforces strict server-side parameter isolation:

| Binding Mode | Description | Overridable via URL? |
|---|---|---|
| **From Caller (`query`)** | Passed as URL query string parameters (e.g., `?Dept=Marketing`). | Yes |
| **From User Account (`user:id`)** | Automatically bound to the caller's unique User ID. | **No** (Strictly forbidden; 422 if attempted) |
| **From Department (`user:department`)** | Automatically bound to the caller's department claim. | **No** (IT managers cannot query HR data by spoofing URLs) |

> [!IMPORTANT]
> Undeclared parameters in the URL query string are immediately rejected with **HTTP 422 Unprocessable Entity** to prevent mass-assignment attacks.

---

## 3. Output Formats & Performance Caching

- **JSON Array**: Standard list of records with metadata (`columns`, `count`, `truncated`).
- **Single Object (KPI)**: Returns the first row as a single JSON object.
- **CSV Export**: `text/csv` formatted in UTF-8 with BOM. Values starting with `=`, `+`, `-`, or `@` are escaped with a leading apostrophe to neutralize CSV Formula Injection.
- **Response Caching**: Responses can be cached from `0` to `3600` seconds via the `X-Cache` header (`HIT` or `MISS`). Saving a new version of the query immediately invalidates the cache.

---

## 4. API Key Access

Query APIs can be protected by integration API keys configured with granular scope wildcards:
- Single query: `query:procurement-summary`
- All queries: `query:*`

