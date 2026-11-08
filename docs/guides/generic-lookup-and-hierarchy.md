# Generic Lookup Dialog & Self-Referential Hierarchy

This guide explains FrogFramework's generic lookup component and circular reference prevention in self-referencing tree structures.

---

## 1. Generic Lookup Dialog

FrogFramework replaces basic static select dropdowns with a server-paginated, searchable modal dialog when interacting with **Link** fields:

- **Debounced Search**: Queries `GET /api/links/{target}?q=` with server-side filtering.
- **Display Fields**: Shows document title, secondary description columns, and parent module.
- **Keyboard Navigation**: Up/Down arrow key selection with `Enter` confirmation and `Esc` dismissal.

---

## 2. Self-Referential Trees & Hierarchy

Certain business documents naturally form tree structures:
- **Department**: Parent Department (`parent_department_id`).
- **Employee**: Supervisor / Manager (`supervisor_id`).
- **Chart of Accounts**: Parent Ledger Account (`parent_account`).

### Circular Reference Prevention

When a document links to its own DocType, FrogFramework automatically prevents recursive loops:
- A document cannot select itself as its own parent.
- Multi-step loops are detected (e.g., `A → B → C → A`).
- When a loop is detected, the transaction is rejected with a descriptive error message displaying the circular chain:
  ```text
  Circular reference detected: IT -> Infrastructure -> IT
  ```

---

## 3. Interactive `OrgTree` Visualization

Tree-structured DocTypes can be visualized interactively using the `OrgTree` Blazor component:
- Collapsible branches with node counters.
- Visual status cards displaying photos, job titles, and active employee headcounts.
- Highlights the currently authenticated user's position within the organizational hierarchy.

