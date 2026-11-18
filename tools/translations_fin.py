# FINANCE translations (FIN-001 to FIN-084)
FIN_TRANSLATIONS = {
    "FIN-001": {
        "name": "Total Outstanding Receivables",
        "definition": "Outstanding balance of unpaid sales invoices",
        "formula": "SUM(SalesInvoice.outstanding_amount, docstatus=1) − SUM(SalesCreditNote.outstanding_amount)"
    },
    "FIN-002": {
        "name": "DSO (Days Sales Outstanding)",
        "definition": "Average collection period for receivables in days",
        "formula": "(Outstanding receivables / Credit sales in period) × days in period"
    },
    "FIN-003": {
        "name": "Overdue Receivables",
        "definition": "Receivables past due date",
        "formula": "SUM(outstanding_amount WHERE due_date < CURRENT_DATE)"
    },
    "FIN-004": {
        "name": "Overdue AR %",
        "definition": "Proportion of receivables past due",
        "formula": "Overdue AR / Total AR x100"
    },
    "FIN-005": {
        "name": "AR Aging > 90 Days",
        "definition": "Receivables overdue by more than 90 days",
        "formula": "SUM(outstanding WHERE CURRENT_DATE − due_date > 90) / Total AR x100"
    },
    "FIN-006": {
        "name": "Collection Effectiveness Index (CEI)",
        "definition": "Collection effectiveness achieved during the period",
        "formula": "(Beginning AR + Sales − Ending AR) / (Beginning AR + Sales − Ending current AR) x100"
    },
    "FIN-007": {
        "name": "Average Days to Pay",
        "definition": "Average days elapsed from invoice date to customer payment",
        "formula": "AVG(PaymentEntry.posting_date − SalesInvoice.posting_date) via references"
    },
    "FIN-008": {
        "name": "Customers Over Credit Limit",
        "definition": "Number of customers with outstanding balances exceeding credit limit",
        "formula": "COUNT(Customer WHERE SUM(outstanding) > credit_limit)"
    },
    "FIN-009": {
        "name": "Credit Limit Utilization",
        "definition": "Overall customer credit limit exposure",
        "formula": "SUM(outstanding) / SUM(credit_limit) x100"
    },
    "FIN-010": {
        "name": "Credit Note Ratio",
        "definition": "Sales credit notes issued compared to gross revenue",
        "formula": "SUM(SalesCreditNote.grand_total) / SUM(SalesInvoice.grand_total) x100"
    },
    "FIN-011": {
        "name": "Credit Notes by Reason",
        "definition": "Breakdown of credit notes by reason (returns, discounts, price adjustments, cancellations)",
        "formula": "SUM(grand_total) GROUP BY reason"
    },
    "FIN-012": {
        "name": "Unapplied Customer Payments",
        "definition": "Customer receipts not yet allocated to specific invoices",
        "formula": "SUM(PaymentEntry.unallocated_amount WHERE payment_type='Receive')"
    },
    "FIN-013": {
        "name": "Payment Reconciliation Backlog",
        "definition": "Number of unapplied payment entries pending reconciliation",
        "formula": "COUNT(PaymentEntry unallocated_amount > 0)"
    },
    "FIN-014": {
        "name": "Invoices Without Tax Invoice",
        "definition": "Taxable sales invoices missing official tax invoice numbers",
        "formula": "COUNT(SalesInvoice vat_amount > 0 AND tax_invoice_no IS NULL)"
    },
    "FIN-015": {
        "name": "Net Sales",
        "definition": "Total sales revenue net of credit notes",
        "formula": "SUM(SalesInvoice.tax_base) − SUM(SalesCreditNote.tax_base)"
    },
    "FIN-016": {
        "name": "Top 10 AR Concentration",
        "definition": "Share of outstanding receivables held by the top 10 customers",
        "formula": "SUM(outstanding top 10 customers) / Total AR x100"
    },
    "FIN-017": {
        "name": "Bad Debt Write-off Ratio",
        "definition": "Uncollectible receivables written off compared to sales",
        "formula": "SUM(JournalEntry write-offs to bad debt accounts) / Sales x100"
    },
    "FIN-018": {
        "name": "Invoices With Milestone Terms",
        "definition": "Percentage of invoices utilizing payment schedules",
        "formula": "COUNT(payment_schedule >= 2 lines) / COUNT(SalesInvoice) x100"
    },
    "FIN-019": {
        "name": "Total Outstanding Payables",
        "definition": "Outstanding balance of unpaid purchase invoices",
        "formula": "SUM(PurchaseInvoice.outstanding_amount, docstatus=1) − SUM(PurchaseDebitNote.outstanding_amount)"
    },
    "FIN-020": {
        "name": "DPO (Days Payable Outstanding)",
        "definition": "Average time taken to pay vendor bills in days",
        "formula": "(Outstanding payables / Purchases in period) × days in period"
    },
    "FIN-021": {
        "name": "On-time Payment Rate",
        "definition": "Vendor payments executed on or before invoice due date",
        "formula": "COUNT(PaymentEntryReference allocated <= due_date) / COUNT x100"
    },
    "FIN-022": {
        "name": "Late Payment Amount",
        "definition": "Total monetary value of overdue vendor payables not on hold",
        "formula": "SUM(outstanding WHERE due_date < CURRENT_DATE AND on_hold=0)"
    },
    "FIN-023": {
        "name": "Invoices On Hold",
        "definition": "Vendor invoices with payment withheld on hold",
        "formula": "COUNT / SUM(outstanding WHERE on_hold=1)"
    },
    "FIN-024": {
        "name": "Invoice Processing Time",
        "definition": "Days from vendor bill date to invoice submission in system",
        "formula": "AVG(posting_date − bill_date)"
    },
    "FIN-025": {
        "name": "PO-backed Invoice Rate",
        "definition": "Purchase invoices originating from purchase orders",
        "formula": "COUNT(purchase_order filled) / COUNT(PurchaseInvoice) x100"
    },
    "FIN-026": {
        "name": "3-Way Match Exception Rate",
        "definition": "Invoices with PO/GR/Invoice price or quantity discrepancies",
        "formula": "COUNT(invoices with qty/rate variance vs GoodsReceiptItem/PurchaseOrderItem) / COUNT x100"
    },
    "FIN-027": {
        "name": "Duplicate Bill Numbers",
        "definition": "Vendor invoices sharing duplicate external bill numbers",
        "formula": "COUNT(GROUP BY vendor, bill_no HAVING COUNT(*) > 1)"
    },
    "FIN-028": {
        "name": "Debit Note Ratio",
        "definition": "Purchase debit notes issued compared to gross purchases",
        "formula": "SUM(PurchaseDebitNote.grand_total) / SUM(PurchaseInvoice.grand_total) x100"
    },
    "FIN-029": {
        "name": "Unapplied Vendor Payments",
        "definition": "Disbursements to vendors not yet allocated to invoices",
        "formula": "SUM(PaymentEntry.unallocated_amount WHERE payment_type='Pay')"
    },
    "FIN-030": {
        "name": "Early Payment Discount Captured",
        "definition": "Early settlement supplier discounts realized vs available",
        "formula": "SUM(discounts captured) / SUM(discounts available per PaymentTermDetail) x100"
    },
    "FIN-031": {
        "name": "Purchase Invoices Without Attachment",
        "definition": "Vendor bills missing digital scan or attachment",
        "formula": "COUNT(attachment IS NULL) / COUNT x100"
    },
    "FIN-032": {
        "name": "Cash & Bank Balance",
        "definition": "Total book balance across all operational bank accounts",
        "formula": "SUM(GL balance of BankAccount.gl_account)"
    },
    "FIN-033": {
        "name": "Cash Runway",
        "definition": "Estimated operational months remaining based on average burn",
        "formula": "Cash balance / Average monthly cash disbursements (PaymentEntry Pay)"
    },
    "FIN-034": {
        "name": "Net Cash Flow",
        "definition": "Cash receipts minus cash disbursements across all payment entries",
        "formula": "SUM(paid_amount Receive) − SUM(paid_amount Pay)"
    },
    "FIN-035": {
        "name": "Bank Reconciliation Timeliness",
        "definition": "Bank reconciliations completed within 3 working days post month-end",
        "formula": "COUNT(BankReconciliation docstatus=1 within 3 working days of period_end) / COUNT(Active BankAccounts) x100"
    },
    "FIN-036": {
        "name": "Bank Reconciliation Difference",
        "definition": "Total unexplained bank reconciliation variance",
        "formula": "SUM(ABS(difference))"
    },
    "FIN-037": {
        "name": "Uncleared Items",
        "definition": "Book transactions not yet cleared on bank statements",
        "formula": "SUM(total_uncleared); COUNT(BankReconciliationItem is_cleared=0)"
    },
    "FIN-038": {
        "name": "Aged Uncleared Items > 30 Days",
        "definition": "Uncleared bank reconciliation items outstanding > 30 days",
        "formula": "COUNT(is_cleared=0 AND period_end − posting_date > 30)"
    },
    "FIN-039": {
        "name": "Bank Statement Auto-match Rate",
        "definition": "Bank statement lines matched automatically by reconciliation rules",
        "formula": "COUNT(BankStatementLine match_status='Matched') / COUNT(lines excluding 'Ignored') x100"
    },
    "FIN-040": {
        "name": "Unmatched Bank Lines",
        "definition": "Bank statement transaction lines pending manual reconciliation",
        "formula": "COUNT(match_status='Unmatched')"
    },
    "FIN-041": {
        "name": "Statement Balance Integrity Check",
        "definition": "Bank statements where calculated closing does not match stated closing",
        "formula": "COUNT(BankStatement computed_closing != closing_balance)"
    },
    "FIN-042": {
        "name": "Payment Run Efficiency",
        "definition": "Number of payment entries processed per batch payment run",
        "formula": "SUM(payments_created) / COUNT(PaymentRun)"
    },
    "FIN-043": {
        "name": "Payment Run Coverage",
        "definition": "Due supplier payables successfully included in payment runs",
        "formula": "SUM(PaymentRun.total_amount) / SUM(due payables <= due_until) x100"
    },
    "FIN-044": {
        "name": "Vendors Without Bank Account",
        "definition": "Active suppliers lacking electronic banking payment details",
        "formula": "COUNT(Vendor is_active=1 AND bank_accounts is empty)"
    },
    "FIN-045": {
        "name": "FX Exposure",
        "definition": "Total outstanding foreign currency receivable and payable exposure",
        "formula": "SUM(outstanding_amount × exchange_rate WHERE currency != base_currency)"
    },
    "FIN-046": {
        "name": "Missing Daily Exchange Rates",
        "definition": "Active currency pairs missing foreign exchange rate for today",
        "formula": "COUNT(active currency pairs without CurrencyExchangeRate rate_date=CURRENT_DATE)"
    },
    "FIN-047": {
        "name": "Total Expense Claims",
        "definition": "Total employee business expense reimbursements claimed",
        "formula": "SUM(ExpenseClaim.total_claimed, approved)"
    },
    "FIN-048": {
        "name": "Expense Claim per Employee",
        "definition": "Average business reimbursement expense claimed per employee",
        "formula": "SUM(total_claimed) / headcount"
    },
    "FIN-049": {
        "name": "Expense Claim Cycle Time",
        "definition": "Average days from claim submission date to payment disbursement",
        "formula": "AVG(PaymentEntry.posting_date − ExpenseClaim.posting_date)"
    },
    "FIN-050": {
        "name": "Expense Approval Turnaround",
        "definition": "Average workflow approval turnaround time for expense claims",
        "formula": "AVG(workflow transition hours to Approved)"
    },
    "FIN-051": {
        "name": "Expense Rejection Rate",
        "definition": "Percentage of employee expense claims rejected",
        "formula": "COUNT(state='Rejected') / COUNT(Submitted) x100"
    },
    "FIN-052": {
        "name": "Receipt Compliance",
        "definition": "Percentage of mandatory-receipt expense lines with receipts attached",
        "formula": "COUNT(ExpenseClaimItem receipt attached WHERE type.requires_receipt=1) / COUNT(Mandatory) x100"
    },
    "FIN-053": {
        "name": "Policy Breach (Over Limit)",
        "definition": "Claim items submitted exceeding maximum policy allowance",
        "formula": "COUNT(amount > ExpenseClaimType.max_amount_per_item)"
    },
    "FIN-054": {
        "name": "Entertainment Expense Spend",
        "definition": "Total entertainment claims incurred requiring fiscal nominative reporting",
        "formula": "SUM(amount WHERE type.is_entertainment=1)"
    },
    "FIN-055": {
        "name": "Expenses by Project",
        "definition": "Employee reimbursement costs allocated to specific projects",
        "formula": "SUM(total_claimed) GROUP BY project"
    },
    "FIN-056": {
        "name": "Outstanding Employee Advances",
        "definition": "Total employee advances outstanding pending settlement",
        "formula": "SUM(paid_amount − settled_amount − returned_amount) WHERE advance_status != 'Completed'"
    },
    "FIN-057": {
        "name": "Overdue Advance Settlements",
        "definition": "Employee advances past settlement due date not yet resolved",
        "formula": "COUNT(settle_by_date < CURRENT_DATE AND advance_status IN ('Paid','Partly Settled'))"
    },
    "FIN-058": {
        "name": "Advance Settlement Ratio",
        "definition": "Proportion of employee advances settled via expense claims",
        "formula": "SUM(settled_amount) / SUM(paid_amount) x100"
    },
    "FIN-059": {
        "name": "Unpaid Approved Claims",
        "definition": "Approved expense reimbursements awaiting payment disbursement",
        "formula": "SUM(net_payable WHERE payment_status IN ('Unpaid','Partly Paid'))"
    },
    "FIN-060": {
        "name": "Output VAT (PPN Keluaran)",
        "definition": "Total output value-added tax collected on sales",
        "formula": "SUM(SalesInvoice.vat_amount) − SUM(SalesCreditNote.vat_amount)"
    },
    "FIN-061": {
        "name": "Input VAT (PPN Masukan)",
        "definition": "Total creditable input value-added tax paid on purchases",
        "formula": "SUM(PurchaseInvoice.vat_amount) − SUM(PurchaseDebitNote.vat_amount)"
    },
    "FIN-062": {
        "name": "VAT Payable / Overpaid",
        "definition": "Net VAT balance payable to tax authority or refundable",
        "formula": "Output VAT − Credited Input VAT"
    },
    "FIN-063": {
        "name": "Tax Invoice Approval Rate",
        "definition": "Tax invoices officially validated and approved by tax authority (DJP)",
        "formula": "COUNT(TaxInvoice status='Approved') / COUNT(status != 'Cancelled') x100"
    },
    "FIN-064": {
        "name": "Tax Invoices Pending Upload",
        "definition": "Draft tax invoices pending submission or export to e-Faktur",
        "formula": "COUNT(TaxInvoice status='Draft')"
    },
    "FIN-065": {
        "name": "Replacement Tax Invoice Rate",
        "definition": "Percentage of tax invoices requiring replacement (e-Faktur pengganti)",
        "formula": "COUNT(status='Replaced') / COUNT(TaxInvoice) x100"
    },
    "FIN-066": {
        "name": "Uncredited Input Tax Invoices",
        "definition": "Input tax invoices not yet claimed in a VAT tax return period",
        "formula": "COUNT(direction='Input' AND credited_period IS NULL)"
    },
    "FIN-067": {
        "name": "Withholding Tax (PPh 23) Payable",
        "definition": "Total income tax withheld from vendor payments (PPh 23/4(2))",
        "formula": "SUM(PurchaseInvoice.wht_amount)"
    },
    "FIN-068": {
        "name": "Withholding Certificates Issued",
        "definition": "Purchase invoices with tax withholding where tax certificate was issued",
        "formula": "COUNT(WithholdingCertificate direction='We withhold') / COUNT(PurchaseInvoice wht_amount > 0) x100"
    },
    "FIN-069": {
        "name": "Customer Withholding Certificates Received",
        "definition": "Deductions withheld by customers with verified certificates collected",
        "formula": "COUNT(WithholdingCertificate direction='Withheld by counterparty') / COUNT(PaymentEntryDeduction WHT) x100"
    },
    "FIN-070": {
        "name": "Effective Tax Rate",
        "definition": "Corporate income tax expense compared to pre-tax accounting profit",
        "formula": "Corporate income tax expense / Profit before tax x100 (ProfitAndLossReport)"
    },
    "FIN-071": {
        "name": "Taxable Counterparties Without Tax ID",
        "definition": "Taxable vendor or customer master records missing valid tax ID (NPWP)",
        "formula": "COUNT(is_pkp=1 AND tax_id IS NULL)"
    },
    "FIN-072": {
        "name": "Fixed Assets Net Book Value",
        "definition": "Gross asset acquisition cost minus cumulative depreciation",
        "formula": "SUM(gross_purchase_amount − opening_accumulated_depreciation − SUM(posted depreciation))"
    },
    "FIN-073": {
        "name": "Period Depreciation Expense",
        "definition": "Total depreciation posted to general ledger for the period",
        "formula": "SUM(DepreciationRun.total_depreciation)"
    },
    "FIN-074": {
        "name": "Depreciation Run Completion",
        "definition": "Percentage of accounting periods with completed monthly depreciation runs",
        "formula": "COUNT(DepreciationRun per accounting_period) / COUNT(AccountingPeriod) x100"
    },
    "FIN-075": {
        "name": "Unposted Depreciation Schedules",
        "definition": "Asset depreciation schedule rows past due date not yet posted",
        "formula": "COUNT(AssetDepreciationSchedule schedule_date <= CURRENT_DATE AND is_posted=0)"
    },
    "FIN-076": {
        "name": "Average Asset Depreciated %",
        "definition": "Cumulative depreciation compared to gross acquisition cost",
        "formula": "SUM(Accumulated depreciation) / SUM(gross_purchase_amount) x100"
    },
    "FIN-077": {
        "name": "Fully Depreciated Assets In Use",
        "definition": "Assets completely written off but still actively in service",
        "formula": "COUNT(status='Fully Depreciated')"
    },
    "FIN-078": {
        "name": "Disposal Gain / Loss",
        "definition": "Net gain or loss realized on fixed asset retirements and disposals",
        "formula": "SUM(AssetDisposal.gain_loss)"
    },
    "FIN-079": {
        "name": "Write-offs & Lost Assets",
        "definition": "Net book value of assets scrapped, lost, or written off",
        "formula": "COUNT / SUM(book_value WHERE disposal_type IN ('Write-off','Lost/Damaged'))"
    },
    "FIN-080": {
        "name": "Assets Without Custodian",
        "definition": "Active fixed assets missing an assigned custodian or owner",
        "formula": "COUNT(Asset status='Active' AND custodian IS NULL)"
    },
    "FIN-081": {
        "name": "Assets Without Location / Cost Center",
        "definition": "Active fixed assets lacking an assigned physical location or cost center",
        "formula": "COUNT(location IS NULL OR cost_center IS NULL)"
    },
    "FIN-082": {
        "name": "Period Capex",
        "definition": "Total capital expenditures invested in new fixed assets during period",
        "formula": "SUM(gross_purchase_amount WHERE purchase_date in period)"
    },
    "FIN-083": {
        "name": "Draft Assets Not Activated",
        "definition": "Capital assets purchased over 30 days ago remaining in Draft state",
        "formula": "COUNT(status='Draft' AND purchase_date < CURRENT_DATE − 30)"
    },
    "FIN-084": {
        "name": "Capex to Depreciation Ratio",
        "definition": "Capital expenditure investment compared to periodic depreciation charge",
        "formula": "Capex / Depreciation expense"
    }
}
