# Attendance codes and the monthly recap

Every employee gets one Attendance record per day, also on days without clock-in or clock-out. Each record carries
an **attendance code**. The codes of a month are counted per employee for payroll.

## Codes

| Code | Meaning | When |
|---|---|---|
| **ONT** | On time | Clock-in and clock-out within the shift, inside the grace periods. Also "Work from home" entered by HR without punches. |
| **LI** | Late in | Clock-in after the shift start + *Late grace*. `late_minutes` = minutes after start − grace. |
| **LO** | Early out | Clock-out before the shift end − *Early-out grace*. `early_out_minutes` = minutes before end − grace. |
| **LI/LO** | Both | Late and early out on the same day; counted as a late day **and** as an early-out day. |
| **NCIN** | No clock-in | Only a clock-out, once the deadline has passed. |
| **NCOUT** | No clock-out | Only a clock-in, once the deadline has passed. Lateness is still recorded. |
| **ABS** | Absent | Working day (not a public holiday), no approved leave, no punches, once the deadline has passed. |
| **TO-ND** | Time off, no deduction | Approved leave of a leave type marked **Paid**. |
| **TO-D** | Time off, salary deducted | Approved leave of a leave type **not** marked Paid (master *Leave Type*). |
| **DO** | Day off | Scheduled rest day, holiday or public holiday without punches; or worked with an approved overtime request. |
| **INV** | Invalid | A clock-in more than *N* hours before the shift start, a clock-in after the shift end, or a clock-out before the start; a mobile punch flagged for review (outside the location radius, mock location, other phone…); punches on a day off without approved overtime. |

ONT is used for "on time" so it is not read as overtime in payroll reports.

### Order of the rules

The first matching rule decides:

1. approved full-day leave → TO-ND or TO-D (also when the employee clocked in);
2. day off (schedule REST/HOLIDAY or public holiday; without a daily schedule: the shift's weekly off or the holiday
   list) → DO, or INV when there are punches but no approved overtime;
3. no shift → no code;
4. flagged mobile punch, or punch outside the shift window → INV;
5. no punches → ABS after the deadline (half-day leave: TO-ND/TO-D);
6. only one punch → NCIN / NCOUT after the deadline;
7. both punches → LI, LO, LI/LO or ONT. With half-day leave, lateness and early leaving are not counted.

**Deadline.** A day is decided *scheduled end + N hours* (HR Settings › Attendance codes, default 4). Before that the
code stays empty and *Code final* is off, because punches may still arrive (offline sync, machine import, HR entry).

**HR decides.** Tick *Code set by HR* on a record to choose the code and status by hand (for example after reviewing
a flagged punch). The system then leaves that day alone. Clearing *Punch review notes* (`ess_flags`) and saving also
re-evaluates a flagged day.

**Status.** The status follows the code: ABS → Absent, TO-ND/TO-D → On leave, DO → Day off, the others → Present
(a "Work from home" or "Half day" status entered by HR is kept).

## How records are created

| Source | What happens |
|---|---|
| ESS mobile punches | Every 5 minutes the punches become the day's Attendance (unchanged). The code is computed on save. |
| Machine import / HR entry | The code is computed on save. |
| **Daily attendance job** (`Erp.AttendanceDaily`, hourly) | For every active employee (and those who left, until their separation date) and every day of the last *N* days (HR Settings, default 7): creates the missing record (source *System*), and finalizes records whose deadline has passed. Employees on unpaid leave or suspension get no automatic records. |
| **Recalculate month** (recap page) | Creates missing days of the month and recalculates every code not set by HR, for example after a leave was approved for past days or a schedule changed. |

Months whose payslip is submitted are locked: their records are not created or changed (reported as "skipped").

## Monthly recap

**HR › Time & Attendance › Attendance Recap** (`/hr/attendance-recap`) shows, per employee and month: days per code,
late days and minutes, early-out days and minutes, and days without a final code (no record yet, or still waiting).
Filter by unit; export to Excel or CSV. The page needs read access to the attendance of all employees (HR roles);
*Recalculate month* needs write access.

## Payroll

When a payslip is created, the section **Attendance summary** is filled from the same recap for the payslip's
employment days in the month: `att_on_time_days`, `att_late_days`, `att_late_minutes`, `att_early_out_days`,
`att_early_out_minutes`, `att_no_clock_in_days`, `att_no_clock_out_days`, `att_absent_days`, `att_invalid_days`,
`att_time_off_paid_days`, `att_time_off_deduction_days`, `att_day_off_days`, `att_unclassified_days`.

Salary proration is unchanged:

- unpaid leave days come from approved leave of types not marked Paid (the same days as TO-D);
- with *Prorate for absence* on in Payroll Settings, records with status **Absent** are deducted. These are now created
  automatically (ABS) for working days without punches, so **check the recap before running payroll**:
  an employee without punches for a month because of a missing machine import shows ABS for every working day;
- NCIN, NCOUT, INV, LI and LO are not deducted automatically. Use the summary fields in salary components or review
  them before payroll.

## Settings

| Where | Field | Default |
|---|---|---|
| Shift Type | Late grace (minutes) | 0 |
| Shift Type | Early-out grace (minutes) | 0 |
| HR Settings › Attendance codes | Decide missing clock-in/out after (hours) | 4 |
| HR Settings › Attendance codes | Clock-in accepted from (hours before shift) | 4 |
| HR Settings › Attendance codes | Create daily attendance for the last (days) | 7 |
| Leave Type | Paid | decides TO-ND or TO-D |

The ESS API (`/api/ess/v1/attendance` and the home endpoint) returns `code`, `codeFinal` and `earlyOutMinutes` with each day.
