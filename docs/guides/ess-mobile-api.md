# ESS (Employee Self-Service) Mobile Application API — v1

This document is the API contract for the ESS mobile application (Flutter / mobile client). All ESS endpoints reside under **`/api/ess/v1`** and always operate in the context of the **employee owning the account** (determined by the `employee` claim on the user account). The server never accepts an employee ID from the mobile client for attendance punches, leave requests, payslips, or approvals.

- Base URL example: `https://erp.example.co.id`
- Format: JSON UTF-8, camelCase property names.
- Dates: `yyyy-MM-dd`. Timestamps: ISO 8601 with offset (`2026-11-18T08:01:12+07:00`). The server returns UTC time (`...Z`) or formatted with an offset.
- Working time zone: returned by `GET /me` (`timeZone`, defaults to `Asia/Jakarta`). Work dates and tardiness are evaluated in this zone.

## 1. Authentication

Authentication uses standard ASP.NET Core Identity with Bearer tokens. Username = ERP account username. Accounts automatically provisioned from employee records use the **work email** (lowercase) as the username.

### 1.1 Login
```http
POST /api/auth/login
Content-Type: application/json

{ "userName": "ahmad.fadilah@acme.co.id", "password": "SecretPassword2026" }
```

cURL Example:
```bash
curl -X POST http://localhost:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName": "ahmad.fadilah@acme.co.id", "password": "SecretPassword2026"}'
```

Response 200:
```json
{ "tokenType": "Bearer", "accessToken": "CfDJ8...", "expiresIn": 3600, "refreshToken": "CfDJ8..." }
```
- 401 `Login failed`: Invalid username/password or deactivated account.
- 423 `Account locked`: Locked for 15 minutes after 5 consecutive failed attempts.

### 1.2 Refresh Token (Without Password)
Access tokens are valid for **1 hour**; refresh tokens are valid for **30 days** (server setting `Ess:RefreshTokenDays`).
```http
POST /api/auth/refresh
Content-Type: application/json

{ "refreshToken": "CfDJ8..." }
```

cURL Example:
```bash
curl -X POST http://localhost:5080/api/auth/refresh \
  -H "Content-Type: application/json" \
  -d '{"refreshToken": "CfDJ8..."}'
```
Response 200 is identical to login (returns a new access token **and** a new refresh token — persist both, do not reuse the old refresh token).
401 = refresh token expired, password changed, or account disabled/locked → present the login screen.

Mobile App Best Practices:
- Store the refresh token in `flutter_secure_storage` (Android Keystore / iOS Keychain); unlock the application using local biometrics (fingerprint / Face ID) or device PIN.
- Proactively refresh the access token when remaining lifespan is < 5 minutes, or once upon receiving a 401 response and replay the failed request.
- Do not authenticate with password on every app launch: concurrent logins during morning rush hour put unnecessary load on the server due to password hashing.

### 1.3 Request Headers
```
Authorization: Bearer <accessToken>
Accept: application/json
```
All state-mutating requests **must** include the Bearer token header (cookies are rejected).

### 1.4 Account Profile
`GET /api/auth/me` → account identity (ID, username, tenant, roles, `employee` claim). For employee HR data, use `GET /api/ess/v1/me`.

## 2. Error Response Conventions

All errors return `application/problem+json`:
```json
{
  "type": "https://httpstatuses.io/422",
  "title": "Validation failed",
  "status": 422,
  "detail": "Only 3 days of Annual Leave remain",
  "errors": [ { "path": "to_date", "message": "Only 3 days of Annual Leave remain" } ]
}
```

| Status | Meaning | Client Action |
|---|---|---|
| 400 | Invalid payload / malformed JSON | Correct request payload (client bug) |
| 401 | Missing or expired token | Refresh token and retry once; if failed → prompt login |
| 403 | Forbidden, or account not linked to an employee | Display `detail` message |
| 404 | Resource not found / does not belong to employee | Display user message |
| 409 | Version conflict / document state conflict | Reload document data |
| 422 | Business validation failed | Display `errors[].message` (or `detail`) |
| 429 | Rate limit exceeded | Back off according to `Retry-After` header (seconds) |
| 500/503 | Server error / service unavailable | Retry with exponential backoff; enqueue punch offline |

*Exception*: **Rejected attendance punches** (`POST /attendance/punches`) return 422 with the structured **punch response body** (Section 4.2), not problem+json.

## 3. Profile & Home

### 3.1 `GET /api/ess/v1/me`
Called on app launch; response may be cached on device (rarely changes).
```json
{
  "employee": "EMP-0001",
  "fullName": "Ahmad Fadilah",
  "jobTitle": "IT Staff",
  "position": "POS-000002",
  "organizationUnit": "DEPT-IT",
  "company": "ACME",
  "reportsTo": "EMP-0003",
  "location": "JKT-HQ",
  "status": "Active",
  "shift": { "name": "Office 08-17", "start": "08:00:00", "end": "17:00:00" },
  "attendance": {
    "mode": "Assigned location",
    "deviceBinding": true,
    "deviceRegistered": true,
    "requirePhoto": false,
    "maxAccuracyMeters": 100,
    "minPunchIntervalMinutes": 1,
    "maxOfflineHours": 24,
    "rejectOutsideGeofence": true,
    "rejectMockLocation": true,
    "sites": [
      { "code": "JKT-HQ", "name": "Headquarters Jakarta", "latitude": -6.2251, "longitude": 106.809, "radiusMeters": 150 }
    ]
  },
  "timeZone": "Asia/Jakarta",
  "serverTime": "2026-11-18T01:00:02.123+00:00"
}
```
- `attendance.mode`: `Assigned location` (strictly within assigned work location radius), `Any office` (within radius of any company location that permits mobile attendance), `Anywhere` (field staff; coordinates captured without geofence check), `Not allowed` (must use physical biometric machines — hide mobile punch button).
- `sites`: Used by client to render map / calculate proximity prior to punching. **The final geofencing evaluation is always performed by the server.**
- `serverTime`: Used to compute clock skew against device clock; warn user if automatic device time is disabled.

### 3.2 `GET /api/ess/v1/home`
Summary payload for the dashboard home screen.
```json
{
  "serverTime": "2026-11-18T01:05:00+00:00",
  "today": {
    "workDate": "2026-11-18",
    "lastPunchType": "in",
    "punches": [ { "...": "see 4.4" } ],
    "attendance": { "...": "see 4.4 (null if pending aggregation)" }
  },
  "leave": [ { "...": "see 5.1" } ],
  "latestPayslip": { "name": "PSL-2026-000123", "period": "2026-10", "grossEarnings": 8500000, "netPay": 8086086, "pph21": 153914, "pdfUrl": "/api/print/Payslip/PSL-2026-000123/pdf" },
  "pendingApprovals": 2,
  "myPendingRequests": 1
}
```
- `lastPunchType`: `in` → display **Clock Out** button; `out` or `null` → display **Clock In** button.
- `today.attendance`: Populated once the server aggregation job has processed the punch (max ~5 minutes); prior to aggregation, display status derived from `today.punches`.

## 4. Attendance

### 4.1 Mobile Client Workflow
1. Acquire GPS location (high accuracy, ~15-second timeout). Set `isMockLocation` using OS mock location detection (Android `isMock`/`isFromMockProvider`).
2. Generate a fresh **UUID v4** for this punch attempt, store it in local offline queue (SQLite / Hive) **before** sending.
3. Submit `POST /attendance/punches`. On success (201/200) or business rejection (422) → remove from queue, render result.
   On network failure / 5xx / 429 → retain in queue, retry later **with the exact same ID** (`isOffline: true` if punch was executed without network connectivity).
4. If selfie is required/captured, upload after punch is acknowledged (`POST /attendance/punches/{id}/photo`); may be deferred (e.g. awaiting Wi-Fi).

Retrying with the same punch UUID **never** produces duplicate records; the server returns the existing saved record (`duplicate: true`).

### 4.2 `POST /api/ess/v1/attendance/punches`
```http
POST /api/ess/v1/attendance/punches
Authorization: Bearer <token>
Content-Type: application/json

{
  "id": "6f1c2a52-6a0e-4a8b-9a0e-8f2d1b7c3e11",
  "type": "in",
  "latitude": -6.225180,
  "longitude": 106.809120,
  "accuracy": 12.5,
  "isMockLocation": false,
  "isOffline": false,
  "deviceTime": "2026-11-18T08:01:12+07:00",
  "deviceId": "a3f9c1e2b7d84e60",
  "deviceInfo": "Samsung SM-A155F / Android 14",
  "appVersion": "1.0.3+15",
  "note": null
}
```

cURL Example (Clock-In):
```bash
curl -X POST http://localhost:5080/api/ess/v1/attendance/punches \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{
    "id": "6f1c2a52-6a0e-4a8b-9a0e-8f2d1b7c3e11",
    "type": "in",
    "latitude": -6.225180,
    "longitude": 106.809120,
    "accuracy": 12.5,
    "isMockLocation": false,
    "isOffline": false,
    "deviceTime": "2026-11-18T08:01:12+07:00",
    "deviceId": "a3f9c1e2b7d84e60",
    "deviceInfo": "Samsung SM-A155F / Android 14",
    "appVersion": "1.0.3+15",
    "note": "Clocking in on time"
  }'
```

| Field | Required | Notes |
|---|---|---|
| `id` | Yes | UUID v4 from client (idempotency key) |
| `type` | Yes | `in` (clock in) or `out` (clock out) |
| `latitude`, `longitude` | Yes* | Decimal degrees. *Optional only for `Anywhere` mode |
| `accuracy` | Recommended | GPS accuracy radius in meters |
| `isMockLocation` | Yes | `true` if client OS flagged simulated/mock location |
| `isOffline` | Yes | `true` if punch occurred offline and was delivered later |
| `deviceTime` | Yes | Device timestamp when button was pressed, with offset |
| `deviceId` | Yes | Stable device ID per installation (`android_id`/`identifierForVendor`, or persistent UUID in secure storage) |
| `deviceInfo`, `appVersion`, `note` | No | Max 200 / 50 / 500 characters respectively |

Response (201 created, 200 replayed/duplicate, 422 rejected):
```json
{
  "id": "6f1c2a52-6a0e-4a8b-9a0e-8f2d1b7c3e11",
  "type": "in",
  "status": "accepted",
  "duplicate": false,
  "reasons": [],
  "punchTime": "2026-11-18T01:01:13.402+00:00",
  "serverTime": "2026-11-18T01:01:13.402+00:00",
  "localTime": "2026-11-18T08:01:13",
  "workDate": "2026-11-18",
  "location": { "code": "JKT-HQ", "name": "Headquarters Jakarta", "distanceMeters": 14.2, "radiusMeters": 150 }
}
```
- `status`: `accepted` (valid), `flagged` (recorded but flagged for HR review), `rejected` (disallowed and not counted toward attendance).
- `reasons[]`: `{ "code": "...", "message": "..." }` — display `message` to the user.
- `punchTime`: Online punch = **server time**; offline punch = `deviceTime` (if validated within acceptable threshold).
- `workDate`: Work calendar date. An out-punch crossing midnight within 16 hours of clock-in inherits the clock-in date (night shift support).

#### Reason Codes

| Code | Outcome | Message / Action |
|---|---|---|
| `employee_inactive` | rejected | Employee status is inactive |
| `mobile_attendance_not_allowed` | rejected | Employee must use physical time clocks |
| `device_not_registered` | rejected | Device is not the bound device; HR must clear *ESS registered phone* to re-bind |
| `device_id_missing` | rejected | `deviceId` was omitted |
| `offline_too_old` | rejected | Offline punch older than configured `maxOfflineHours` |
| `device_time_in_future` | rejected | Device clock is set to the future (offline punch) |
| `too_soon` | rejected | Same punch type within `minPunchIntervalMinutes` (double-tap prevention) |
| `mock_location` | rejected* | Mock/fake location detected (*flagged if HR policy permits) |
| `outside_geofence` | rejected* | Outside permitted site radius (*flagged if HR policy is lenient) |
| `location_missing` | rejected* | Coordinates omitted for mode other than `Anywhere` |
| `no_site_configured` | flagged | Work location lacks coordinates or radius configuration |
| `low_accuracy` | flagged | GPS accuracy radius exceeds `maxAccuracyMeters` |
| `offline` | flagged | Offline punch synced later; device timestamp was used |
| `clock_skew` | flagged | Device clock differs > 5 minutes from server (online punch still uses server time) |
| `out_without_in` | flagged | Clock-out recorded without matching clock-in |
| `face_not_registered` | flagged | Face match required, but the employee has no verified Face Registration (see 12.1) |
| `face_missing` | flagged | Face match required, but the punch carries no `face.score` |
| `face_mismatch` | flagged* | `face.score` below HR Settings *Minimum face match score* (*rejected when *Reject punches below the face score* is on) |
| `face_template_outdated` | flagged | `face.registration` is not the active registration: download the template again |

**Face match.** When HR Settings › *Require face match on every punch* is on, add to the punch body:
`"face": { "score": 0.91, "registration": "FACE-2026-00012", "modelVersion": "1.2", "liveness": 0.97 }`.
The app compares the live face with the template from `GET /face/template` on the phone; the server checks the score
against its own threshold (the app's threshold is ignored).

GPS accuracy is evaluated with tolerance: if the accuracy circle (up to `maxAccuracyMeters`) intersects the site radius circle, the punch is considered within geofence boundaries.

### 4.3 `POST /api/ess/v1/attendance/punches/{id}/photo`
Selfie photo for punch `{id}` (owned by the authenticated employee). JPEG, PNG, or WebP, **max 1 MB** (compress client-side: max dimension ~800px, quality ~70).
Two supported transport options:
```http
POST /api/ess/v1/attendance/punches/6f1c2a52-.../photo
Authorization: Bearer <token>
Content-Type: multipart/form-data; boundary=...

(form field "photo" = image binary)
```
or raw image body with `Content-Type: image/jpeg`.

cURL Example (Multipart):
```bash
curl -X POST http://localhost:5080/api/ess/v1/attendance/punches/6f1c2a52-6a0e-4a8b-9a0e-8f2d1b7c3e11/photo \
  -H "Authorization: Bearer <accessToken>" \
  -F "photo=@selfie.jpg;type=image/jpeg"
```

Response 200: `{ "id": "6f1c2a52-...", "photoFileId": "f_8a1d..." }`.
Re-uploading for an existing punch returns the same `photoFileId`. The photo attaches to the corresponding DocFrame `Attendance` record (in *Check-in/Check-out photo*) on the next aggregation cycle.

### 4.4 `GET /api/ess/v1/attendance?from=2026-11-01&to=2026-11-18`
Attendance history (defaults to start of current month through today; max 62-day span), ordered descending by date.

cURL Example:
```bash
curl -X GET "http://localhost:5080/api/ess/v1/attendance?from=2026-11-01&to=2026-11-18" \
  -H "Authorization: Bearer <accessToken>"
```
```json
{
  "from": "2026-11-01",
  "to": "2026-11-18",
  "days": [
    {
      "date": "2026-11-18",
      "attendance": {
        "name": "ATT-2026-0000123", "date": "2026-11-18", "status": "Present",
        "checkIn": "2026-11-18T08:01:13+07:00", "checkOut": "2026-11-18T17:03:40+07:00",
        "workingHours": 8.04, "lateMinutes": 0, "leaveApplication": null, "code": "ONT", "codeFinal": true, "earlyOutMinutes": 0, "source": "Mobile app", "notes": null
      },
      "punches": [
        {
          "id": "6f1c2a52-...", "type": "in", "status": "accepted", "reasons": [],
          "punchTime": "2026-11-18T01:01:13.402+00:00", "localTime": "2026-11-18T08:01:13", "workDate": "2026-11-18",
          "location": "JKT-HQ", "distanceMeters": 14.2, "isOffline": false, "hasPhoto": true,
          "attendance": "ATT-2026-0000123", "processError": null
        }
      ]
    }
  ]
}
```
`processError` contains error details if a punch could not be materialized into an `Attendance` record (e.g. payroll period already locked) for HR review.

## 5. Leave Management

### 5.1 `GET /api/ess/v1/leave/types`
```json
{
  "items": [
    {
      "leaveType": "Annual Leave", "isPaid": true, "isAnnualQuota": true, "requiresAttachment": false, "maxConsecutiveDays": 12,
      "allocated": 12, "used": 2, "pending": 1, "balance": 10, "validFrom": "2026-01-01", "validTo": "2026-12-31"
    },
    {
      "leaveType": "Sick Leave", "isPaid": true, "isAnnualQuota": false, "requiresAttachment": true, "maxConsecutiveDays": 0,
      "allocated": null, "used": null, "pending": null, "balance": null, "validFrom": null, "validTo": null
    }
  ]
}
```
`balance` = allocated − used (approved). `pending` = requests awaiting approval (does not yet decrement balance, but server validates against balance overflow).

### 5.2 `GET /api/ess/v1/leave/applications?year=2026`
```json
{ "year": 2026, "items": [ { "name": "LV-2026-000001", "leaveType": "Annual Leave", "fromDate": "2026-01-19", "toDate": "2026-01-20",
  "halfDay": false, "totalDays": 2, "reason": "Family event", "approver": "EMP-0003", "state": "Approved", "docstatus": 1 } ] }
```
`state`: `Draft`, `Pending Approval`, `Approved`, `Rejected`, `Cancelled`.

### 5.3 `POST /api/ess/v1/leave/applications`
```json
{ "leaveType": "Annual Leave", "fromDate": "2026-12-22", "toDate": "2026-12-24", "halfDay": false, "reason": "Year-end holiday", "submit": true }
```
- `submit` (defaults to `true`): Directly submits to supervisor (`Pending Approval`). `false` = save as draft.
- 201 → object schema identical to 5.2. 422 → business rule rejection (insufficient balance, date overlap, max consecutive days exceeded, attachment required) with server message.
- Leave types requiring a supporting certificate (`requiresAttachment`): upload it first with `POST /files/LeaveApplication/attachment` (12.6) and send `"attachmentFileId": "f_…"`.

### 5.4 `POST /api/ess/v1/leave/applications/{name}/withdraw`
Withdraws a pending application still in `Pending Approval` state. 200 → latest updated leave application object.

## 6. Payslips

### 6.1 `GET /api/ess/v1/payslips?year=2026`
Retrieves finalized (submitted) payslips only.
```json
{ "year": "2026", "items": [ { "name": "PSL-2026-000123", "period": "2026-10", "grossEarnings": 8500000, "netPay": 8086086, "pph21": 153914,
  "pdfUrl": "/api/print/Payslip/PSL-2026-000123/pdf" } ] }
```

### 6.2 `GET /api/ess/v1/payslips/{name}`
```json
{
  "summary": { "...": "identical to 6.1" },
  "earnings": [ { "component": "Basic Salary", "amount": 6500000 }, { "component": "Transport & Meal Allowance", "amount": 2000000 } ],
  "deductions": [],
  "workingDays": 21, "payableDays": 21, "unpaidLeaveDays": 0,
  "employeeContributions": 260000, "pph21": 153914, "otherDeductions": 0,
  "bpjs": { "jht": 130000, "jp": 65000, "kesehatan": 65000 },
  "ptkpStatus": "TK/0", "terCategory": "A"
}
```

### 6.3 PDF Download
`GET {pdfUrl}` with Bearer authorization header → `application/pdf`. Store in client app-private sandbox (not public device gallery) and avoid caching in shared storage.

## 7. Supervisor & HR Approvals

One inbox serves every approver: line supervisors, project approvers, the shift-swap colleague and HR. What appears is
driven by the workflows, not by a hard-coded list: a request is listed when the authenticated user has a workflow action
on it in its current state (the same rule as the web desk's decision queue). Covered request types: `LeaveApplication`,
`OvertimeRequest`, `ShiftSwapRequest`, `AttendanceCorrection`, `FaceRegistration`, `BiodataChangeRequest`,
`ExpenseClaim`, `EmployeeAdvance`. Nobody sees their own requests.

| Stage | Who | Examples |
|---|---|---|
| `supervisor` | The approver set on the request (leave approver, overtime/correction approver, swap manager) | Leave, overtime, attendance correction (first step), shift swap (after both employees agree), expense claim, advance |
| `project` | The project approver | Overtime charged to a project |
| `peer` | The colleague asked to swap | Shift swap |
| `hr` | HR roles (`HR-Time-Attendance`, `HR-Employee-Management`) | Face registration (Verify), attendance correction (second step), biodata change |

HR can also decide a request still waiting for the supervisor (an *override*, e.g. when the supervisor is away). Overrides
are hidden by default so the HR inbox only shows HR's own work; pass `includeOverrides=true` to list them
(`isOverride: true`). Override actions require a comment.

### 7.1 `GET /api/ess/v1/approvals?stage=hr&includeOverrides=false`
Both parameters are optional. `counts` always covers the whole inbox (for tab badges); `items` is filtered by `stage`.
```json
{ "counts": { "supervisor": 3, "hr": 2 },
  "items": [ {
  "doctype": "AttendanceCorrection", "doctypeTitle": "Attendance Correction", "name": "ACR-2026-000012",
  "employee": "EMP-0001", "employeeName": "Ahmad Fadilah",
  "title": "Attendance correction 2026-11-18: Missing clock-out", "amount": null, "reason": "Phone battery died",
  "state": "Pending HR Approval", "stage": "hr", "isOverride": false, "submittedAt": "2026-11-18T02:10:00+00:00",
  "actions": [
    { "action": "Approve (HR)", "kind": "approve", "toState": "Approved", "requireComment": false },
    { "action": "Reject (HR)", "kind": "reject", "toState": "Rejected", "requireComment": true } ]
} ] }
```
`actions` lists the workflow actions this user may take; `kind` tells the app which button to draw.

### 7.2 `GET /api/ess/v1/approvals/{doctype}/{name}`
Detail screen: `{ "summary": { ...same as an item above... }, "details": { ... } }`. `details` depends on the type:
face registration returns the photo URLs (`/files/{id}`, open with the bearer token), the capture quality scores and
consent; attendance correction returns the requested times next to the recorded punches; biodata change returns each
field with its current and requested value; leave, overtime and shift swap return their request fields; attachments are
returned as file URLs. 404 if the request is not in the user's inbox.

### 7.3 `POST /api/ess/v1/approvals/{doctype}/{name}/{decision}`
`decision` = `approve` or `reject`. Body: `{ "comment": "...", "action": null }`.
- The server maps the decision to the workflow action available to the user: `approve` becomes `Approve`,
  `Approve (HR)`, `Verify` (face registration) or the project approval, whichever applies. When more than one matches,
  the supervisor's own action wins over the HR override.
- `action` (optional) picks a specific action from `actions`, e.g. `"Approve (HR)"` for an override.
- `comment` is mandatory when the action has `requireComment` (all rejections and HR overrides) → 422 otherwise.

200 → `{ "doctype": "...", "name": "...", "state": "Pending HR Approval", "docstatus": 1 }`.
403 when the user has no such action (e.g. their own request, or someone else already decided); 409 on a concurrent
decision → the client reloads the list.

What happens next: an approved attendance correction moves to the HR stage and, after HR approval, is applied to the
attendance; a verified face registration becomes the active template; an approved biodata change is written to the
employee record; expense claims and advances go to Finance verification on the web desk.

### 7.4 Notifications
The approver gets an inbox notification when a request reaches their stage (the supervisor or project approver by
employee link, HR by role), and the requester when it is approved or rejected. Notifications carry `docType`/`docName`
so the app can open the approval detail directly.

## 8. General Document API Integration (Transitional)

Overtime requests, expense claims, and employee advances can interact via the general document API (`/api/data`) using the same bearer token; row-level security scopes access to the employee's own records.

```http
POST /api/data/OvertimeRequest
{ "employee": "EMP-0001", "overtime_date": "2026-11-18", "day_type": "Working day", "hours": 2, "reason": "System deployment" }
→ 201, Header ETag: "1"

POST /api/data/OvertimeRequest/OT-2026-000010/actions/Submit%20for%20approval
If-Match: "1"
{ "comment": null }
```
DocType schema metadata: `GET /api/meta/OvertimeRequest` (also `ExpenseClaim`, `EmployeeAdvance`). Claim receipts attachment:
`POST /api/data/ExpenseClaim/{name}/attachments` (multipart). Dedicated ESS convenience endpoints for these three DocTypes are planned for v1.1.

## 9. ERP Configuration (HR Setup)

1. **Location**: Configure *Geolocation* (latitude, longitude), check *Allow mobile attendance*, specify *Attendance radius (m)* (empty defaults to HR Settings).
2. **HR Settings › Mobile attendance (ESS)**: Enforce geofencing rejection, default radius, maximum GPS accuracy, mock location rejection, single device binding, selfie enforcement, max offline lifespan, minimum punch interval, and night shift window.
3. **Employee › Job › Schedule**: *Work location*, *Shift type*, *Mobile attendance* (mode), *ESS registered phone* (clear to allow re-binding new phone).
4. **User Accounts**: Assigned the **Employee** role with claim `employee` = employee ID. The HR Settings toggle *Create a user account when an employee is created* provisions accounts automatically (username = work email). The Employee role is strictly restricted to self-service master read permissions (excluding Customer, Contact, Address, price lists, financial, CRM, and stock configurations).
5. **Attendance Aggregation**: DocFrame `Attendance` documents are materialized automatically by the background job `Erp.EssAttendanceAggregation` (every 5 minutes). Employees cannot directly author/modify Attendance records from the desk UI; manual adjustments are restricted to HR. Flagged punches populate *Punch review notes*.

## 10. Capacity & Operational Architecture

- Punch ingestion = single lean `INSERT` statement into `ess_attendance_punches` (bypassing heavy DocType lifecycle hooks, series number generators, and audit log pipelines). Benchmark on PostgreSQL 16 (32 concurrent connections): ~9,000 punches/second. A morning rush peak of 6,000 employees punching within a 5-minute window operates well within capacity; scaling considerations primarily involve application container replicas and DB connection pooling rather than table lock contention.
- Per-user rate limiting by tier: reads `Api:RateLimitPerMinute` (300/min), writes `Api:WriteRateLimitPerMinute` (120/min); see `rate-limiting.md`. Login/refresh endpoints are throttled per IP with a higher threshold `Api:AuthRateLimitPerMinute` (default 1,200/min per node) to accommodate multiple employees sharing corporate office Wi-Fi or carrier CGNAT IPs.
- Background aggregator batch size: `Ess:AggregationBatchSize` (default 1,000 employee-days per 5-minute cycle).
- Time zone: `Ess:TimeZone` (defaults to system application zone, `Asia/Jakarta`).
- Go-live load testing recommendation: Execute load simulation (e.g. k6) simulating 6,000 punches within 5 minutes plus a 17:00 clock-out surge, including replay retries and intermittent offline network drops.

## 11. Versioning & Lifecycle

- `v1` is backwards-compatible: newly added properties will be additive (clients should ignore unrecognized fields); breaking changes will be versioned under `v2`.
- Mobile clients should supply `appVersion` with every punch payload to assist HR and IT support in diagnosing outdated client builds.

## 12. Self-Service Requests

All requests are created **as the employee** and, unless `"submit": false`, submitted at once. Permissions, workflow
conditions and validation are the same as on the web desk; a 422 carries the server's field errors. Each list returns
the employee's own documents, newest first; `withdraw` works while the request is pending.

| Request | Endpoints | Approval |
|---|---|---|
| Face registration | `GET /face`, `GET /face/template`, `POST /face/registrations`, `POST /face/registrations/{name}/withdraw` | HR (HR-Time-Attendance) verifies |
| Time off | section 5 | Leave workflow |
| Shift change (swap) | `GET/POST /shift-swaps`, `POST /shift-swaps/{name}/accept\|decline\|withdraw` | Colleague, then manager |
| Attendance correction | `GET/POST /attendance/corrections`, `POST /attendance/corrections/{name}/withdraw` | Line approver, then HR (HR-Time-Attendance) |
| Overtime | `GET/POST /overtime`, `POST /overtime/{name}/withdraw` | Project approver (if any), then line approver |
| Biodata change | `GET /profile/biodata`, `GET/POST /profile/change-requests`, `POST /profile/change-requests/{name}/withdraw` | HR (HR-Employee-Management) |
| Attachments | `POST /files/{doctype}/{field}` | — |

### 12.1 Face registration

Face recognition runs **on the phone**: the app computes a face template (embedding) with its model, the server stores
it **encrypted** (ASP.NET Data Protection) together with 1–3 photos for HR, and returns it only to the employee's own app.

`POST /api/ess/v1/face/registrations` — `multipart/form-data`:

| Field | Required | Content |
|---|---|---|
| `photo1` … `photo3` | 1–3 files | JPEG/PNG/WebP face photos, max 2 MB each, shown to HR when verifying |
| `template` | yes | JSON array of 32–4096 numbers, e.g. `[0.0123, -0.0456, …]` |
| `consent` | yes | `true`: the employee agreed to biometric processing (UU 27/2022 PDP); stored with the time |
| `modelName`, `modelVersion` | recommended | e.g. `MobileFaceNet`, `1.2`; the app must match with the same model |
| `qualityScore`, `livenessScore` | optional | 0–1 values from the capture |
| `deviceId`, `appVersion` | optional | |

201 → `{ "name": "FACE-2026-00012", "state": "Pending Verification", "active": false, "photos": 2, "modelName": "…", "modelVersion": "…", "sha256": "…", "dimensions": 128, "verifiedAt": null }`.

HR verifies on the desk (*Face Registration* › **Verify** or **Reject** with a reason). Verifying makes it the active
registration; the previous one is revoked. HR can **Revoke** an active registration.

`GET /api/ess/v1/face` → `{ "required": true, "minScore": 0.8, "rejectMismatch": false, "status": "active|pending|rejected|none", "active": {…}, "registrations": [ … ] }`.

`GET /api/ess/v1/face/template` → `{ "registration": "FACE-2026-00012", "modelName": "…", "modelVersion": "…", "sha256": "…", "template": [ … ] }`
(404 without a verified registration; `Cache-Control: no-store`). Keep the template in the phone's secure storage and
refresh it when `sha256` or `registration` in `GET /face` changes.

### 12.2 Shift change

`POST /shift-swaps` with `{ "targetEmployee": "EMP-0007", "swapDate": "2026-11-25", "targetSwapDate": null, "reason": "…", "submit": true }` creates the request. The colleague answers with
`POST /shift-swaps/{name}/accept` or `/decline` (`{ "comment": "…" }` required to decline); the requester can
`/withdraw`. The manager approves on the desk or through approvals.

### 12.3 Attendance correction

```json
POST /api/ess/v1/attendance/corrections
{ "date": "2026-11-19", "correctionType": "Missing clock-out", "checkIn": null, "checkOut": "2026-11-19T17:05",
  "reason": "Phone battery empty", "attachmentFileId": null, "submit": true }
```

- `correctionType`: `Missing clock-in`, `Missing clock-out`, `Missing both`, `Wrong clock-in time`, `Wrong clock-out time`, `Wrong times`.
- Times are local (application time zone) or carry an offset. Check-in on the date (from 18:00 the day before, for
  night shifts); check-out on the date or the next morning. At least one time is required.
- Only today or past days; refused when the month's payslip is submitted; one open correction per day.
- 201 → `{ "name": "ACR-2026-000031", "date": "…", "recordedCheckIn": "…", "recordedCheckOut": "…", "recordedCode": "NCOUT", "checkIn": null, "checkOut": "…", "approver": "EMP-0002", "state": "Pending Approval", … }`.
- When HR approves, the attendance of that day gets the corrected times (created if missing), the mobile-punch review
  notes are cleared and the attendance code is recalculated (`docs/guides/attendance-codes.md`).

### 12.4 Overtime

```json
POST /api/ess/v1/overtime
{ "date": "2026-11-20", "hours": 2, "dayType": null, "project": null, "reason": "Month-end closing", "submit": true }
```

`dayType` defaults to *Working day* (a scheduled rest day or holiday is detected from the roster). With a `project`
the project approver approves first. 201 → `{ "name": "OT-2026-000120", "date": "…", "hours": 2, "weightedHours": 3.5, "approver": "…", "state": "Pending Approval", … }`.

### 12.5 Biodata change

`GET /api/ess/v1/profile/biodata` lists the categories, their fields (with options, or columns for tables) and the
current values.

| Category | Fields | Supporting document |
|---|---|---|
| `Contact & address` | `mobile`, `personal_email`, `current_address`, `permanent_address`, `emergency_contacts` (table) | no |
| `Marital status & dependents` | `marital_status`, `ptkp_status`, `dependents` (table) | required |
| `Bank account` | `bank_accounts` (table) | required (bank book) |
| `Identity numbers` | `nik`, `npwp`, `bpjs_health_no`, `bpjs_employment_no`, `identity_documents` (table) | required |

```json
POST /api/ess/v1/profile/change-requests
{ "category": "Bank account",
  "changes": { "bank_accounts": [ { "bank": "BCA", "account_no": "8800123456", "account_holder": "Rina Wati", "is_default": true } ] },
  "reason": "New payroll account", "attachmentFileId": "f_…", "submit": true }
```

A table value replaces the whole table. Values are checked (options, e-mail, NIK of 16 digits, known columns);
other fields are refused. When HR approves, the employee record is updated.

### 12.6 Attachments

`POST /api/ess/v1/files/{doctype}/{field}` (`multipart/form-data`, field `file`, max 10 MB, PDF/Office/image) →
`{ "fileId": "f_…", "fileName": "…", "size": 12345 }`. Allowed: `LeaveApplication/attachment`,
`AttendanceCorrection/attachment`, `BiodataChangeRequest/attachment`. Send the `fileId` in the request; the file is
visible only to the uploader until the request is saved.
