# Enterprise Organization Management (OM) & Workforce Scheduling (WSM) Roadmap

This document tracks the implemented architecture for **Enterprise Organization Management (OM)** and **Workforce Scheduling & Management (WSM)** in DocFrame (`Apps.Erp`), along with prioritized upcoming enhancements.

---

## 1. Completed Foundation (Steps 1–11)

### Track 1: Enterprise Organization Management (OM)
- **Step 1 — Dynamic Unit Types (`OrganizationUnitType`)**: Replaced static `Select` options with a configurable `OrganizationUnitType` master, seeded with 8 system types and advisory parent-type validation.
- **Step 2 — Effective-Dated Tree & Integrity (`OrganizationUnit`)**: Added `valid_from`, `valid_to`, `status` (`Draft`, `Planned`, `Active`, `Closed`), `materialized_path`, and `depth`, enforced by cycle prevention, overlap validation, automatic descendant path cascade, and closure checks.
- **Step 3 — As-Of-Date Queries**: Added `GetValidUnitAsOfAsync` and integrated `ManagerForAsync` to resolve organizational hierarchies as of the request/transaction date.
- **Step 4 — Breadcrumbs & Hierarchy Filtering**: Auto-computed `organization_breadcrumb` and `organization_path` on `Position` and `Employee`, with read-only unit derivation when staffing model is `Position`.
- **Step 5 — Planned Structure Activation**: Implemented `OrganizationUnitActivateAction` to activate `Planned` unit versions, close old versions, and migrate active positions.
- **Step 6 — Dual/Acting Roles & Org Chart (`EmployeeAssignment` + `OrgChart.razor`)**: Added `EmployeeAssignment` for `Primary`, `Secondary`, and `Acting` roles, plus an interactive tree visualization at `/hr/struktur-organisasi`.

### Track 2: Workforce Scheduling & Management (WSM)
- **Step 7 — Shift Groups & Versioned Patterns**: Added `ShiftGroup`, `ShiftPattern` (with `version`, `effective_from`, `effective_to`, `cycle_days`), and `ShiftAssignment`.
- **Step 8 — Materialized Daily Schedules & Batch Generator**: Added `EmployeeDailySchedule`, `ScheduleGeneration`, and `WsmScheduleGenerator` (preserving locked, manual override, swap, and leave rows).
- **Step 9 — Cross-Midnight Attendance & Statutory Overtime**: Updated `AttendanceHook` and `OvertimeRequestHook` to resolve `work_date` and `scheduled_start_datetime` / `scheduled_end_datetime` from `EmployeeDailySchedule`, differentiating `Scheduled Rest Day` vs. `Public Holiday` under PP 35/2021.
- **Step 10 — Constraint Engine, Shift Swap & Leave Sync**: Added configurable `WorkScheduleRule` (max consecutive work days, min rest hours, night-to-morning restriction), `ShiftOverride`, 4-stage `ShiftSwapRequest` workflow, and automatic `LeaveApplication` → `EmployeeDailySchedule` (`LEAVE`) sync.
- **Step 11 — Manpower Coverage & ESS Roster Calendar**: Added `ShiftRequirement` shortage detection and the visual monthly calendar at `/hr/roster-calendar` (`RosterCalendar.razor`).

---

## 2. Prioritized Next Enhancements

### Priority 1: Operational Automation & Mobile Self-Service (Implemented)
1. **ESS Mobile API Roster Integration (`/api/ess/v1`)**:
   - `GET /api/ess/v1/home`: Include `today.schedule` (`scheduleType`, `shiftType`, `dayCategory`, `scheduledStart`, `scheduledEnd`, `isLocked`).
   - `GET /api/ess/v1/schedules`: Query monthly or custom date-range daily schedules for the authenticated employee.
   - `GET /api/ess/v1/shift-swaps` & `POST /api/ess/v1/shift-swaps`: List and submit shift swap requests from mobile ESS.
2. **Rolling Horizon Auto-Schedule Background Job (`RosterRollingHorizonJob`)**:
   - Daily `@daily` scheduled job (`Erp.RosterRollingHorizon`) that automatically generates schedules for the next 30 days across all active companies and `ShiftAssignment`s without overwriting locked or manual/leave entries.
3. **Real-Time Shift Swap Notifications**:
   - Automatic in-app notifications via `ErpEventHandler` / `ShiftSwapRequestHook` to the target peer on submission, the approving manager on peer acceptance, and both employees upon final approval.

### Priority 2: UX & Performance Enhancements (Planned)
1. **Roster Calendar Quick Actions (`/hr/roster-calendar`)**:
   - In-page "Generate Roster" dialog, Excel/CSV export for team schedules, and visual shortage alert badges per date.
2. **Org Chart As-Of-Date Picker (`/hr/struktur-organisasi`)**:
   - Date selector to inspect historical or future `Planned` organization trees, plus SVG/PDF print export.
3. **PostgreSQL Composite Index Optimization**:
   - Explicit indexes in `DocumentIndexSync` for `(tenant_id, employee, work_date)` on `EmployeeDailySchedule` and `text_pattern_ops` on `OrganizationUnit.materialized_path`.

### Priority 3: Advanced Optimization (Future)
1. **Constraint Solver Auto-Scheduling**:
   - Optimization engine (e.g., MILP / Google OR-Tools) to propose optimal shift assignments balancing employee preferences, competencies, and rest constraints.
2. **Matrix Reporting Approval Routing**:
   - Extended approval delegation using `EmployeeAssignment` (`Secondary` / `Acting`) for cross-functional projects.
