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
        "workingHours": 8.04, "lateMinutes": 0, "leaveApplication": null, "source": "Mobile app", "notes": null
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
- Leave types requiring medical/supporting certificates (`requiresAttachment`) are routed to HR/web desk in v1.

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

## 7. Manager Approvals

### 7.1 `GET /api/ess/v1/approvals`
Subordinate requests pending approval by the authenticated user (leave, overtime, expense claims, advances), sorted oldest first.
```json
{ "items": [ {
  "doctype": "LeaveApplication", "name": "LV-2026-000045", "employee": "EMP-0001", "employeeName": "Ahmad Fadilah",
  "title": "Annual Leave 2026-12-22 to 2026-12-24 (3 days)", "amount": null, "reason": "Year-end vacation",
  "state": "Pending Approval", "submittedAt": "2026-11-18T02:10:00+00:00", "actions": ["approve", "reject"]
} ] }
```

### 7.2 `POST /api/ess/v1/approvals/{doctype}/{name}/{decision}`
`decision` = `approve` or `reject`. Request body: `{ "comment": "..." }` (mandatory for `reject`).
200 → `{ "doctype": "...", "name": "...", "state": "Approved", "docstatus": 1 }`.
403/409 if already handled by another supervisor or if user is not authorized → client reloads the approval list.
Expense claims and employee advances transition to Finance verification (web desk) upon supervisor approval.

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
- Per-user rate limiting: `Api:RateLimitPerMinute` (default 300/min). Login/refresh endpoints are throttled per IP with a higher threshold `Api:AuthRateLimitPerMinute` (default 1,200/min per node) to accommodate multiple employees sharing corporate office Wi-Fi or carrier CGNAT IPs.
- Background aggregator batch size: `Ess:AggregationBatchSize` (default 1,000 employee-days per 5-minute cycle).
- Time zone: `Ess:TimeZone` (defaults to system application zone, `Asia/Jakarta`).
- Go-live load testing recommendation: Execute load simulation (e.g. k6) simulating 6,000 punches within 5 minutes plus a 17:00 clock-out surge, including replay retries and intermittent offline network drops.

## 11. Versioning & Lifecycle

- `v1` is backwards-compatible: newly added properties will be additive (clients should ignore unrecognized fields); breaking changes will be versioned under `v2`.
- Mobile clients should supply `appVersion` with every punch payload to assist HR and IT support in diagnosing outdated client builds.
