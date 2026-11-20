# List Views: Board, Calendar and Tree

Every DocType list (`/app/{DocType}`) can be shown as a grid, a **board**, a **tree** or a **calendar**. The layout toggle in the list toolbar switches between them, and the choice is kept in the URL (`?view=board`, `?view=tree`, `?view=calendar`), so a layout can be bookmarked or linked.

All layouts use the same list query. Search, filters, saved views, row-level scope and field permissions therefore apply to all of them.

## Board (Kanban)

The board is available for top-level DocTypes that have a workflow, or that have submit enabled.

- **Columns**
  - With a workflow, there is one column per workflow state, in the order the workflow defines them, coloured by the state tone.
  - Without a workflow, the columns are *Draft*, *Submitted* and, if cancel is allowed, *Cancelled*.
- **Moving a card** (drag and drop, or the ⇄ menu on the card for keyboard and touch users)
  - The move runs the workflow action that leads to the target column.
  - Only actions returned by `DocumentService.AvailableActionsAsync` are offered. That method checks role, transition condition and row scope.
  - `RunActionAsync` checks the move again on the server, so a stale or forged move is rejected.
- **Special cases**
  - When two actions lead to the same column, the board asks which one to run.
  - Actions with `requireComment` ask for a comment first.
  - *Amend* is never offered, because it creates a new revision.
- **Cards** show the title field (or the document number) and up to three list columns. Each column renders up to 100 cards.

## Calendar

The calendar is available for top-level DocTypes with at least one `Date` or `Datetime` field. It has month, week and day ranges.

```json
"listView": {
  "titleField": "subject",
  "calendar": { "startField": "starts_at", "endField": "ends_at", "colorField": "priority" }
}
```

| Setting | Required | Meaning |
|---|---|---|
| `startField` | yes | `Date` or `Datetime` field where an entry starts. |
| `endField` | no | `Date` or `Datetime` field where it ends. Date ends are inclusive. |
| `colorField` | no | Field whose value picks the entry colour, for example a Select. Colours follow the option order and are shown in a legend. Without it, entries are coloured by workflow or document status. |

The metadata compiler rejects a calendar whose fields do not exist or have the wrong type.

When there is no `calendar` setting, the first `Date`/`Datetime` list column is used (or the first such field). Users can switch to any other date field they are allowed to read.

### Placement rules

- `Datetime` values are shown in the default time zone (`FieldTypeSettings.DefaultTimeZone`). An entry without an end lasts one hour.
- Entries that cross midnight appear on both days.
- Entries covering whole days are shown in the *All day* row of the week and day views.
- Overlapping timed entries are placed side by side.
- Documents without a start value are counted in a note above the calendar and are not placed.

### New document on a date

The **+** on a day opens `/app/{DocType}/new?prefill.{startField}=yyyy-MM-dd`. A `Datetime` start field is prefilled with 09:00 on that day. It is offered only when the user may create the DocType and write the field.

The form accepts `prefill.{field}` for any scalar field the user may write. Each value passes through the field type's normalization, and the server validates as usual on save.

## Tree

The tree is available for parent–child masters: DocTypes with a `Link` field to themselves, such as the chart of accounts, cost centers and organization units.

```json
"listView": {
  "tree": { "parentField": "parent_account", "groupField": "is_group" }
}
```

| Setting | Required | Meaning |
|---|---|---|
| `parentField` | yes | `Link` field to the same DocType that holds the parent. |
| `groupField` | no | `Check` field marking nodes that can have children. Only those nodes offer **+** (new child). |

The metadata compiler rejects a parent field that is not a `Link` to the same DocType, and a group field that is not a `Check`.

When there is no `tree` setting, a self-referencing `Link` whose name starts with `parent` is used. That choice matters for organization units, which also link to their previous version. A `Check` field named `is_group` is used as the group field.

- **Order:** nodes keep the list's sort order among siblings. Group nodes show how many documents are below them.
- **Expansion:** small lists open three levels deep and larger ones one level. *Expand all* and *Collapse all* are in the toolbar.
- **Missing parents:** a document whose parent is not in the list is shown at the top level and marked *parent not shown*. This happens when the parent is filtered out or not visible to the user.
- **Bad data:** a parent loop is cut and marked *parent loop*, so every document still appears once.
- **Keyboard:** Up and Down move between nodes, Right expands a node or moves to its first child, Left collapses it or moves to its parent, Home and End jump to the first and last node, and Enter opens the document.
- **New child:** the **+** opens `/app/{DocType}/new?prefill.{parentField}={name}`.
