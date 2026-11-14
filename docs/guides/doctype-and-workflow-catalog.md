# DocType & Workflow Catalog

This document provides an exhaustive reference catalog of all **DocTypes** and **Visual Workflows** defined within the ERP application suite.

DocTypes define schema structures, permissions, child tables, and behaviors. Workflows define multi-stage approval lifecycles, role permissions, and validation transitions.

## Summary Overview

| Metric | Count | Details |
|---|---|---|
| **Total Modules** | `7` | Core, HR, Procurement, Accounting, Finance, CRM, Stock |
| **Total DocTypes** | `146` | Document schemas and child tables |
| **Active Workflows** | `8` | State machine lifecycles with role-based transitions |

---

## 1. DocType Catalog by Module

### Core & Master Data (23 DocTypes)

Foundational master data, organization settings, locations, currencies, customers, suppliers, and shared pricing tables used across all business apps.

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `Address` | Address | Document | No | - | One address can be used by several parties (Customer, Supplier, Company, Location). |
| `Bank` | Bank | Document | No | - | Bank entity. |
| `Company` | Company | Document | No | - | Legal entity (SAP Company Code / NetSuite Subsidiary / Workday Company). |
| `Contact` | Contact | Document | No | - | Person representing a party (Salesforce Contact). Shared by CRM, AR, AP. |
| `Currency` | Currency | Document | No | - | Currency entity. |
| `CurrencyExchangeRate` | Currency Exchange Rate | Document | No | - | Exchange rate per date and rate type (SAP exchange rate type). The Tax Rate is used for foreign-currency VAT. |
| `Customer` | Customer | Document | No | - | Customer, shared by CRM and Receivables (Salesforce Account / NetSuite Customer). |
| `CustomerGroup` | Customer Group | Document | No | - | Customer Group entity. |
| `GlobalDefaults` | General Settings | Document | No | - | Settings that apply to the whole tenant. |
| `Holiday` | Holiday | Document | No | - | Holiday entity. |
| `HolidayList` | Holiday List | Document | No | - | Used by leave, attendance, and working-day calculations (Workday Holiday Calendar). |
| `Item` | Item | Document | No | - | Goods or services sold/bought (Salesforce Product2 / NetSuite Item). |
| `ItemGroup` | Item Group | Document | No | - | Item Group entity. |
| `ItemPrice` | Item Price | Document | No | - | Item price per price list (Salesforce PricebookEntry). |
| `Location` | Location | Document | No | - | Physical location: office, branch, warehouse (Workday Location / NetSuite Location). Also an accounting dimension. |
| `PartyBankAccount` | Party Bank Account | Document | No | - | Party Bank Account entity. |
| `PartyLink` | Party Link | Document | No | - | Party Link entity. |
| `PaymentTerm` | Payment Terms | Document | No | - | Installment payment terms (NetSuite Terms / SAP Payment Terms). |
| `PaymentTermDetail` | Payment Term Detail | Document | No | - | Payment Term Detail entity. |
| `PriceList` | Price List | Document | No | - | Salesforce Pricebook2. |
| `UOM` | UOM | Document | No | - | UOM entity. |
| `Vendor` | Supplier | Document | No | - | Supplier (NetSuite Vendor / SAP Business Partner supplier). |
| `VendorGroup` | Supplier Group | Document | No | - | Supplier Group entity. |


### Human Resources (HR) (46 DocTypes)

Comprehensive human capital management covering employee profiles, organizational units, job requisitions, leave, attendance, overtime, appraisals, compensation, and Indonesian statutory payroll (BPJS & PPh 21 TER).

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `Appraisal` | Appraisal | Document | No | `AppraisalReview` | Goal-based performance review: employee self-scores, manager scores and completes (Workday Performance Review). Weighted score 1–5 sets the rating. |
| `AppraisalCycle` | Appraisal Cycle | Document | No | - | Performance review period (Workday Review Template / Cycle). |
| `AppraisalGoal` | Appraisal Goal | Document | No | - | Appraisal Goal entity. |
| `Attendance` | Attendance | Document | No | - | Daily attendance per employee (Workday Time Tracking). Locked once the payslip for its month is submitted. |
| `Employee` | Employee | Document | No | - | Worker record (Workday Worker). Job changes go through Job Change, exits through Employee Separation. |
| `EmployeeDependent` | Dependents | Document | No | - | Dependents entity. |
| `EmployeeEducation` | Education History | Document | No | - | Education History entity. |
| `EmployeeEmergencyContact` | Emergency Contact | Document | No | - | Emergency Contact entity. |
| `EmployeeIdentityDocument` | Identity Document | Document | No | - | Identity Document entity. |
| `EmployeeSeparation` | Employee Separation | Document | No | - | Exit event (Workday Terminate). Submit: Employee → Left, Position → Vacant, user account disabled (hook). |
| `EmployeeWorkHistory` | Previous Employment | Document | No | - | Previous Employment entity. |
| `EmploymentContract` | Employment Contract | Document | No | - | Fixed-term (PKWT) / permanent (PKWTT) employment contract (Workday Employee Contract). The hook checks the PKWT duration limit and reminds before it ends. |
| `Grade` | Grade | Document | No | - | Compensation grade + pay range per company (Workday Compensation Grade & Grade Profile). |
| `GradePayRange` | Grade Pay Range | Document | No | - | Grade Pay Range entity. |
| `HRSettings` | HR Settings | Document | No | - | HR Settings entity. |
| `Interview` | Interview | Document | No | - | One interview round by one interviewer, with feedback (Workday Interview). The interviewer can read and fill their own. |
| `JobApplicant` | Job Applicant | Document | No | - | Candidate for a requisition (Workday Candidate). Personal data is permission level 1 (HR only), in line with the PDP Law. |
| `JobChange` | Job Change | Document | No | - | One event for promotion/transfer/rotation/reorganization (Workday Change Job). Submit: the hook updates Employee & Position as of the effective date. |
| `JobFamily` | Job Family | Document | No | - | Job Family entity. |
| `JobFamilyGroup` | Job Family Group | Document | No | - | Job Family Group entity. |
| `JobOffer` | Job Offer | Document | No | `JobOfferLifecycle` | Offer to a candidate (Workday Offer). Workflow: Draft → Sent → Accepted/Declined/Expired. 'Create employee' turns an accepted offer into an Employee. |
| `JobProfile` | Job Profile | Document | No | - | Generic job definition (Workday Job Profile / SAP Job). A position is its instance. |
| `JobRequisition` | Job Requisition | Document | No | - | Approved request to hire (Workday Job Requisition). Submit = approved and open for applicants. |
| `LeaveAllocation` | Leave Allocation | Document | No | - | Leave balance per employee per period. Created in bulk by the scheduler from LeavePolicy. |
| `LeaveApplication` | Leave Application | Document | No | `LeaveApproval` | Leave request approved by the employee's leave approver (Workday Request Time Off). |
| `LeavePolicy` | Leave Policy | Document | No | - | Leave entitlement package per employee group (Workday Time Off Plan). |
| `LeavePolicyDetail` | Leave Entitlement | Document | No | - | Leave Entitlement entity. |
| `LeaveType` | Leave Type | Document | No | - | Leave type (Workday Time Off Type). |
| `OrganizationUnit` | Organization Unit | Document | No | - | Organization unit/management hierarchy (Workday Supervisory Organization / SAP OM Org Unit). Org chart via OrgTree. |
| `OvertimeRequest` | Overtime Request | Document | No | `OvertimeApproval` | Overtime request with approval; approved overtime is paid on the payslip of its month (PP 35/2021: hourly wage = 1/173 of the monthly wage). |
| `PayrollRun` | Payroll Run | Document | No | - | Monthly payroll for a company (Workday Pay Run). 'Create payslips' drafts one payslip per employee; submit posts the payroll journal from the submitted payslips. |
| `PayrollSettings` | Payroll Settings | Document | No | - | Statutory rates and accounts for payroll. Values reflect the rules in force in November 2026; verify each year. |
| `Payslip` | Payslip | Document | No | - | Monthly pay for one employee: earnings, BPJS, PPh 21 (TER, annual in the last period), net pay. Posted to the ledger by its Payroll Run. |
| `PayslipDetail` | Payslip Detail | Document | No | - | Payslip Detail entity. |
| `Position` | Position | Document | No | - | Budgeted headcount slot filled by one employee (Workday Position / SAP Position). Hierarchy via 'reports to position'. |
| `PositionDottedLine` | Dotted Line | Document | No | - | Dotted Line entity. |
| `PtkpRate` | PTKP Rate | Document | No | - | PTKP Rate entity. |
| `SalaryAssignment` | Salary Assignment | Document | No | - | Pay package per employee from a date (Workday Compensation / SAP infotype 0008). |
| `SalaryComponent` | Salary Component | Document | No | - | Earning or deduction on a payslip (Workday Pay Component / SAP Wage Type). |
| `SalaryDetail` | Salary Detail | Document | No | - | Salary Detail entity. |
| `SeparationChecklistItem` | Offboarding Checklist | Document | No | - | Offboarding Checklist entity. |
| `ShiftType` | Shift Type | Document | No | - | Standard work schedule (Workday Work Schedule Calendar). |
| `TaxBracket` | Tax Bracket | Document | No | - | Tax Bracket entity. |
| `TerRate` | TER Rate | Document | No | - | TER Rate entity. |
| `TrainingAttendee` | Training Attendee | Document | No | - | Training Attendee entity. |
| `TrainingEvent` | Training Event | Document | No | - | Training session with attendees, attendance, scores, and certificates (Workday Learning). Submit after the event to lock the record. |


### Procurement & Purchasing (6 DocTypes)

Source-to-pay workflow covering internal purchase requests, supplier purchase orders, and goods receipt verification for 3-way matching.

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `GoodsReceipt` | Goods Receipt | Document | No | - | Receipt of goods or acceptance of services against a purchase order (SAP MIGO / BAST). No inventory valuation: used for 3-way match. |
| `GoodsReceiptItem` | Goods Receipt Item | Document | No | - | Goods Receipt Item entity. |
| `PurchaseOrder` | Purchase Order | Document | No | - | Order to a supplier (SAP Purchase Order / NetSuite PO). Received through Goods Receipt, billed through Purchase Invoice (3-way match). |
| `PurchaseOrderItem` | Purchase Order Item | Document | No | - | Purchase Order Item entity. |
| `PurchaseRequest` | Purchase Request | Document | No | `PurchaseRequestApproval` | Internal request to buy (SAP Purchase Requisition / NetSuite Purchase Request), approved by the requester's expense approver. |
| `PurchaseRequestItem` | Purchase Request Item | Document | No | - | Purchase Request Item entity. |


### General Ledger & Accounting (12 DocTypes)

Multi-company chart of accounts, accounting periods, journal vouchers, cost centers, accounting dimensions, and annual expense budgeting.

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `Account` | Account | Document | No | - | Chart of accounts shared by all companies in the tenant (SAP chart of accounts / NetSuite Account). |
| `AccountingDimension` | Accounting Dimension | Document | No | - | Registry of dimensions carried to the ledger (NetSuite Segment / SAP ACDOCA). Needs a LedgerLine with generic dimensions. |
| `AccountingPeriod` | Accounting Period | Document | No | - | Monthly period with locks per subledger like NetSuite period close (AR → AP → assets → GL). |
| `AccountingSettings` | Accounting Settings | Document | No | - | Accounting Settings entity. |
| `Budget` | Budget | Document | No | - | Annual expense budget per account, optionally per cost center (SAP Funds Management / NetSuite Budget). Submit to activate; amend to revise. |
| `BudgetAccount` | Budget Account | Document | No | - | Budget Account entity. |
| `CostCenter` | Cost Center | Document | No | - | Cost center (SAP Cost Center / NetSuite Department). Mandatory dimension for expense accounts when set on the account. |
| `FiscalYear` | Fiscal Year | Document | No | - | Fiscal Year entity. |
| `JournalEntry` | Journal Entry | Document | No | - | Manual journal. Submit posts to the ledger; cancel creates reversing lines. |
| `JournalEntryLine` | Journal Entry Line | Document | No | - | Journal Entry Line entity. |
| `PeriodClosingVoucher` | Period Closing Voucher | Document | No | - | Closes profit and loss account balances into retained earnings and closes the fiscal year. |
| `Project` | Project | Document | No | - | Project as an accounting dimension (SAP WBS / NetSuite Project). The full project module comes later. |


### Finance & Cash Management (33 DocTypes)

Accounts receivable and payable processing, customer sales invoices, supplier purchase invoices, multi-currency payment entries, bank accounts, tax invoices, and fixed asset management.

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `Asset` | Fixed Asset | Document | No | - | Fixed asset register (SAP Asset Master / NetSuite FAM Asset). Submit = asset in use; depreciation via Depreciation Run. |
| `AssetCategory` | Asset Category | Document | No | - | Asset class (SAP Asset Class / NetSuite FAM Asset Type): method, life, and accounts. |
| `AssetDepreciationSchedule` | Depreciation Schedule | Document | No | - | Depreciation Schedule entity. |
| `AssetDisposal` | Asset Disposal | Document | No | - | Asset sale/write-off (SAP ABAVN). Submit: closes cost & accumulated depreciation, recognizes gain/loss. |
| `BankAccount` | Bank Account | Document | No | - | Company bank/cash account (SAP House Bank). Other parties' accounts are on Supplier/Employee. |
| `BankReconciliation` | Bank Reconciliation | Document | No | - | Reconciles book balance vs bank statement per account per period. |
| `BankReconciliationItem` | Bank Reconciliation Item | Document | No | - | Bank Reconciliation Item entity. |
| `BankStatement` | Bank Statement | Document | No | - | Imported bank statement (SAP Electronic Bank Statement). Matched to vouchers during reconciliation. |
| `BankStatementLine` | Bank Statement Line | Document | No | - | Bank Statement Line entity. |
| `DepreciationRun` | Depreciation Run | Document | No | - | One document per period that posts depreciation for all assets (SAP AFAB / NetSuite Depreciation). |
| `DepreciationRunItem` | Depreciation Run Item | Document | No | - | Depreciation Run Item entity. |
| `EmployeeAdvance` | Employee Advance | Document | No | `EmployeeAdvanceApproval` | Employee cash advance (Workday Spend Authorization + Cash Advance). Paid via Payment Entry, settled through Expense Claim. |
| `ExpenseClaim` | Expense Claim | Document | No | `ExpenseClaimApproval` | Employee expense reimbursement (Workday Expense Report / NetSuite Expense Report). Approved: Dr expense / Cr employee payable; paid via Payment Entry. |
| `ExpenseClaimItem` | Expense Claim Item | Document | No | - | Expense Claim Item entity. |
| `ExpenseClaimType` | Expense Claim Type | Document | No | - | Employee expense category (Workday Expense Item). |
| `FinanceSettings` | Finance Settings | Document | No | - | Finance Settings entity. |
| `InvoicePaymentSchedule` | Payment Schedule | Document | No | - | Payment Schedule entity. |
| `PaymentEntry` | Payment | Document | No | - | Receipts, payments, and internal transfers (NetSuite Customer/Vendor Payment, SAP F-28/F-53). |
| `PaymentEntryDeduction` | Payment Deduction | Document | No | - | Payment Deduction entity. |
| `PaymentEntryReference` | Payment Allocation | Document | No | - | Payment Allocation entity. |
| `PaymentReconciliation` | Payment Reconciliation | Document | No | - | Allocates party advances (unallocated payments) to open invoices without moving cash (NetSuite Apply Credit / SAP F-32 / F-44). Submit: Dr/Cr the same control account against the payment and the invoice. |
| `PaymentReconciliationAllocation` | Payment Reconciliation Allocation | Document | No | - | Payment Reconciliation Allocation entity. |
| `PaymentRun` | Payment Run | Document | No | - | Batch payment of due supplier invoices, expense claims, advances, and salaries (SAP F110 / NetSuite Pay Bills). Submit creates and submits one Payment Entry per party; the bank transfer list is in the Bank Transfer List report. |
| `PaymentRunItem` | Payment Run Item | Document | No | - | Payment Run Item entity. |
| `PurchaseDebitNote` | Purchase Debit Note | Document | No | - | Return/discount from a supplier (NetSuite Vendor Credit). Reduces the original invoice's payable. |
| `PurchaseInvoice` | Purchase Invoice | Document | No | - | Supplier bill (NetSuite Vendor Bill / SAP MIRO). Submit: Dr expense/asset & input VAT, Cr payable & withholding tax payable. |
| `PurchaseInvoiceItem` | Purchase Invoice Item | Document | No | - | Purchase Invoice Item entity. |
| `SalesCreditNote` | Sales Credit Note | Document | No | - | Return/discount against a sales invoice (NetSuite Credit Memo). Reduces the original invoice's receivable. |
| `SalesInvoice` | Sales Invoice | Document | No | - | Sales invoice (NetSuite Invoice / SAP FB70). Submit: Dr receivable, Cr income & output VAT. |
| `SalesInvoiceItem` | Sales Invoice Item | Document | No | - | Sales Invoice Item entity. |
| `TaxCode` | Tax Code | Document | No | - | Tax rate and account (SAP Tax Code / NetSuite Tax Code). Shared chart of accounts, so one account per code. |
| `TaxInvoice` | Tax Invoice | Document | No | - | Output/input tax invoice register. Created by hook from invoices; number & status synced with Coretax. |
| `WithholdingCertificate` | Withholding Tax Certificate | Document | No | - | Withholding certificates for PPh 23/4(2)/26/15: those we issue, and those we receive from customers (tax credit). |


### Customer Relationship Management (CRM) (20 DocTypes)

Lead qualification, pipeline management, opportunity tracking, customer contracts, sales campaigns, and commission management.

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `Activity` | Activity | Document | No | - | Sales calls, meetings, visits, and tasks (Salesforce Task/Event). Email stays with the framework's communications feature. |
| `CRMSettings` | CRM Settings | Document | No | - | CRM Settings entity. |
| `Campaign` | Campaign | Document | No | - | Marketing campaign with cost and results (Salesforce Campaign). Leads and opportunities link to it for ROI. |
| `CommissionLine` | Commission Line | Document | No | - | Commission Line entity. |
| `CommissionPlan` | Commission Plan | Document | No | - | Sales commission plan (Salesforce Spiff / NetSuite Commission Schedule): flat rate on invoiced amount, with per-rep overrides. |
| `CommissionPlanRep` | Commission Plan Rep | Document | No | - | Commission Plan Rep entity. |
| `CommissionStatement` | Commission Statement | Document | No | - | Monthly commission per sales rep from submitted sales invoices and credit notes. Submit accrues Dr commission expense / Cr commission payable (paid with Payment Entry). |
| `CustomerContract` | Customer Contract | Document | No | - | Signed customer contract with term and renewal reminder (Salesforce Contract). The daily CRM job reminds the owner before the end date. |
| `Industry` | Industry | Document | No | - | Industry entity. |
| `Lead` | Lead | Document | No | - | Unqualified prospect (Salesforce Lead). The 'Convert' action creates Customer + Contact + Opportunity. |
| `LeadSource` | Lead Source | Document | No | - | Lead Source entity. |
| `LostReason` | Lost Reason | Document | No | - | Lost Reason entity. |
| `Opportunity` | Opportunity | Document | No | - | Sales opportunity with stage, probability, and forecast (Salesforce Opportunity). Stage history via track changes. |
| `OpportunityContactRole` | Contact Role | Document | No | - | Contact Role entity. |
| `OpportunityLine` | Opportunity Item | Document | No | - | Opportunity Item entity. |
| `Quotation` | Quotation | Document | No | `QuotationLifecycle` | Price quotation (Salesforce Quote). Workflow: Draft → Sent → Accepted/Rejected/Expired → Billed. Revisions via amend (versions -1, -2). |
| `QuotationItem` | Quotation Item | Document | No | - | Quotation Item entity. |
| `SalesOrder` | Sales Order | Document | No | - | Confirmed customer order (Salesforce Order / NetSuite Sales Order). Billed in one or more sales invoices; billing status is computed. |
| `SalesStage` | Sales Stage | Document | No | - | Opportunity stage with probability and forecast category (Salesforce OpportunityStage). |
| `Territory` | Territory | Document | No | - | Sales territory tree (Salesforce Territory / NetSuite Sales Territory), e.g. Indonesia → Java → Jabodetabek. |


### Inventory & Stock Management (6 DocTypes)

Multi-warehouse stock movements, inventory entries (receipts, issues, transfers), perpetual inventory settings, and physical stock count reconciliations.

| DocType | Label / Title | Type | Submittable | Workflow | Description |
|---|---|---|---|---|---|
| `StockEntry` | Stock Entry | Document | No | - | Stock receipt, issue, or transfer (SAP MIGO 501/201/311 / NetSuite Inventory Adjustment & Transfer). Submit posts the movement and the inventory journal. |
| `StockEntryDetail` | Stock Entry Detail | Document | No | - | Stock Entry Detail entity. |
| `StockReconciliation` | Stock Opname | Document | No | - | Physical stock count per warehouse (SAP MI07 / NetSuite Inventory Count). Submit sets stock to the counted quantity and posts the value difference. |
| `StockReconciliationItem` | Stock Opname Item | Document | No | - | Stock Opname Item entity. |
| `StockSettings` | Stock Settings | Document | No | - | Defaults and accounts for perpetual inventory. |
| `Warehouse` | Warehouse | Document | No | - | Warehouse or stock location tree (SAP Storage Location / NetSuite Location with inventory). |


---

## 2. ERP Workflows Catalog

Workflows model formal approval gates and document lifecycles. When a workflow is assigned to a DocType, the standard `Save` / `Submit` buttons are replaced with workflow state actions restricted by user roles and transition conditions.


### Workflow: AppraisalReview

- **Target DocType**: `Appraisal`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Self review done` | Draft (0) | `pending` | Self review done state |
| `Completed` | Submitted (1) | `positive` | Completed state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Submit self review** | `Self review done` | Employee, HR | `employee == user.employee || has_role('HR')` |
| `Self review done` | **Return to employee** | `Draft` | Employee, HR | `reviewer == user.employee || has_role('HR')` |
| `Self review done` | **Complete review** | `Completed` | Employee, HR | `(reviewer == user.employee && employee != user.employee) || has_role('HR')` |
| `Completed` | **Cancel** | `Cancelled` | HR | None |


### Workflow: EmployeeAdvanceApproval

- **Target DocType**: `EmployeeAdvance`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Pending Manager` | Submitted (1) | `pending` | Pending Manager state |
| `Pending Finance` | Submitted (1) | `pending` | Pending Finance state |
| `Approved` | Submitted (1) | `positive` | Approved state |
| `Rejected` | Cancelled (2) | `negative` | Rejected state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Submit for approval** | `Pending Manager` | Employee | `employee == user.employee` |
| `Pending Manager` | **Approve** | `Pending Finance` | Employee | `expense_approver == user.employee && employee != user.employee` |
| `Pending Manager` | **Reject** | `Rejected` | Employee | `expense_approver == user.employee && employee != user.employee` |
| `Pending Manager` | **Withdraw** | `Cancelled` | Employee | `employee == user.employee` |
| `Pending Finance` | **Approve** | `Approved` | Finance | `employee != user.employee` |
| `Pending Finance` | **Reject** | `Rejected` | Finance | None |
| `Approved` | **Cancel** | `Cancelled` | Finance | None |


### Workflow: ExpenseClaimApproval

- **Target DocType**: `ExpenseClaim`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Pending Manager` | Submitted (1) | `pending` | Pending Manager state |
| `Pending Finance Review` | Submitted (1) | `pending` | Pending Finance Review state |
| `Approved` | Submitted (1) | `positive` | Approved state |
| `Rejected` | Cancelled (2) | `negative` | Rejected state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Submit for approval** | `Pending Manager` | Employee | `employee == user.employee` |
| `Pending Manager` | **Approve** | `Pending Finance Review` | Employee | `expense_approver == user.employee && employee != user.employee` |
| `Pending Manager` | **Reject** | `Rejected` | Employee | `expense_approver == user.employee && employee != user.employee` |
| `Pending Manager` | **Withdraw** | `Cancelled` | Employee | `employee == user.employee` |
| `Pending Finance Review` | **Verify** | `Approved` | Finance | `employee != user.employee` |
| `Pending Finance Review` | **Reject** | `Rejected` | Finance | None |
| `Approved` | **Cancel** | `Cancelled` | Finance | None |


### Workflow: JobOfferLifecycle

- **Target DocType**: `JobOffer`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Sent` | Submitted (1) | `pending` | Sent state |
| `Accepted` | Submitted (1) | `positive` | Accepted state |
| `Declined` | Submitted (1) | `negative` | Declined state |
| `Expired` | Submitted (1) | `negative` | Expired state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Send to candidate** | `Sent` | HR | None |
| `Sent` | **Candidate accepted** | `Accepted` | HR | None |
| `Sent` | **Candidate declined** | `Declined` | HR | None |
| `Sent` | **Mark expired** | `Expired` | HR | None |
| `Sent` | **Withdraw offer** | `Cancelled` | HR | None |


### Workflow: LeaveApproval

- **Target DocType**: `LeaveApplication`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Pending Approval` | Submitted (1) | `pending` | Pending Approval state |
| `Approved` | Submitted (1) | `positive` | Approved state |
| `Rejected` | Cancelled (2) | `negative` | Rejected state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Submit for approval** | `Pending Approval` | Employee, HR | `employee == user.employee || has_role('HR')` |
| `Pending Approval` | **Approve** | `Approved` | Employee | `leave_approver == user.employee && employee != user.employee` |
| `Pending Approval` | **Reject** | `Rejected` | Employee | `leave_approver == user.employee && employee != user.employee` |
| `Pending Approval` | **Approve (HR)** | `Approved` | HR | `employee != user.employee` |
| `Pending Approval` | **Withdraw** | `Cancelled` | Employee | `employee == user.employee` |
| `Approved` | **Cancel** | `Cancelled` | HR | None |


### Workflow: OvertimeApproval

- **Target DocType**: `OvertimeRequest`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Pending Approval` | Submitted (1) | `pending` | Pending Approval state |
| `Approved` | Submitted (1) | `positive` | Approved state |
| `Rejected` | Cancelled (2) | `negative` | Rejected state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Submit for approval** | `Pending Approval` | Employee, HR | `employee == user.employee || has_role('HR')` |
| `Pending Approval` | **Approve** | `Approved` | Employee | `approver == user.employee && employee != user.employee` |
| `Pending Approval` | **Reject** | `Rejected` | Employee | `approver == user.employee && employee != user.employee` |
| `Pending Approval` | **Approve (HR)** | `Approved` | HR | `employee != user.employee` |
| `Pending Approval` | **Withdraw** | `Cancelled` | Employee | `employee == user.employee` |
| `Approved` | **Cancel** | `Cancelled` | HR | None |


### Workflow: PurchaseRequestApproval

- **Target DocType**: `PurchaseRequest`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Pending Approval` | Submitted (1) | `pending` | Pending Approval state |
| `Approved` | Submitted (1) | `positive` | Approved state |
| `Rejected` | Cancelled (2) | `negative` | Rejected state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Submit for approval** | `Pending Approval` | Employee | `requested_by == user.employee` |
| `Pending Approval` | **Approve** | `Approved` | Employee | `approver == user.employee && requested_by != user.employee` |
| `Pending Approval` | **Reject** | `Rejected` | Employee | `approver == user.employee && requested_by != user.employee` |
| `Pending Approval` | **Withdraw** | `Cancelled` | Employee | `requested_by == user.employee` |
| `Approved` | **Cancel** | `Cancelled` | Purchasing | None |


### Workflow: QuotationLifecycle

- **Target DocType**: `Quotation`
- **Initial State**: `Draft`
- **State Field**: `workflow_state` (tracked via `docStatus`)

#### States

| State Name | Document Status | Badge Tone | Description |
|---|---|---|---|
| `Draft` | Draft (0) | `neutral` | Initial editable state |
| `Sent` | Submitted (1) | `pending` | Sent state |
| `Accepted` | Submitted (1) | `positive` | Accepted state |
| `Billed` | Submitted (1) | `positive` | Billed state |
| `Rejected` | Cancelled (2) | `negative` | Rejected state |
| `Expired` | Cancelled (2) | `negative` | Expired state |
| `Cancelled` | Cancelled (2) | `negative` | Cancelled state |


#### Transitions & Actions

| Current State | Action Button | Target State | Allowed Roles | Guard Condition |
|---|---|---|---|---|
| `Draft` | **Send to customer** | `Sent` | Sales, Manager | `sales_rep == user.employee || has_role('Manager')` |
| `Sent` | **Customer accepted** | `Accepted` | Sales, Manager | `sales_rep == user.employee || has_role('Manager')` |
| `Sent` | **Customer rejected** | `Rejected` | Sales, Manager | `sales_rep == user.employee || has_role('Manager')` |
| `Sent` | **Mark expired** | `Expired` | System Manager, Manager | None |
| `Accepted` | **Mark billed** | `Billed` | System Manager, Finance | None |
| `Accepted` | **Cancel** | `Cancelled` | Manager | None |

