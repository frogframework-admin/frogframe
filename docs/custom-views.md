# Saved Filters & Custom Views (P1-8)

FrogFramework provides user-defined **Saved Filters and Custom Views** per DocType, allowing each user to persist, name, and recall frequently used search filter combinations (e.g. *"PO Pending Approval"*, *"Unpaid Invoices"*, *"High Priority Tasks"*) without modifying global schemas or affecting other users.

---

## Key Capabilities

- **Named Filter Presets per DocType**: Save multi-clause filter combinations under a custom name (e.g., *"Draft Invoices This Month"*).
- **Default View Auto-Loading**: Mark a preferred view as default (`IsDefault`). When navigating to the DocType's list page, the default view's filter clauses are automatically loaded and applied.
- **Dynamic Condition Builder**:
  - Filter drawer with row-based condition builder supporting all queryable scalar fields (Data, Int, Float, Currency, Date, Datetime, Check, Select, Link, Workflow State).
  - Supported operators: `=` (Equals), `!=` (Not equals), `>` (Greater than), `<` (Less than), `>=` (Greater or equal), `<=` (Less or equal), `like` (Contains text), and `in` (In set).
  - Value input adapts dynamically to field type (e.g. Workflow State dropdown, Checkbox, Number input, Date picker, or Text box).
- **Interactive Active Filter Strip**:
  - Filter chips appear below the toolbar showing active field, operator, and value.
  - Individual filters can be dismissed with a single click (`✕`), or all active filters cleared at once.
- **Manage Views Drawer**:
  - Rename custom views.
  - Set or toggle the default view (`★ Default`).
  - Delete unused custom views with a confirmation dialog.
- **Export Consistency**: Active custom filters are passed directly to `Docs.ExportListAsync(...)`, ensuring exported Excel (`.xlsx`) and CSV reports match the filtered list view.
- **Per-User & Per-Tenant Isolation**: Saved views are stored in `IUserPreferenceStore` partitioned by `(tenant_id, user_id, views:{docType})`. Users cannot see or alter another user's views.

---

## Data Models & Persistence

Saved filters and custom views are defined in `Framework.Core/Preferences/UserPreferences.cs`:

### Data Structures

```csharp
public sealed record SavedFilterClause
{
    public string Field { get; init; } = "";
    public string Op { get; init; } = "=";
    public object? Value { get; init; }

    public FilterClause ToFilterClause() => new(Field, Op, CustomViewExtensions.UnwrapValue(Value));
}

public sealed record SavedCustomView
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string? Search { get; init; }
    public List<SavedFilterClause> Filters { get; init; } = [];
    public string? SortField { get; init; }
    public string? SortDirection { get; init; }
    public bool IsDefault { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DocTypeCustomViewsPreference
{
    public List<SavedCustomView> Views { get; init; } = [];
    public string? DefaultViewId { get; init; }
}
```

### Storage Key & API Extensions

Saved views are persisted via `IUserPreferenceStore` under the key:
`views:{docType}` (e.g. `views:Sales Invoice`).

Extension methods on `IUserPreferenceStore`:
- `GetCustomViewsAsync(tenantId, userId, docType, cancellationToken)`: Retrieves custom views and the active default view ID.
- `SaveCustomViewAsync(tenantId, userId, docType, view, cancellationToken)`: Creates or updates a custom view.
- `DeleteCustomViewAsync(tenantId, userId, docType, viewId, cancellationToken)`: Removes a view and clears default status if the deleted view was default.
- `SetDefaultCustomViewAsync(tenantId, userId, docType, viewId, cancellationToken)`: Designates a view as the default for the list page.
- `UnwrapValue(object? val)`: Safely unwraps `JsonElement` scalar types (string, boolean, integer, decimal, array) into CLR primitives for `FilterClause`.

---

## User Interface & Workflow

### 1. View Selector
Located on the `ListPage.razor` toolbar:
- Shows **"Custom Views (All)"** by default.
- Lists all saved views for the current DocType with a star icon (`★`) denoting the default view.
- Selecting a view immediately applies its filter clauses and updates the active filter chips.
- Selecting **"Manage Views..."** opens the view management drawer.

### 2. Filter Drawer (`AdminDrawer`)
- Click the **Filter** button (with a badge counter indicating the count of active filter clauses).
- Add one or multiple filter rows: choose Field, Operator, and Value.
- Click **Apply** to run the filter query.
- Use **Save as View** to save the current filter combination under a descriptive name.

### 3. Active Filter Chips Strip
- Displayed between the toolbar and data grid.
- Shows current active view name badge (if a saved view is selected).
- Displays each filter clause as a removable chip: `Field Operator Value ✕`.
- Includes a **"Clear All"** button to reset filters to the default state.

---

## Localization

All UI labels, button tooltips, placeholders, and drawer texts default to English and are localized in Indonesian via `src/Framework.Web/Localization/framework.id.translations.json`:
- `Custom Views` / `Filter Tersimpan`
- `Save Current Filters as View` / `Simpan Filter Aktif sebagai View`
- `Manage Custom Views` / `Kelola Filter Tersimpan`
- `Set as Default` / `Jadikan Default`
- `Clear All` / `Hapus Semua`
