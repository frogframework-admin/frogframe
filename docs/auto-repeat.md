# Auto Repeat (Recurring Document Automation)

FrogFrame supports scheduled recurring document generation through **Auto Repeat**. Similar to Frappe's Auto Repeat, this feature enables tenants to automate the recurring creation of standard transactional documents (such as monthly Purchase Requests, subscription Invoices, or recurring Journal Entries) without manual data re-entry.

---

## 1. Metadata Configuration

### Enabling Auto Repeat on DocTypes

Auto Repeat is opt-in per DocType. To enable it, set `"allowAutoRepeat": true` in the DocType JSON definition:

```json
{
  "name": "PurchaseRequest",
  "title": "Pengajuan Pembelian",
  "allowAutoRepeat": true,
  "storage": {
    "strategy": "document",
    "table": "purchase_requests"
  },
  ...
}
```

> [!IMPORTANT]
> The metadata compiler validates that `allowAutoRepeat` can only be `true` on standard, top-level transactional DocTypes. Setting `allowAutoRepeat` on **Child Tables** (`isChild: true`) or **Single DocTypes** (`isSingle: true`) will throw a compile error (`MetadataCompilationException`).

### Protecting Fields with `noCopy`

When cloning a document during an Auto Repeat run, certain fields should not be copied from the source template (such as specific reference numbers, manual status overrides, or external invoice numbers). Set `"noCopy": true` on any field definition to omit it from recurring document payloads:

```json
{
  "name": "vendor_quotation_no",
  "type": "Data",
  "label": "Vendor Quotation No",
  "noCopy": true
}
```

---

## 2. Schedule Math & Recurrence Frequencies

Auto Repeat schedules calculate subsequent execution dates using `AutoRepeatSchedule.Next(DateOnly current, AutoRepeatFrequency freq)`:

| Frequency | Step Calculation |
|---|---|
| **Daily** | `current.AddDays(1)` |
| **Weekly** | `current.AddDays(7)` |
| **Monthly** | `current.AddMonths(1)` (normalized to valid month days) |
| **Quarterly** | `current.AddMonths(3)` |
| **Yearly** | `current.AddYears(1)` |

### Completion & End Date

When an `EndDate` is defined on the schedule, `AutoRepeatSchedule.IsCompleted(repeat, nextDate)` checks whether `nextDate > repeat.EndDate`. Once completed, the Auto Repeat record is automatically transitioned to `Completed` status and will no longer be selected for recurring execution.

---

## 3. Cloning Engine & Document Generation

During each scheduled run, `AutoRepeatService` performs clean document replication:

1. **Automation Identity & Permissions**: The repeat run executes under the context of the configured `RunAsUserId` (or the creator's identity) through `IAutomationIdentity`. If the user lacks `Create` permission on the target DocType, the run fails safely and logs an audit error.
2. **System Field Stripping**: Top-level system fields (`name`, `owner`, `creation`, `modified`, `docstatus`, `version`, `tenant_id`, `amended_from`) are stripped from the payload.
3. **Child Table Normalization**: All child tables are cloned row-by-row, stripping child system identifiers (`name`, `parent`, `idx`) while preserving all child business fields.
4. **`noCopy` Field Omission**: Any field marked `noCopy` is omitted from the generated document payload.
5. **Date Shifting**:
   - Primary transaction date fields (`posting_date`, `request_date`, `transaction_date`) are automatically set to the current schedule date (`curDate`).
   - Relative due date fields (`due_date`, `required_by`) preserve the original offset relative to the base date (e.g. `curDate + offsetDays`).
6. **Auto Submit**: If `SubmitOnCreation` is enabled and the DocType is submittable (without blocking approval workflows), the document is automatically submitted upon insertion.
7. **Email Notifications**: If `NotifyByEmail` is enabled, an email listing the generated document names is queued to the configured recipient list via the Outbox.

---

## 4. Catch-up Limits & Idempotency

### Catch-up Protection

If the background scheduler has been paused or the server was offline for multiple recurring intervals, `AutoRepeatService` catches up on missing cycles up to **`MaxCatchUpPerCycle = 12`** documents per cycle. This prevents worker thread starvation or massive write bursts.

### Idempotency, Claim-First Protocol & Duplicate Prevention

Every generated document run is managed through a **Claim-First** protocol backed by `frogframe_auto_repeat_runs` with a unique database constraint:

```sql
CONSTRAINT uq_frogframe_auto_repeat_run UNIQUE (tenant_id, auto_repeat_id, scheduled_for)
```

1. **Atomic Period Claim (`TryClaimRunAsync`)**: Before creating a document, the worker attempts to insert a record with status `Claimed`. On conflict, only runs with status `Failed` can be reclaimed. If another worker or instance is currently processing the period or has succeeded, the claim fails immediately.
2. **Crash Resilience**: If the application crashes or is killed mid-run (e.g. during document insertion or auto-submission), the period remains `Claimed` rather than unrecorded, guaranteeing that duplicate documents will never be generated upon restart.
3. **Run Completion (`CompleteRunAsync`)**: Once the document is inserted and any auto-submission completes, the status is updated to `Success` with the generated document name (or `Failed` with error diagnostics if creation threw an exception).

### Authorization & Run-As Security

`AutoRepeatService` strictly enforces security boundaries at the API layer:
- **Ownership Verification**: Non-administrators may only modify, pause, resume, or manually execute Auto Repeat schedules that run as their own user identity.
- **Run-As Elevation Control**: Only System Managers / Administrators can configure a schedule to run as another user (`RunAsUserId`).
- **Source Document Access**: Creating an Auto Repeat schedule requires the user to have read permission on the source template document.

---

## 5. Background Scheduler & Administration

### Scheduled Job (`AutoRepeatJob`)

`AutoRepeatJob` implements `IScheduledJob` with a cron expression of `@hourly`. Every hour, it evaluates all active Auto Repeat schedules whose `NextScheduleDate <= today`.

### User Interface

- **Form Toolbar (`Repeat`)**: For any DocType with `AllowAutoRepeat = true`, a **Repeat** button appears on the document form header. Clicking it opens a drawer to configure recurrence frequency, date range, auto-submit options, and notification recipients.
- **Admin Management (`/admin/auto-repeats`)**: Accessible from the Settings navigation menu. System Managers can view all recurring schedules across the tenant, toggle status (**Pause** / **Resume**), trigger an immediate catch-up (**Run Now**), and inspect detailed execution logs with status, error traces, and links to generated documents.

