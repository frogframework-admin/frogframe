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
3. **PostgreSQL Composite Index Optimization** (implemented):
   - `EmployeeDailySchedule` declares `indexes: [{ fields: [employee, work_date] }]`, and `OrganizationUnit` declares a prefix index on `materialized_path` (`text_pattern_ops`). The subtree lookup in `HrHooks` uses the new `startswith` filter, so it reads only the units under the path. See "Database indexes" in `docs/guides/doctype-and-workflows.md`.

### Priority 3: Advanced Optimization & Matrix Governance (Implemented)
1. **Constraint Solver Auto-Scheduling (`WsmOptimizerEngine` / Google OR-Tools CP-SAT)**:
   - Added `ShiftPreference` (`Prefer` / `Avoid` / `Unavailable` with date ranges, day-of-week filters, and 1–5 priority weight) and `ScheduleOptimization` + `ScheduleOptimizationLine`.
   - Implemented `WsmOptimizerEngine` (OR-Tools CP-SAT) optimizing coverage shortage slack, preferences, total/weekend workload fairness, night shift balance, and stability vs baseline while strictly enforcing `WorkScheduleRule` (max consecutive work days, min rest hours, night-to-morning restriction), pinned/locked/override/swap/leave rows, and `Unavailable` constraints.
   - Added `Run Optimizer (CP-SAT)`, `Apply Proposal`, and `Discard Proposal` actions, interactive preview page at `/hr/schedule-optimization/{name}`, and ESS mobile API endpoints (`GET/POST /api/ess/v1/shift-preferences`).
2. **Matrix Reporting & Dotted-Line Approval**:
   - Extended `EmployeeAssignment` with `reporting_line` (`Solid` / `Dotted`), `dotted_line_manager`, `project`, `acting_for` (Plt.), and `has_approval_authority`, and `Project` with `project_manager` and `sponsor`.
   - Implemented `Hr.ResolveApproversAsync` supporting automatic Acting (Plt.) substitution when a line manager is on approved leave or vacant, dotted-line / project approver resolution, and self-approval guards.
   - Added `Pending Project Approval` stage to `ExpenseClaimApproval`, `OvertimeApproval`, and `PurchaseRequestApproval` workflows, daily `Erp.ApprovalDelegationRefresh` job with in-app notifications, ESS pending-approval integration, and dotted-line / Acting (Plt.) visualization on `/hr/organizational-chart/{id}`.
3. **Corrections to Priority 3 (review follow-up)**:
   - Optimizer staffing: each shift above the target headcount costs `w_overstaff` (default 60), a target above the minimum is filled at a lower cost, and `WorkScheduleRule.max_work_days_per_7_days` limits working days in any 7 days. Before this, nothing stopped the solver from scheduling far more people than required.
   - Optimizer horizon: the days before `from_date` (from `EmployeeDailySchedule`) count for the consecutive-day, 7-day and rest-hour rules. Locked rows on a non-candidate shift stay working days in the model.
   - Optimizer demand: each `ShiftRequirement` counts only employees of its unit subtree (and job profile, if set); requirements with the same scope add up instead of taking the largest one. A run for a unit includes the requirements of its sub-units.
   - Optimizer fairness: days an employee cannot work (leave, before hire, after separation, whole-day Unavailable) count as used, so one day of leave no longer switches the fairness term off.
   - Apply: only changed rows are written; days with leave approved after the proposal and months with a submitted payslip are skipped.
   - Approvers: `approver`, `expense_approver` and `leave_approver` on OvertimeRequest, PurchaseRequest, ExpenseClaim, EmployeeAdvance and LeaveApplication are no longer `fetchFrom` fields (the fetch ran after the hook and overwrote the Acting substitute). The hook sets them on every draft save, checking availability for today.
   - Only approved leave makes an approver unavailable (a pending leave request does not). When the project approver would be the same person as the line approver, the project stage is skipped. The delegation job filters pending documents in the query and writes an audit entry.
