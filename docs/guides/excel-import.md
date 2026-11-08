# Excel & CSV Data Import Engine Guide

The FrogFrame Import Engine allows users with document creation permissions to upload bulk records using `.xlsx` or `.csv` files.

---

## 1. Unified Pipeline (No Shortcut Bypasses)

Importing records in FrogFrame executes through the standard `DocumentService` pipeline. Every single imported row respects:
- Role permissions and field security levels (`permLevel`)
- Lifecycle hooks (`BeforeValidate`, `BeforeSave`, `AfterSave`)
- Formula computations and linked field fetches (`fetchFrom`)
- Validation rules and constraints
- Versioned audit logs (recorded as `Created via import #imp_...`)
- Transactional outbox event triggers

---

## 2. 4-Step Import Wizard

1. **Download Template or Upload File**:
   - Download a metadata-generated Excel template containing only fields accessible to the current user. Mandatory fields are marked with `*`, dropdown options are pre-populated, and child tables are separated into dedicated sheets.
   - Alternatively, upload custom `.xlsx` or `.csv` spreadsheets.
2. **Column Mapping**: Automatically matches spreadsheet headers to DocType fields (case-insensitive).
3. **Dry-Run Simulation**:
   - Validates all rows against server rules **without persisting to the database**.
   - Displays clear, line-numbered validation errors matching Excel row indices.
   - Execution strategies:
     - **Skip Failed Rows** (default): Successfully validated rows are committed; invalid rows are skipped.
     - **All or Nothing**: Single atomic transaction; any single failure aborts the entire import.
4. **Results & Error Export**:
   - Generates an error workbook containing invalid rows along with an added "Reason for Failure" column for rapid correction and re-upload.

---

## 3. Supported Data Formats

- **Dates**: Excel serial dates, `YYYY-MM-DD`, `DD/MM/YYYY`.
- **Numbers & Currencies**: Standard decimals or formatted currency strings (e.g., `1,500,000.50` or `1.500.000,50`).
- **Checkboxes**: `Yes`/`No`, `1`/`0`, `true`/`false`.
- **Link Fields**: Matches document names or document titles (ambiguous titles throw validation errors).
- **Child Tables**: Linked via an **Import Key** column (e.g., `1`, `2`, `3`) connecting child rows to their parent record.

