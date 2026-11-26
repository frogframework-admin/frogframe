# Changelog

Notable changes to FrogFrame. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/):

- **MAJOR**: a change that needs action on upgrade, such as a removed API, a breaking metadata format change, or a migration that is not safe during a rolling update.
- **MINOR**: new features and new optional settings.
- **PATCH**: fixes only.

Releases are tagged `vMAJOR.MINOR.PATCH` on `main`. Add every user-visible change to **Unreleased** in the same pull request. When releasing, rename *Unreleased* to the version and date, and start a new empty *Unreleased* section.

## [Unreleased]

### Added
- Payroll formulas (`docs/guides/payroll-formulas.md`): salary components can be calculated by **formula** instead of a fixed amount, so each company configures its own pay rules without code.
  - Salary Component: *Calculation* (Fixed / Formula), *Applies when* condition (eligibility, e.g. only sales job profiles), *Formula*, *Company*, and *Variable name* (abbreviation widened to 30 characters) to refer to other components (`GAPOK * 10 / 100`). Formulas read `emp.*` (Employee fields plus grade level, job level, manager flag, years/months of service, age, children), `att.*` (attendance summary, including the new *Days present*), and `period.*`, with `if`, `min`, `max`, `floor`, `ceil`, `div`, `lookup`, `lookup_range`. Syntax, unknown names and loops are checked when the component is saved; payslip rows record the formula and the values used.
  - New DocType **Salary Rate Table** (child **Salary Rate Row**): keyed and ranged rates with *valid from* versions, e.g. basic salary per grade, allowance per level, length-of-service steps. Menu *Payroll › Salary Rate Table*.
  - **Pay cut** deductions (e.g. absence or lateness cuts) lower the taxable gross, regular pay and BPJS base according to their flags; their GL account defaults to the salary expense account.
  - **Tax allowance (gross-up)**: *PPh 21 method* on the salary assignment and *Tax allowance component* in Payroll Settings; the payslip solves the allowance that equals the PPh 21 it carries (TER and last-period Article 17), shown on 1721-A1 line 2.
  - Overtime hourly wage now uses the payslip's full-month earnings (including formula components) instead of only the salary assignment rows.
- **360-degree feedback** (`docs/guides/360-feedback.md`): Feedback Template (questions per competency, rating 1–5 or comment, minimum peers/direct reports, anonymity threshold), 360 Feedback per employee and cycle (employee nominates raters, manager approves, raters answer, HR closes), *Launch 360 feedback* on the Appraisal Cycle, reminders, results per rater group (self, manager, peers, direct reports, internal customers, all others, gap) that never show a group smaller than the anonymity threshold, comments without names, *360 (others)* on the appraisal's competencies, report *360 Feedback Summary* (`erp-360-feedback`) and ESS endpoints `/api/ess/v1/feedback/…`. Single responses are visible only to the rater (and HR-Talent read-only).
- **Switch tenant** for group administrators (`docs/guides/tenants.md`): a user in `Platform:Administrators` can work in any active tenant of the group with their own account (profile menu › *Switch tenant*), as System Manager of that tenant, with a bar on every page and *Back to my tenant*. The choice lives in an encrypted session cookie bound to the user id; it is checked on every request (still a platform administrator, tenant still active), logged, and never applies to API tokens or API keys.
- Metadata list (`/admin/metadata`): column *Source / Status* shows Application or Custom, *Customized vN* and *Needs attention* (customization partly switched off after an update). The detail panel of an application DocType shows the customization history and points to the visual editor instead of a full-JSON draft.
- DocType Editor asks before leaving with unpublished changes (links, reload, closing the tab).

### Security
- With more than one tenant, only group administrators can change the shared metadata (publish or customize DocTypes, reset customizations, create DocTypes, publish workflows, save DocType permissions in *Roles & Permissions*, `POST /api/admin/metadata/publish`). Other System Managers see these pages read-only. `Platform:TenantAdminsCanEditMetadata=true` keeps the old behaviour.

### Fixed
- Two administrators customizing the same application DocType no longer overwrite each other: saving or resetting checks the customization version the editor started from and is refused with a clear message (API: `expectedCustomizationVersion`, 409).
- Metadata detail panel: a JSON draft being typed is no longer reset when any DocType is published elsewhere; the version shown for application DocTypes is the application definition plus the customization version.
- New DocType form: "Module" label in English like the rest of the page.
- Home page "My team today" and "Who is away" (`docs/guides/home-page.md`): managers see their direct reports with today's status (on leave, day off, present/late with clock-in time, absent, not checked in) and their team's time off and day off today and in the next 7 days; HR roles see who is away in the whole company.
- HRM review follow-up (24 Nov 2026):
  - **9-Box Talent Grid** report (`erp-nine-box`) per appraisal cycle, unit (with sub-units) and calibration session.
  - **Announcement acknowledgments**: "I have read this" on mandatory announcements, with who and when (`AnnouncementAcknowledgment`) and a count; HR cannot alter them by editing. Announcements reach sub-units (option), respect the target company and use the employee's current unit.
  - **Disciplinary Action**: "valid until" defaults to the issue date + HR Settings `warning_validity_months` (6) for SP1–SP3, status Active/Expired, advisory escalation check (SP2 without active SP1, SP3 without active SP2).
  - **Offer needs an interview**: a Job Offer requires a passed interview with feedback or scorecard scores, unless HR Settings `allow_offer_without_interview` is on.
  - Menus for Announcement, Probation Evaluation, Disciplinary Action and Calibration Session.
  - `IFileStore.DeleteAsync` (in-memory, PostgreSQL, object storage) to delete a single file.
- Careers portal API (`docs/guides/careers-portal-api.md`): a public, sign-in-free API `/api/careers/v1/{tenant}` for a separate careers website. New DocType **Job Posting** (what applicants see of an approved requisition: title, location, publication window, sanitized description; one published posting per requisition; status Live/Scheduled/Closed/Unpublished). Applying stores the CV and e-mails a confirmation link; only a confirmed application becomes a Job Applicant (source *Company website*, linked to the posting, consent time and privacy notice text stored). Applicants check status and withdraw through a signed e-mail link, without an account; internal stages are grouped and rejection reasons are never shown. HR Settings section *Careers portal* (on/off, portal address, privacy notice, reply-to). Per-IP rate limits (`Careers:ReadPerMinute`, `Careers:SubmitPerMinute`), CORS for the portal address, a honeypot field and an optional siteverify captcha (`Careers:Captcha:*`, Turnstile/hCaptcha/reCAPTCHA). Menu *Talent & Recruitment › Job Posting*.
- Position template per organization unit type (`docs/guides/position-templates.md`): a unit type lists job profiles and how many positions each unit gets (one head line). A new active unit with staffing model *Position* gets its positions automatically (head first, staff report to the head, the head reports to the parent unit's head, the unit's head position is set). *Sync positions to units* on the unit type adds new lines to existing units and closes vacant auto-created positions above a lowered quantity; filled positions are reported, never closed. New child DocType `OrganizationUnitTypePosition`; new Position field *Created from unit type template*.
- ESS self-service API (`docs/guides/ess-mobile-api.md` section 12):
  - face registration for on-device face recognition: photos for HR, face template stored encrypted (Data Protection) and returned only to the employee's app, HR verification, one active registration per employee, consent recorded (UU PDP); optional face match on every punch (HR Settings: required, minimum score, reject or flag);
  - attendance correction (new DocType, line approver then HR); on approval the day's attendance gets the corrected times and its code is recalculated;
  - overtime requests (create, list, withdraw) and shift-swap answers (accept, decline, withdraw);
  - biodata change requests (new DocType) for contact & address, marital status & dependents, bank account and identity numbers, approved by HR and then applied to the employee;
  - attachment upload for leave, correction and biodata requests.
- Approval review on the desk forms of face registration, attendance correction and biodata change (`docs/guides/form-panels.md`): photos next to the employee photo and the registration being replaced; recorded vs corrected times with a preview of the resulting attendance code and the day's mobile punches (refused ones included); current vs requested biodata, tables row by row; automatic checks (consent, duplicate template or phone, paid month, repeated corrections, data changed since the request, duplicate NIK, missing supporting document) and who the request is waiting for.
- Form panels: `services.AddFormPanel<TComponent>(docType)` adds an application component above the tabs of a DocType's form.
- ESS approvals for supervisors and HR (`docs/guides/ess-mobile-api.md` section 7): one workflow-driven inbox with stages (supervisor, project, peer, HR) and per-stage counts, a detail endpoint (photos, requested vs recorded values, attachments), decisions mapped to the right workflow action (`Approve`, `Approve (HR)`, `Verify`, project approval) with an optional explicit `action` for HR overrides; HR now approves face registrations, attendance corrections and biodata changes from the app; approvers and requesters get inbox notifications.
- Attendance codes and monthly recap (`docs/guides/attendance-codes.md`):
  - every Attendance record gets a code: ONT (on time), LI (late in), LO (early out), LI/LO, NCIN, NCOUT, ABS, TO-ND (paid time off), TO-D (time off with salary deduction, from the leave type's *Paid* flag), DO (day off) and INV (punch outside the shift window, flagged mobile punch, or work on a day off without approved overtime); early-out minutes are recorded next to late minutes;
  - an hourly job creates the daily record also for days without punches (ABS, DO, time off) and decides missing clock-in/out after the scheduled end + 4 hours (HR Settings);
  - HR can set a code by hand (*Code set by HR*); the system then leaves that day alone;
  - `/hr/attendance-recap`: days per code, late and early-out minutes per employee and month, Excel/CSV export and *Recalculate month*;
  - payslips get an *Attendance summary* section with the same counts; the ESS attendance API returns `code`, `codeFinal` and `earlyOutMinutes`.
- Tenant management for a group of companies (`/platform/tenants`, see `docs/guides/tenants.md`):
  - tenant registry `frogframe_tenants` (migration 045; existing tenants with users are registered as active);
  - group administrators listed in `Platform:Administrators` (`FROGFRAME_PLATFORM_ADMINS`) create a tenant with its first System Manager, edit settings, suspend and reactivate tenants, and see users, documents and last activity per tenant;
  - a suspended tenant cannot sign in; its sessions end, API calls get 403 and its scheduled jobs stop;
  - settings per tenant: default language, maximum active users, roles that must use two-factor authentication.
- Home page (`/`), see `docs/guides/home-page.md`:
  - "Needs your action": documents waiting for the user's workflow decision, grouped by module, oldest first, with how long they have waited and the workflow buttons on the row. Actions that require a comment ask for the reason first.
  - Compact header with date, summary chips (approvals, overdue or due-today tasks, unread notifications) and "Create new" shortcuts limited to DocTypes the user may create.
  - Side column: "single number" widgets of the default dashboard, running background jobs, bookmarks and followed documents.
  - Banner suggesting two-factor authentication to sessions without a second factor; each user can hide it.
  - Extension points for applications: `AddHomePanel<TComponent>(id, HomeSlot, order, visible)` and `AddHomeQuickAction(...)`.
  - ERP panels: "My day" (shift, check-in/out, leave balance, latest payslip; check-in from the browser only when the attendance policy needs no location, device or photo), "My requests", company info (next holiday, colleagues on leave today) and a setup checklist for administrators.
- DocType editor: a search box above the control palette filters the layout controls and field types by name, type or common words (English and Indonesian, e.g. "angka", "tanggal", "lampiran"). Enter adds the first match to the selected section; Escape clears.
- `scripts/codespaces-demo.sh`: starts the demo with Docker Compose in GitHub Codespaces (or locally). It prepares `.env` (random secrets, Codespace URL and allowed hosts), publishes, builds, starts and waits until ready. The README's Codespaces instructions now use it; `dotnet run` alone needs PostgreSQL.
- Alternate list views: Kanban board (drag and drop between workflow states), calendar (month/week/day), and tree view for hierarchical masters.
- Roster calendar quick actions: generate roster, export (.xlsx/.csv), and shortage indicators per shift.
- Ad-hoc report builder: choose columns, filters and aggregates, and save named reports.
- Row-level sharing: per-document Read/Write/Share grants (`frogframe_doc_shares`).
- Visual workflow editor with preview, diff and publish.
- Letterhead and print format designer (`/admin/print-designs`). Formats offer a choice of columns, paper and orientation, and two layout options: *Two lines* (each label above its value) and *Show tables as records*. The document type picker can be filtered.
- Secret files: settings can be read from `/run/secrets` (Docker/Kubernetes secrets, Key Vault or Vault CSI).
- Database migrations as a separate step: `--migrate-only`, `--migration-status`, `Database:MigrateOnStartup`. Docker Compose runs `db-backup` → `migrate` → `app`, with a verified snapshot before migrating.
- Restore drill script (`deploy/scripts/restore-drill.sh`): restores into a throwaway database, verifies row counts and checksums, and checks the RTO.
- Repository templates: CODEOWNERS, pull request and issue templates, Dependabot, `SECURITY.md`.
- Background jobs (`/jobs`):
  - long imports (automatic from 500 rows) and bulk submit/cancel/delete run in the background;
  - live progress, cancel, run again, output files and a notification when a job ends;
  - safe with several instances.
- Release pipeline:
  - `release.yml`: image, smoke test, GHCR, staging, production with approval;
  - `deploy.yml`: deploy, smoke test, automatic rollback;
  - `deploy/scripts/deploy.sh` and `smoke-test.sh`;
  - `FROGFRAME_IMAGE` in compose to run released images.
- DocType `indexes` (PostgreSQL): composite and prefix indexes declared in metadata. Added for `EmployeeDailySchedule` (employee + work date) and `OrganizationUnit` (`materialized_path` prefix).
- `startswith` filter operator (case-sensitive prefix) in the API, list filters and the report builder.
- Tags, bookmarks and follow on documents: shared colour-coded tags (filterable in lists, `?tag=`), personal bookmarks and a `/bookmarks` page, and follow with in-app notifications of other users' changes.
- Granular ERP roles (see `docs/guides/roles.md`): 29 roles across HR, CRM, Finance, Accounting, Procurement and Stock replace the broad roles in DocType permissions, workflow transitions and form buttons. `HR`, `Payroll`, `Finance`, `Sales`, `Purchasing` and `Stock` are now role groups: holding one grants all of its roles, and holding a role counts as the group for menus, dashboards and notification rules, so existing accounts keep working. The user page warns about segregation-of-duties conflicts; startup logs metadata that still names a group.
  - **Behaviour changes:** payroll runs and payslips are changed only by HR-Payroll (Finance reads them); stock entries and counts only by Stock-Keeper / Stock-Opname (Procurement reads them); supplier bank accounts only by Finance-Admin (field now permission level 1); exceeding a blocked credit limit only by Finance-Admin.
- Database role segregation (see `docs/guides/database-roles.md`): requests run as `frogframe_app`, which can only read and write data (no DDL, no TRUNCATE, ledger append-only); the owner `frogframe` runs migrations, indexes and reporting views. The migrate step creates the role and its grants (`Database:AppRole`, `Database:AppRolePassword`); startup logs a warning when the runtime role is a superuser or owns tables.
  - **Upgrade:** add `FROGFRAME_APP_PASSWORD` to `.env` before the next `docker compose up` (Compose refuses to start without it).
- Tiered rate limits (see `docs/guides/rate-limiting.md`): sign-in (now also the browser login and 2FA forms) and password reset per client IP; API reads, writes and heavy requests (reports, saved queries, print) per user or API key, each with its own budget. New settings `Api:WriteRateLimitPerMinute` (120), `Api:HeavyRateLimitPerMinute` (60), `Api:PasswordResetRateLimitPerMinute` (20); `429` responses carry the real `Retry-After` and the tier, and are logged.
- Two-factor authentication and single sign-on (see `docs/guides/two-factor-and-sso.md`):
  - authenticator-app codes with QR enrolment and ten one-time recovery codes (`/account/two-factor`), also on `POST /api/auth/login` (`twoFactorCode`, `twoFactorRecoveryCode`);
  - `Security:TwoFactor:RequiredForRoles` makes it mandatory for roles; administrators can reset it for a user who lost their phone;
  - "Continue with Google" and "Continue with Microsoft" (Entra ID, single directory) sign in existing accounts by verified e-mail; optional allowed e-mail domains;
  - migration `042_two_factor.sql`.

### Changed
- Approval delegation job (`Erp.ApprovalDelegationRefresh`): when the approver of a pending document has left and there is no Acting (Plt.), the document moves to the requester's current approver; when the line approver moved and no longer is the requester's line approver (job change, reorganization), the document moves to the current line approver (an active Acting for that approver keeps it). Audit action *Approver reassigned*; the new approver and the requester get an inbox notice.
- Attendance: the status follows the attendance code (ABS → Absent, time off → On leave, new status *Day off*). Because absent days are now recorded automatically, payroll's *Prorate for absence* deducts working days without punches; review the monthly recap before running payroll.
- Home page loads faster: the approval list queries only the workflow states the user can decide, store queries that need only rows skip the total count (`StoreQuery.CountTotal`, also used by the ERP `Masters` helper), DocTypes are queried in parallel, sections appear as their data arrives, and KPI widgets run in parallel with a 3-second limit and a 5-minute cache. With 63,000 documents the approval list went from 57–148 ms to 2–3 ms. See `docs/guides/home-page.md`.
- Home page: users with decisions waiting see them first; other users see their own day first. Recent documents show type, party or title, amount and status (the "Available actions" column and the arbitrary "View all" link are gone). The card "Tasks & unread notifications", which counted only notifications, is replaced by the summary chips.
- ESS leave balances are computed in one place (`EssLeave.BalancesAsync`) for the ESS API and the home page; the API response is unchanged.
- Docker Compose sets `TZ` for the `app` and `migrate` services (`FROGFRAME_TZ`, default `Asia/Jakarta`).
- `TODO.md` and the production-readiness checklist now match the code: fail-fast startup checks, report builder, row sharing, board/calendar/tree views, visual workflow editor and print designer are marked done; observability and post-deploy smoke tests are marked partly done.
- Write performance (PostgreSQL):
  - each application instance uses a connection pool of 30 unless the connection string sets `Maximum Pool Size` (was Npgsql's 100);
  - master documents are read once per save;
  - the DocType lock, schema check, audit and outbox writes are batched.
  - Under a 100-user burst, p95 went from 1,299 ms with errors to 292 ms without errors. See `docs/guides/performance.md`.
- Load test (`tests/load/k6-baseline.js`) sends the current Purchase Request fields.
- `docker-compose.yml` defaults to Development during development. Setting `ASPNETCORE_ENVIRONMENT=Production` turns demo mode off.
- Printing: wide child tables fit the page. Default columns skip check boxes and duplicated `*_name` fields, wide tables use a denser type size, and long values wrap.

### Security
- Authenticator secrets are encrypted with Data Protection and recovery codes are stored as SHA-256 hashes. Only the authenticator app counts as a second factor (the e-mail and SMS code providers are off).
- Two-factor security events are logged (enable, disable, new recovery codes, recovery-code sign-in, failed codes, administrator reset), and the password confirmation on `/account/two-factor` counts towards the account lockout.
- Outside Development the application refuses to start when demo mode is on, unless a dedicated demo server opts in with its own password.
- Outside Development the application refuses to start while a secret in use is a `change-me` placeholder, a repository default, or shorter than 12 characters.

### Fixed
- Home page "On leave today (your team)" also listed leave requests still waiting for approval; it now lists approved leave only.
- HRM review follow-up (24 Nov 2026):
  - Applicant retention job used fields that do not exist and stopped on the first applicant: it now anonymizes applicant, stage-history notes and interview feedback in one write, deletes the CV file, records `anonymized_at`, and counts the retention period from the date of rejection/withdrawal.
  - "Create employee" from an offer failed after the employee was created (onboarding list field `boarding_begins_on`): the onboarding list is now created with `start_date`, and a failure is reported in the message.
  - "Propose Job Change" on an appraisal always failed (no new value): it now opens a prefilled Job Change form. The `has_change` rule accepts a new company or a salary-only adjustment.
  - Job Change salary used fields that do not exist on Salary Assignment and failures were swallowed: it now uses `from_date` and an earnings row (component from the Job Change or Payroll Settings); failures go to HR as a follow-up notice.
  - Talent records were visible to managers in lists, reports and exports even with manager access off: the Manager permission on Talent Review and Succession Plan is now scoped to the stored `manager_viewer`; Development Plans are visible to the direct manager.
  - Probation Evaluation: only the direct manager or HR can evaluate; a failed probation created a separation with an invalid type ("Termination" → "PHK"); the reminder now also covers an extended probation.
  - Requisition headcount check counted filled and cancelled requisitions; it now counts openings still to fill of pending and approved requisitions.
  - Requisition workflow: rejecting at the unit-head step needs the unit head or HR, approving with no unit head needs HR, and a pending requisition cannot be changed by the requester (HR can).
  - Recruitment metrics used the last change date for "days to hire"; it now uses the date of the Hired stage. The new-applicant notice showed an empty name.
  - Calibration: reviews must match the session's cycle and unit; a finalized session locks scores too and accepts no new reviews.
  - Removed the duplicate `CompetencyCatalog` DocType (levels live in `Competency.proficiency_levels`).
- When an employee leaves (Employee Separation) or a manager changes position (Job Change), their direct reports kept the old supervisor, so new leave and expense requests went to someone who had left or moved. Their supervisor is now recomputed from position and unit, the leave and expense approvers follow (manual overrides to other people are kept), and HR (HR-Employee-Management) gets an inbox notice for employees left without a supervisor.
- ESS approvals list no longer misses requests whose pending state is already submitted (overtime, attendance corrections), shows shift swaps waiting for the manager, and no longer fails on project or HR steps by always sending `Approve`/`Reject`; the home *my pending requests* count includes the same requests.
- Home page greeting, date and My Tasks "due today" / "overdue" use the application time zone instead of the server clock (UTC in a container without `TZ`), and the greeting has four periods (Indonesian "Selamat sore" between 15:00 and 18:00).
- DocType editor: a "Tab in section" is now drawn inside its section card (tab strip, then the selected tab's fields), as in Preview and the form, instead of as a separate card below the section. Fields can be dropped on a tab in the strip to move them into it.
- Bursts of saves no longer fail with "too many clients already" (surfacing as "An error occurred while executing document hooks").
- Startup no longer retries non-transient PostgreSQL errors (wrong password, unknown database) for minutes; it fails at once.
