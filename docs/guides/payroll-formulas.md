# Payroll Formulas, Rate Tables & Tax Allowance

Every company calculates pay differently: a spouse allowance that is 10% of basic salary, a transport allowance per day
present that differs for staff and managers, a phone allowance only for sales, a length-of-service allowance from a table,
basic salary from the grade, an absence cut from attendance, and PPh 21 paid by the company. These rules are **configured as
data**, not code: a Salary Component can be calculated by a **formula**, formulas read **Salary Rate Tables**, and a salary
assignment can use the **Gross-up** PPh 21 method.

This is the same idea as SAP wage types with rules and T510 tables, Oracle Fast Formula with user tables and element
eligibility, and Workday calculated pay components.

## 1. Pieces

| Piece | Where | What it holds |
|---|---|---|
| **Salary Component** | Payroll › Salary Component | Earning or deduction. *Calculation*: **Fixed** (amount typed in the salary assignment or payslip) or **Formula**. *Variable name* (e.g. `GAPOK`) lets formulas use the component. Optional *Company*. |
| **Salary Rate Table** | Payroll › Salary Rate Table | Rows of *Key*, *Key 2*, *From (≥)*, *Up to (<)*, *Amount / rate*, *Valid from*. Used by `lookup()` and `lookup_range()`. |
| **Salary Assignment** | Payroll › Salary Assignment | Fixed earnings and deductions per employee, BPJS flags, and the *PPh 21 method* (Gross / Gross-up). Formula components are **not** typed here. |
| **Payroll Settings** | *Tax allowance (gross-up)* section | The earning used as tax allowance (*tunjangan pajak*). |

## 2. When formulas run

When a payslip is created (also by *Create payslips* on the Payroll Run):

1. Fixed rows are copied from the salary assignment.
2. Working days, payable days and the **attendance summary** are filled (see [Attendance codes](attendance-codes.md)).
3. **Formula components** are calculated, in the order of their references (a component that uses `GAPOK` runs after `GAPOK`).
   A component whose *Applies when* condition is false, or whose result is 0 or less, gives no row.
   Each row records its formula and the values it used in *Calculation*.
4. Approved **overtime** is added. Its hourly wage uses the full-month earnings, including formula components.
5. **Regular** earnings are prorated for new joiners, leavers and unpaid leave (formulas therefore see full-month amounts).
6. BPJS, PPh 21 and the **tax allowance** are calculated; this step runs again on every save.

After creation the amounts can still be edited by hand; formulas are not recalculated on later saves. To recalculate,
delete the draft payslip and run *Create payslips* again.

## 3. Formula language

Formulas use FrogFrame's restricted expression language (no code, no database access).

| Element | Example |
|---|---|
| Numbers, text | `150000`, `'SALES'` |
| Arithmetic | `+ - * / %` and brackets |
| Comparison, logic | `== != < <= > >=`, `&&` / `and`, `\|\|` / `or`, `!` / `not`, `x in ['A', 'B']` |
| Other components | their **variable name**, e.g. `GAPOK` (0 when the employee does not have it) |
| `emp.*` | any Employee field (`emp.grade`, `emp.position`, `emp.job_profile`, `emp.marital_status`, `emp.ptkp_status`, `emp.employment_type`, `emp.location`, `emp.organization_unit`, `emp.religion`, …) plus `emp.name`, `emp.grade_level`, `emp.job_level`, `emp.is_manager`, `emp.years_of_service`, `emp.months_of_service` (from *original hire date*, else *hire date*, to the period end), `emp.age`, `emp.children`, `emp.tax_dependents` |
| `att.*` | `present_days` (ONT + LI + EO + LI/EO + NCIN + NCOUT), `on_time_days`, `late_days`, `late_minutes`, `early_out_days`, `early_out_minutes`, `no_clock_in_days`, `no_clock_out_days`, `absent_days` (ABS), `invalid_days`, `time_off_paid_days`, `time_off_deduction_days`, `day_off_days`, `unclassified_days`, `working_days`, `payable_days`, `unpaid_leave_days` |
| `period.*` | `year`, `month`, `days` (days in the month), `start`, `end` |

Functions:

| Function | Result |
|---|---|
| `if(condition, a, b)` | `a` when the condition holds, else `b`. **Both** `a` and `b` are evaluated. |
| `div(a, b[, fallback])` | `a / b`, or `fallback` (default 0) when `b` is 0. Use it instead of `if()` to guard a division. |
| `min(a, b, …)`, `max(a, b, …)` | smallest / largest |
| `round(x[, digits])`, `floor(x[, multiple])`, `ceil(x[, multiple])` | rounding; `floor(x, 1000)` rounds down to whole thousands |
| `abs(x)`, `coalesce(a, b, …)` | absolute value; first non-empty value |
| `lookup('TABLE', key[, key2])` | amount of the matching row |
| `lookup_range('TABLE', number[, key[, key2]])` | amount of the row with *From* ≤ number < *Up to* (and matching keys) |

Results are rounded to whole rupiah. Saving a Salary Component checks the syntax, unknown names (typos such as
`emp.grade_levle`), and loops between formulas.

## 4. Rate tables

- *Key* / *Key 2* empty = matches any key, so a row with empty keys is the **default**. The most specific matching row wins;
  two equally specific matches are an error. Keys are compared as text, ignoring case (`3` matches grade level 3).
- *From (≥)* / *Up to (<)* are only used by `lookup_range()`; empty = no bound.
- *Valid from*: the rows with the latest date on or before the period end are used **together** as the table version.
  To change rates from July, add a full new set of rows with *Valid from* = 1 July; older payslips keep the old rates.
- No matching row stops payslip creation with a message naming the component and key. Add a default row (amount 0) when
  "no allowance" is a valid outcome.
- A table with a *Company* can only be used on that company's payslips.

## 5. Worked example

Rate tables:

| Table | Rows (Key · From · Up to · Amount) |
|---|---|
| `GAPOK_GRADE` | G1 · · · 5,000,000 — G2 · · · 6,000,000 — G3 · · · 7,500,000 |
| `BENSIN` | STAFF · · · 20,000 — MANAGER · · · 40,000 — (empty) · · · 0 |
| `MASA_KERJA` | · 0 · 3 · 0 — · 3 · 5 · 150,000 — · 5 · 10 · 300,000 — · 10 · · 500,000 |

Salary components (all *Calculation = Formula* unless noted):

| Component | Variable | Type / flags | Applies when | Formula |
|---|---|---|---|---|
| Gaji Pokok | `GAPOK` | Earning, taxable, regular, BPJS base | | `lookup('GAPOK_GRADE', emp.grade)` |
| Tunjangan Istri/Suami | `TUNJ_PASANGAN` | Earning, taxable, regular, BPJS base | `emp.marital_status == 'Married'` | `GAPOK * 10 / 100` |
| Tunjangan Bensin | `BENSIN` | Earning, taxable, **not regular** | | `att.present_days * lookup('BENSIN', if(emp.is_manager, 'MANAGER', 'STAFF'))` |
| Tunjangan Pulsa | `PULSA` | Earning, taxable, regular | `emp.job_profile in ['SALES', 'SALES-SPV']` | `200000` |
| Tunjangan Masa Kerja | `TMK` | Earning, taxable, regular, BPJS base | | `lookup_range('MASA_KERJA', emp.years_of_service)` |
| Potongan Absen | `POT_ABSEN` | Deduction, **pay cut**, taxable, regular, BPJS base | | `GAPOK / 21 * att.absent_days` |
| Potongan Terlambat | `POT_TELAT` | Deduction, pay cut, taxable, regular | | `floor(att.late_minutes / 30) * 25000` |
| Tunjangan Pajak | `TUNJ_PPH` | Earning, taxable, *Fixed* | | *(Payroll Settings › Tax allowance component)* |

Notes:

- `BENSIN` is *not regular*: it already follows attendance, so it must not be prorated again.
- A **pay cut** deduction lowers the taxable gross (if *Taxable* is ticked), the regular pay, and the BPJS base (if *Part of
  BPJS wage base* is ticked), as the law treats it as lower wage. Its GL account may stay empty: the cut is then credited to
  the salary expense account. Turn off *Prorate for absence* in Payroll Settings when an absence cut formula is used,
  otherwise absence is deducted twice (the payslip shows a warning).
- Basic salary by formula: keep **Payroll Settings › Base salary component** on a *Fixed* component (or empty), because job
  offers and job changes write a fixed basic salary into the salary assignment.

## 6. Tax allowance (gross-up)

Set *PPh 21 method = Gross-up* on the salary assignment and choose the *Tax allowance component* in Payroll Settings
(a taxable earning, *Fixed*, not in the BPJS base). On every save the payslip finds the allowance that equals the PPh 21
due on the gross including that allowance (TER in January–November, Article 17 in the last period), adds it as an earning
row, and withholds the same amount, so net pay is as if no tax were withheld. The allowance appears on line 2 of the
1721-A1 summary. If the last period gives a refund (negative PPh 21), the allowance is 0 and the refund goes to the employee.
Check the interaction with PPh 21 DTP with your tax advisor before using both.

## 7. Troubleshooting

| Message | Fix |
|---|---|
| *Unknown name(s): …* | Wrong variable or field name; variable names are case-sensitive. |
| *Formulas refer to each other in a loop* | A formula uses itself, directly or through another formula. |
| *Salary rate table X has no row for key 'Y'* | Add the row, or a default row with empty key. |
| *Salary rate table 'X' not found* | Wrong code, table inactive, or set up for another company. |
| *Division by zero* | Use `div(a, b)` instead of `a / b` (or `if()`). |
| *… is calculated by formula on every payslip; remove it here* | Formula components are not typed in the salary assignment. |
