# Form Panels and Approval Review

A **form panel** is a Blazor component an application shows on the desk form of one DocType, above the tabs. The ERP
uses it for the approval review of self-service requests: the approver sees the evidence next to the request and
decides with the workflow buttons in the toolbar, as before.

## Registering a panel

```csharp
services.AddFormPanel<MyReviewPanel>("MyDocType", order: 100, visible: (user, doc) => doc.WorkflowState != "Draft");
```

The component receives two parameters and injects whatever else it needs:

```razor
@code {
    [Parameter] public Document Doc { get; set; } = default!;        // the saved document
    [Parameter] public CompiledDocType Meta { get; set; } = default!;
}
```

- Panels are rendered for saved documents only (not on `/new`), after the form has loaded the document with the
  user's permissions.
- The panel is rebuilt whenever the document is reloaded (save, workflow action): its key includes the version.
- A panel that reads other data must check permissions itself. The ERP panels read the request again through
  `DocumentService.GetAsync(user, …)` before gathering anything else.

## ERP approval review panels

| DocType | Panel | What the approver sees |
|---|---|---|
| `FaceRegistration` | `FaceRegistrationReview` | The photos sent, the employee record photo and the active registration it replaces, image quality and liveness scores, consent time, face model, device; punch-matching setting from HR Settings. Checks: consent, number of photos, model change, the same template or the same phone already registered for another employee. |
| `AttendanceCorrection` | `AttendanceCorrectionReview` | The day's shift, recorded vs corrected check-in/out with the attendance code, late and early-out minutes and working hours each gives (a preview computed with the same rules as the Attendance record, nothing is saved), and the day's mobile punches including refused and flagged ones with their reasons and distance. Checks: month already paid, no attendance record yet, code set by HR, refused punches that support the request, the number of corrections this month, corrected times that are still invalid, no supporting document. |
| `BiodataChangeRequest` | `BiodataChangeReview` | Each field with the value now on the employee record and the requested value; tables (dependents, bank accounts, emergency contacts, identity documents) row by row with added, removed and changed cells marked. Checks: field changed on the employee after the request, NIK already used by another employee, no supporting document for bank, identity or family changes. |

Every panel shows who the request is waiting for (the supervisor by name, or the HR role) and, once decided, the
outcome. The data comes from `EssReviewService` (`src/Apps.Erp/Ess/EssReview.cs`); the components are in
`src/Apps.Erp/Components/Review/`, the styles in `desk.css` (`.review-*`, light and dark, stacked on phones).

The ESS mobile app gets the same request details from `GET /api/ess/v1/approvals/{doctype}/{name}`
([ESS mobile API](ess-mobile-api.md), section 7.2).
