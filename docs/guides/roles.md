# Roles

FrogFrame ERP uses granular roles per module. Each role owns a clear set of documents, so one person does not hold every right in a department. This guide lists the roles, explains how the former broad roles (`HR`, `Finance`, …) still work, and shows who can change which DocType.

Two cross-module roles are unchanged:
- **`Employee`**: self-service. Own leave, overtime, expense claims, payslips and similar documents. Approvals for an employee's documents go to the person named in an approver field (line manager, project or expense approver), not to a role.
- **`Manager`**: a line manager. Reads and approves within their organisation unit.

`System Manager` (and `Administrator`) still has full access.

## Role catalog

| Role | Owns |
|---|---|
| `HR-Admin` | HR setup: HR settings, job families and profiles, grades, organisation unit types, leave types and policies, holiday lists, locations. |
| `HR-Recruitment` | Recruitment: job requisitions, applicants (including personal data), interviews and job offers. |
| `HR-Employee-Management` | Employee records (including personal data), contracts, job changes, separations, positions and organisation units. |
| `HR-Time-Attendance` | Time and attendance: attendance, leave allocations and applications, overtime, shifts, rosters and schedules. |
| `HR-Talent` | Talent: appraisals, appraisal cycles and training events. |
| `HR-Payroll` | Payroll: payroll runs, payslips and payroll settings. Should not also hold HR-Comben. |
| `HR-Comben` | Compensation and benefits: salary components and salary assignments; reads grade pay ranges. |
| `CRM-Admin` | CRM setup: CRM settings, sales stages, territories, industries, lead sources and lost reasons. |
| `CRM-Lead-Management` | Leads and activities; converts leads into customers, contacts and opportunities. |
| `CRM-Sales` | Opportunities, quotations, sales orders, customer contracts, customers and contacts. |
| `CRM-Marketing` | Campaigns and leads from campaigns. |
| `Finance-Admin` | Finance setup and master data: finance settings, payment terms, expense claim types, currencies, items, suppliers and their bank accounts. |
| `Finance-Cashier` | Cash and bank payments and receipts (Payment Entry). |
| `Finance-Receivable` | Sales invoices, credit notes, customer receipts and payment reconciliation. |
| `Finance-Payable` | Supplier invoices, debit notes, expense claims, employee advances and commission statements. Does not create payments. |
| `Finance-Treasury` | Bank accounts, bank statements and reconciliation, payment runs, payments, exchange rates. |
| `Finance-Tax` | Tax codes, tax invoices (Coretax) and withholding certificates. |
| `Accounting-Admin` | Chart of accounts, fiscal years, accounting dimensions, accounting settings, company; the ledger accounts in payroll, salary component and stock settings. |
| `Accounting-General-Ledger` | Manual journal entries. |
| `Accounting-Fixed-Assets` | Fixed assets, asset categories, depreciation runs and disposals. |
| `Accounting-Budget-Costing` | Budgets, cost centers and projects. |
| `Accounting-Closing` | Accounting periods (locks) and period closing vouchers. |
| `Accounting-Audit` | Read, report and export on Finance and Accounting documents. No changes. |
| `Procurement-Admin` | Procurement master data: suppliers, supplier groups, items, price lists, payment terms. |
| `Procurement-Buyer` | Purchase orders; turns approved purchase requests into orders. |
| `Procurement-Receiving` | Goods receipts against purchase orders. |
| `Stock-Admin` | Warehouses and stock settings. |
| `Stock-Keeper` | Stock entries (receipts, issues, transfers) and goods receipts. |
| `Stock-Opname` | Physical stock counts (stock reconciliation). |

Two of the roles proposed for CRM were not created:
- **CRM-Opportunity** is part of **CRM-Sales**, because one sales rep owns the opportunity, the quotation and the order.
- **CRM-Customer-Service** needs a case or ticket DocType first, which does not exist yet.

## Former broad roles are groups

`HR`, `Payroll`, `Finance`, `Sales`, `Purchasing` and `Stock` are now **role groups** (`src/Apps.Erp/ErpRoles.cs`):

| Group | Grants |
|---|---|
| `HR` | HR-Admin, HR-Recruitment, HR-Employee-Management, HR-Time-Attendance, HR-Talent |
| `Payroll` | HR-Payroll, HR-Comben |
| `Finance` | all Finance-* and Accounting-* roles |
| `Sales` | all CRM-* roles |
| `Purchasing` | all Procurement-* roles |
| `Stock` | all Stock-* roles |

A group works in both directions when a user signs in:

1. **Holding a group grants its roles.** Accounts that still hold `Finance` get every Finance and Accounting role. They keep the same access as before the split (with the exceptions below), so nothing breaks on upgrade.
2. **Holding a role counts as holding its group.** A user with only `Finance-Payable` also counts as `Finance` for menus, dashboards, notification rules, report and query sharing, and row shares that name `Finance`. Those settings keep reaching the right people.

Expansion never chains. `Finance-Payable` gives `Finance` for those settings, but not the other Finance roles.

The legacy role `Procurement` is not a group and has no rights in the ERP metadata, as before.

## Changes in what people can do

The split keeps every former right, except for these deliberate changes. Each one separates duties that should not sit together.

| Before | Now | Why |
|---|---|---|
| `Finance` could create and submit Payroll Runs and Payslips | Only **HR-Payroll**. Finance reads them | The people who pay salaries do not also compute them |
| `Purchasing` could post Stock Entries and stock counts | Only **Stock-Keeper** and **Stock-Opname**. Procurement reads them | The people who buy do not also move or count stock |
| `Finance` and `Purchasing` could change supplier bank accounts | Only **Finance-Admin** (the field is now permission level 1). Payable, Treasury, Cashier, Audit and Procurement can read it; employees cannot see it | Changing where money goes is the classic payment-fraud step |
| `Finance` could exceed a customer's credit limit when it is set to *Block* | Only **Finance-Admin** | The person who invoices does not also approve going over the limit |
| Form buttons (create payslips, payment run items, purchase order from request, receive goods, invoice from quotation or order, convert lead, roster and optimizer actions) appeared for the whole broad role | They appear for the role that owns the document | The button matches the permission |

Workflow transitions name the owning role:
- leave, overtime and shift swaps: HR-Time-Attendance;
- appraisals: HR-Talent;
- job offers: HR-Recruitment;
- expense claims and employee advances: Finance-Payable;
- purchase requests: Procurement-Buyer;
- quotations: CRM-Sales and Finance-Receivable.

The `has_role(...)` checks in transition conditions changed the same way.

## Segregation of duties

These pairs should not be held by one person. The user page shows a warning when they are; it does not block, because small teams sometimes have no choice.

| Roles | Risk |
|---|---|
| HR-Comben + HR-Payroll | sets pay and runs payroll |
| Finance-Payable + Finance-Treasury, or + Finance-Cashier | records supplier invoices and pays them |
| Finance-Payable + Finance-Admin | records supplier invoices and changes supplier bank accounts |
| Accounting-General-Ledger + Accounting-Closing | posts journals and locks the periods they are in |
| Procurement-Buyer + Procurement-Receiving | orders goods and confirms their receipt |
| Stock-Keeper + Stock-Opname | moves stock and counts it |
| Accounting-Audit + any other Finance or Accounting role | the auditor can change what they audit |

A user who still holds the group `Finance` holds all of these at once. That is the old situation, now made visible.

## Moving users to the new roles

1. Deploy. The new roles are added to every tenant's role list automatically (**Roles** admin), and existing accounts keep working through their groups.
2. In **Users**, open each account and replace the group (for example `Finance`) with the roles the person needs. Group names are marked *(group)* in the list, and conflicts are shown under the role list.
3. Optional: once nobody holds a group any more, it can stay. It costs nothing and keeps old menus, dashboards and notification rules working.

The demo accounts already use granular roles. A new demo account, `finance02` (treasury and finance controller), covers payments, so `finance01` (invoices, journals, tax) does not need conflicting roles.

## DocTypes changed in the designer

DocTypes and workflows that an administrator published in the designer before this release keep their stored permissions, which may still name a group. A group in a permission gives **every** member the group's full rights. At startup the app logs a warning for each such DocType or workflow:

```
Permissions still name a role group, which gives every member its full rights: DocType Customer: Finance, Sales.
```

Edit them in the DocType or workflow editor and replace the group with the granular roles from the table below.

## Who can change what

Generated from the ERP metadata. *Own rows* means a row scope, for example a sales rep's own customers. `System Manager` has full access everywhere and is not listed.

### Core & master data

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `Address` | Finance-Admin: create/write<br>Finance-Receivable: create/write<br>Finance-Payable: create/write<br>Procurement-Admin: create/write<br>Procurement-Buyer: create/write<br>CRM-Lead-Management: create/write<br>CRM-Sales: create/write | other Finance & Accounting roles, other CRM roles, other Procurement roles | - |
| `Bank` | Finance-Admin: create/write<br>HR-Admin: create/write | other Finance & Accounting roles, other HR roles, Employee | - |
| `Company` | Finance-Admin: create/write<br>Accounting-Admin: create/write | other Finance & Accounting roles, Employee | - |
| `Contact` | Finance-Admin: create/write<br>Finance-Receivable: create/write<br>Finance-Payable: create/write<br>Procurement-Admin: create/write<br>Procurement-Buyer: create/write<br>CRM-Lead-Management: create/write<br>CRM-Sales: create/write | other Finance & Accounting roles, other CRM roles, other Procurement roles | - |
| `Currency` | Finance-Admin: create/write | other Finance & Accounting roles, Employee | - |
| `CurrencyExchangeRate` | Finance-Admin: create/write<br>Finance-Treasury: create/write | other Finance & Accounting roles, all CRM roles, all Procurement roles | - |
| `Customer` | Finance-Admin: create/write<br>Finance-Receivable: create/write<br>CRM-Lead-Management (own rows): create/write<br>CRM-Sales (own rows): create/write<br>Manager: write | other Finance & Accounting roles, other CRM roles, CRM-Admin (own rows), CRM-Lead-Management, CRM-Marketing (own rows), CRM-Sales | - |
| `CustomerGroup` | Finance-Admin: create/write | other Finance & Accounting roles, all CRM roles | - |
| `GlobalDefaults` | Finance-Admin: write<br>Accounting-Admin: write | other Finance & Accounting roles, Employee | - |
| `HolidayList` | HR-Admin: create/write<br>HR-Time-Attendance: create/write<br>Finance-Admin: create/write | other Finance & Accounting roles, other HR roles, Employee | - |
| `Item` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, all CRM roles, other Procurement roles, Employee | - |
| `ItemGroup` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, other Procurement roles, Employee | - |
| `ItemPrice` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, all CRM roles, other Procurement roles | - |
| `KpiDefinition` | - | all Finance & Accounting roles, all HR roles, Manager | - |
| `Location` | Finance-Admin: create/write<br>HR-Admin: create/write | other Finance & Accounting roles, other HR roles, Employee | - |
| `PaymentTerm` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, all CRM roles, other Procurement roles | - |
| `PriceList` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, all CRM roles, other Procurement roles | - |
| `UOM` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, other Procurement roles, Employee | - |
| `Vendor` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, other Procurement roles, Employee | Finance-Admin (write), Finance-Payable (read), Finance-Treasury (read), Finance-Cashier (read), Accounting-Audit (read), Procurement-Admin (read), Procurement-Buyer (read) |
| `VendorGroup` | Finance-Admin: create/write<br>Procurement-Admin: create/write | other Finance & Accounting roles, other Procurement roles, Employee | - |

### HR

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `Appraisal` | Employee (own rows): create/write<br>Employee (own rows): write<br>HR-Talent: create/write | other HR roles | - |
| `AppraisalCycle` | HR-Talent: create/write | other HR roles, Employee | - |
| `Attendance` | HR-Time-Attendance: create/write | other HR roles, Employee (own rows), Manager (own rows) | - |
| `Employee` | HR-Employee-Management: create/write | other HR roles, Employee (own rows), Manager (own rows) | HR-Employee-Management (write), Employee (own rows) (read) |
| `EmployeeAssignment` | HR-Employee-Management: create/write | other HR roles, Employee (own rows), Manager | - |
| `EmployeeDailySchedule` | HR-Time-Attendance: create/write | other HR roles, all Payroll roles, Employee (own rows), Manager | - |
| `EmployeeSeparation` | HR-Employee-Management: create/write/submit | other HR roles, Employee (own rows) | HR-Employee-Management (write) |
| `EmploymentContract` | HR-Employee-Management: create/write/submit | other HR roles, Employee (own rows) | HR-Employee-Management (write), Employee (own rows) (read) |
| `Grade` | HR-Admin: create/write | other HR roles, Employee, HR-Comben | HR-Admin (write), HR-Comben (read) |
| `HRSettings` | HR-Admin: write | other HR roles, Employee | - |
| `Interview` | Employee (own rows): write<br>HR-Recruitment: create/write | other HR roles | - |
| `JobApplicant` | HR-Recruitment: create/write | other HR roles, Manager (own rows) | HR-Recruitment (write) |
| `JobChange` | HR-Employee-Management: create/write/submit | other HR roles, Employee (own rows) | - |
| `JobFamily` | HR-Admin: create/write | other HR roles, Employee | - |
| `JobFamilyGroup` | HR-Admin: create/write | other HR roles, Employee | - |
| `JobOffer` | HR-Recruitment: create/write/submit | other HR roles | HR-Recruitment (write) |
| `JobProfile` | HR-Admin: create/write | other HR roles, Employee | - |
| `JobRequisition` | Manager (own rows): create/write<br>HR-Recruitment: create/write/submit | other HR roles, Manager (own rows) | HR-Recruitment (write) |
| `LeaveAllocation` | HR-Time-Attendance: create/write/submit | other HR roles, Employee (own rows) | - |
| `LeaveApplication` | Employee (own rows): create/write<br>HR-Time-Attendance: create/write | other HR roles, Employee (own rows) | - |
| `LeavePolicy` | HR-Admin: create/write<br>HR-Time-Attendance: create/write | other HR roles, Employee | - |
| `LeaveType` | HR-Admin: create/write<br>HR-Time-Attendance: create/write | other HR roles, Employee | - |
| `OrganizationUnit` | HR-Employee-Management: create/write | other HR roles, Employee, Manager | - |
| `OrganizationUnitType` | HR-Admin: create/write | other HR roles, Employee, Manager | - |
| `OvertimeRequest` | Employee (own rows): create/write<br>HR-Time-Attendance: create/write | other HR roles, Employee (own rows) | - |
| `PayrollRun` | HR-Payroll: create/write/submit | all Finance & Accounting roles, other Payroll roles | - |
| `PayrollSettings` | HR-Payroll: write<br>Accounting-Admin: write | other Finance & Accounting roles, other Payroll roles | - |
| `Payslip` | HR-Payroll: create/write/submit | all Finance & Accounting roles, other Payroll roles, Employee (own rows) | - |
| `Position` | HR-Employee-Management: create/write | other HR roles, Employee, Manager | - |
| `SalaryAssignment` | HR-Comben: create/write | all Finance & Accounting roles, other Payroll roles, Employee (own rows) | - |
| `SalaryComponent` | HR-Comben: create/write<br>Accounting-Admin: create/write | other Finance & Accounting roles, other Payroll roles, Employee | - |
| `ScheduleGeneration` | HR-Time-Attendance: create/write | other HR roles, Manager | - |
| `ScheduleOptimization` | HR-Time-Attendance: create/write<br>Manager: create/write | other HR roles | - |
| `ShiftAssignment` | HR-Time-Attendance: create/write | other HR roles, Employee, Manager | - |
| `ShiftGroup` | HR-Time-Attendance: create/write | other HR roles, Employee, Manager | - |
| `ShiftOverride` | HR-Time-Attendance: create/write<br>Manager: create/write | other HR roles, Employee (own rows) | - |
| `ShiftPattern` | HR-Time-Attendance: create/write | other HR roles, Employee, Manager | - |
| `ShiftPreference` | HR-Time-Attendance: create/write<br>Employee (own rows): create/write | other HR roles, Manager | - |
| `ShiftRequirement` | HR-Time-Attendance: create/write<br>Manager: create/write | other HR roles, Employee | - |
| `ShiftSwapRequest` | Employee (own rows): create/write<br>HR-Time-Attendance: create/write | other HR roles, Employee (own rows), Manager | - |
| `ShiftType` | HR-Admin: create/write<br>HR-Time-Attendance: create/write | other HR roles, Employee | - |
| `TrainingEvent` | HR-Talent: create/write/submit | other HR roles, Employee, Manager | - |
| `WorkScheduleRule` | HR-Time-Attendance: create/write | other HR roles, Employee, Manager | - |

### CRM

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `Activity` | CRM-Lead-Management (own rows): create/write<br>CRM-Sales (own rows): create/write<br>Manager (own rows): create/write | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Marketing (own rows) | - |
| `CRMSettings` | Manager: write | - | - |
| `Campaign` | CRM-Marketing (own rows): create/write<br>Manager (own rows): create/write | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Lead-Management (own rows), CRM-Sales (own rows) | - |
| `CommissionPlan` | Finance-Admin: create/write | other Finance & Accounting roles, all CRM roles, Manager | - |
| `CommissionStatement` | Finance-Payable: create/write/submit | other Finance & Accounting roles, CRM-Admin (own rows), CRM-Lead-Management (own rows), CRM-Marketing (own rows), CRM-Sales (own rows), Manager | - |
| `CustomerContract` | CRM-Sales (own rows): create/write/submit<br>Manager (own rows): create/write/submit | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Lead-Management (own rows), CRM-Marketing (own rows) | - |
| `Industry` | Manager: create/write<br>CRM-Admin: create/write | other CRM roles | - |
| `Lead` | CRM-Lead-Management (own rows): create/write<br>CRM-Marketing (own rows): create/write<br>Manager (own rows): create/write | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Sales (own rows) | - |
| `LeadSource` | Manager: create/write<br>CRM-Admin: create/write | other CRM roles | - |
| `LostReason` | Manager: create/write<br>CRM-Admin: create/write | other CRM roles | - |
| `Opportunity` | CRM-Lead-Management (own rows): create/write<br>CRM-Sales (own rows): create/write<br>Manager (own rows): create/write | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Marketing (own rows) | - |
| `Quotation` | CRM-Sales (own rows): create/write/submit<br>Manager (own rows): create/write/submit | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Lead-Management (own rows), CRM-Marketing (own rows) | - |
| `SalesOrder` | CRM-Sales (own rows): create/write/submit<br>Manager (own rows): create/write/submit | all Finance & Accounting roles, CRM-Admin (own rows), CRM-Lead-Management (own rows), CRM-Marketing (own rows) | - |
| `SalesStage` | Manager: create/write<br>CRM-Admin: create/write | other CRM roles | - |
| `Territory` | Manager: create/write<br>CRM-Admin: create/write | other CRM roles | - |

### Finance

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `Asset` | Accounting-Fixed-Assets: create/write/submit | other Finance & Accounting roles, Manager | - |
| `AssetCategory` | Accounting-Fixed-Assets: create/write | other Finance & Accounting roles, Employee | - |
| `AssetDisposal` | Accounting-Fixed-Assets: create/write/submit | other Finance & Accounting roles, Manager | - |
| `BankAccount` | Finance-Treasury: create/write | other Finance & Accounting roles | - |
| `BankReconciliation` | Finance-Treasury: create/write/submit | other Finance & Accounting roles | - |
| `BankStatement` | Finance-Treasury: create/write/submit | other Finance & Accounting roles | - |
| `DepreciationRun` | Accounting-Fixed-Assets: create/write/submit | other Finance & Accounting roles, Manager | - |
| `EmployeeAdvance` | Employee (own rows): create/write<br>Finance-Payable: write | other Finance & Accounting roles, Employee (own rows) | - |
| `ExpenseClaim` | Employee (own rows): create/write<br>Finance-Payable: write | other Finance & Accounting roles, Employee (own rows) | - |
| `ExpenseClaimType` | Finance-Admin: create/write | other Finance & Accounting roles, Employee | - |
| `FinanceSettings` | Finance-Admin: write | other Finance & Accounting roles | - |
| `PaymentEntry` | Finance-Cashier: create/write/submit<br>Finance-Receivable: create/write/submit<br>Finance-Treasury: create/write/submit | other Finance & Accounting roles, Manager | - |
| `PaymentReconciliation` | Finance-Receivable: create/write/submit<br>Finance-Payable: create/write/submit | other Finance & Accounting roles | - |
| `PaymentRun` | Finance-Treasury: create/write/submit | other Finance & Accounting roles | - |
| `PurchaseDebitNote` | Finance-Payable: create/write/submit | other Finance & Accounting roles, all Procurement roles, Manager | - |
| `PurchaseInvoice` | Finance-Payable: create/write/submit | other Finance & Accounting roles, all Procurement roles, Manager | - |
| `SalesCreditNote` | Finance-Receivable: create/write/submit | other Finance & Accounting roles, Manager | - |
| `SalesInvoice` | Finance-Receivable: create/write/submit | other Finance & Accounting roles, Manager | - |
| `TaxCode` | Finance-Tax: create/write | other Finance & Accounting roles, Employee | - |
| `TaxInvoice` | Finance-Tax: create/write/submit | other Finance & Accounting roles | - |
| `WithholdingCertificate` | Finance-Tax: create/write/submit | other Finance & Accounting roles | - |

### Accounting

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `Account` | Accounting-Admin: create/write | other Finance & Accounting roles, Employee | - |
| `AccountingDimension` | Accounting-Admin: create/write | other Finance & Accounting roles | - |
| `AccountingPeriod` | Accounting-Closing: create/write | other Finance & Accounting roles, Employee | - |
| `AccountingSettings` | Accounting-Admin: write | other Finance & Accounting roles | - |
| `Budget` | Accounting-Budget-Costing: create/write/submit | other Finance & Accounting roles, Manager | - |
| `CostCenter` | Accounting-Budget-Costing: create/write | other Finance & Accounting roles, Employee | - |
| `FiscalYear` | Accounting-Admin: create/write | other Finance & Accounting roles, Employee | - |
| `JournalEntry` | Accounting-General-Ledger: create/write/submit | other Finance & Accounting roles, Manager | - |
| `PeriodClosingVoucher` | Accounting-Closing: create/write/submit | other Finance & Accounting roles, Manager | - |
| `Project` | Accounting-Budget-Costing: create/write | other Finance & Accounting roles, Employee, Manager | - |

### Procurement

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `GoodsReceipt` | Procurement-Receiving: create/write/submit<br>Stock-Keeper: create/write/submit<br>Employee (own rows): create/write | all Finance & Accounting roles, other Procurement roles, other Stock roles, Manager | - |
| `PurchaseOrder` | Procurement-Buyer: create/write/submit | all Finance & Accounting roles, other Procurement roles, all Stock roles, Manager | - |
| `PurchaseRequest` | Employee (own rows): create/write | all Procurement roles, Employee (own rows), Manager (own rows) | - |

### Stock

| DocType | Can change | Read only | Restricted fields (level 1) |
|---|---|---|---|
| `StockEntry` | Stock-Keeper: create/write/submit | all Finance & Accounting roles, all CRM roles, all Procurement roles, other Stock roles, Manager | - |
| `StockReconciliation` | Stock-Opname: create/write/submit | all Finance & Accounting roles, all CRM roles, all Procurement roles, other Stock roles, Manager | - |
| `StockSettings` | Stock-Admin: write<br>Accounting-Admin: write | other Finance & Accounting roles, all Procurement roles, other Stock roles | - |
| `Warehouse` | Stock-Admin: create/write | all Finance & Accounting roles, all CRM roles, all Procurement roles, other Stock roles, Employee | - |


## For developers

- Roles, groups and conflicts are defined in `src/Apps.Erp/ErpRoles.cs` and registered by `AddErpHooks()`.
- Group expansion lives in `Framework.Security.RoleGroups`. It is applied when claims are built (`ClaimsUserMapper`), in the user directory and for automation identities.
- In code, check the granular role: `user.HasRole(ErpRoles.FinanceTreasury)`. `HasRole` already returns true for administrators.
- In DocType permissions and workflow transitions, name granular roles only. The test `Role_split_erp_metadata_names_granular_roles_only` fails when a group or an unknown role appears.
- Tests: `tests/Framework.Tests/RoleSplitTests.cs` and `RoleClaimsTests.cs`.
