# Ad-hoc Report Builder

`/reports/builder` lets users build reports without code: choose a DocType, columns and filters, optionally group and summarise, then run, export to Excel or CSV, and save the report under a name. Saved reports appear on `/reports`, next to the code reports (`IReportProvider`).

## Building a report

| Part | Options |
|---|---|
| Document type | DocTypes where the user has the `report` permission. |
| Columns | Number, created by, document status, workflow state, created, modified, and every value field the user can read. Table, image, attachment, password and layout fields are not offered. |
| Filters | is, is not, greater than, at least, less than, at most, contains, is one of (comma separated). Values are checked against the field type: numbers, `yyyy-MM-dd` dates, and yes/no for checks. |
| Group by | Up to three fields. One row per group. |
| Summaries | count (of documents, or of filled values), sum, average, minimum, maximum. Sum and average need a number field. |
| Sort, limit | Any output column, ascending or descending. 1–10,000 rows. |

Grouped reports show a total row for counts and sums.

## Permissions

- **Data source:** reports read through `DocumentService.QueryForReportAsync` with the `report` action. Tenant isolation, role row scope, row sharing and field redaction therefore apply as in the list view.
- **Read cap:** at most 10,000 documents are read per run. The result says so when the cap is reached, because totals may then be incomplete.
- **Hidden fields:** fields the user cannot read (for example a higher `permLevel`) cannot be shown, filtered, grouped or summarised. A filter on such a field is an error and is never dropped quietly, because dropping it would widen the result.
- **Export:** needs the `export` permission on the DocType.

## Saved reports

- **Storage:** a saved report is a definition only (`frogframe_adhoc_reports`, migration 038). It belongs to its creator.
- **Sharing:** the owner can share it with roles they have; administrators can share with any role. Only roles that have the `report` permission on the DocType are offered.
- **Running a shared report:** each viewer runs it with their own permissions and sees only their own rows. Columns they cannot read are left out, and the result names them. If the report filters on a field the viewer cannot read, it does not run for that viewer.
- **Changes:** only the owner or an administrator can change or delete a report. Others can save a copy. Updates use optimistic concurrency.
