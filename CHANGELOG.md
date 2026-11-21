# Changelog

Notable changes to FrogFrame. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/):

- **MAJOR**: a change that needs action on upgrade, such as a removed API, a breaking metadata format change, or a migration that is not safe during a rolling update.
- **MINOR**: new features and new optional settings.
- **PATCH**: fixes only.

Releases are tagged `vMAJOR.MINOR.PATCH` on `main`. Add every user-visible change to **Unreleased** in the same pull request. When releasing, rename *Unreleased* to the version and date, and start a new empty *Unreleased* section.

## [Unreleased]

### Added
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
- Home page greeting, date and My Tasks "due today" / "overdue" use the application time zone instead of the server clock (UTC in a container without `TZ`), and the greeting has four periods (Indonesian "Selamat sore" between 15:00 and 18:00).
- DocType editor: a "Tab in section" is now drawn inside its section card (tab strip, then the selected tab's fields), as in Preview and the form, instead of as a separate card below the section. Fields can be dropped on a tab in the strip to move them into it.
- Bursts of saves no longer fail with "too many clients already" (surfacing as "An error occurred while executing document hooks").
- Startup no longer retries non-transient PostgreSQL errors (wrong password, unknown database) for minutes; it fails at once.
