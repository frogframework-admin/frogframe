# ACCOUNTING translations (ACC-001 to ACC-048)
ACC_TRANSLATIONS = {
    "ACC-001": {
        "name": "Manual Journal Count",
        "definition": "Number of manual journal entries posted per period",
        "formula": "COUNT(JournalEntry docstatus=1 in period)"
    },
    "ACC-002": {
        "name": "Manual Journal Ratio",
        "definition": "Manual journals compared to total general ledger transactions",
        "formula": "COUNT(JournalEntry) / COUNT(all GL vouchers) x100"
    },
    "ACC-003": {
        "name": "Journals Without Supporting Document",
        "definition": "Manual journal entries posted without attached supporting documentation",
        "formula": "COUNT(supporting_document IS NULL) / COUNT(JournalEntry) x100"
    },
    "ACC-004": {
        "name": "Rejected Unbalanced Journals",
        "definition": "Journal entries created with debit/credit difference not equal to zero",
        "formula": "COUNT(difference != 0 AND docstatus=0)"
    },
    "ACC-005": {
        "name": "Auto-reversal Compliance",
        "definition": "Accrual journals marked for auto-reversal that have been reversed",
        "formula": "COUNT(auto_reverse=1 AND has reversal_of) / COUNT(auto_reverse=1 AND reversal_date <= CURRENT_DATE) x100"
    },
    "ACC-006": {
        "name": "Cancelled / Amended Journals",
        "definition": "Percentage of journal entries cancelled or amended post-submission",
        "formula": "COUNT(docstatus=2) / COUNT(JournalEntry) x100"
    },
    "ACC-007": {
        "name": "Backdated Postings",
        "definition": "Transactions posted with effective dates preceding system entry by threshold",
        "formula": "COUNT(creation − posting_date > AccountingSettings.backdate_limit_days)"
    },
    "ACC-008": {
        "name": "Future-dated Postings",
        "definition": "Transactions entered with posting dates set in future periods",
        "formula": "COUNT(posting_date > creation date)"
    },
    "ACC-009": {
        "name": "P&L Lines Without Cost Center",
        "definition": "Profit and loss journal entry lines missing an assigned cost center",
        "formula": "COUNT(JournalEntryLine account.report_type='Profit and Loss' AND cost_center IS NULL)"
    },
    "ACC-010": {
        "name": "Direct Postings to Control Accounts",
        "definition": "Manual journal lines posting directly to AR/AP control accounts without subledger party",
        "formula": "COUNT(JournalEntryLine account.is_control_account=1 AND party IS NULL)"
    },
    "ACC-011": {
        "name": "Postings to Frozen / Inactive Accounts",
        "definition": "Attempted postings to frozen accounts or deactivated accounts",
        "formula": "COUNT(postings to Account is_frozen=1 OR is_active=0)"
    },
    "ACC-012": {
        "name": "Realized FX Gain / Loss",
        "definition": "Net realized foreign exchange gain or loss posted to general ledger",
        "formula": "SUM(GL balance of Company.exchange_gain_loss_account)"
    },
    "ACC-013": {
        "name": "Round-off Difference",
        "definition": "Total monetary adjustments absorbed by rounding difference accounts",
        "formula": "SUM(ABS(GL round_off_account))"
    },
    "ACC-014": {
        "name": "Trial Balance Integrity Check",
        "definition": "Variance between total debits and total credits on trial balance",
        "formula": "SUM(debit) − SUM(credit) (TrialBalanceReport)"
    },
    "ACC-015": {
        "name": "Suspense / Clearing Account Balance",
        "definition": "Ending balance in clearing accounts (GRNI, unallocated suspense)",
        "formula": "SUM(GL StockSettings.grni_account + suspense accounts)"
    },
    "ACC-016": {
        "name": "Days to Close",
        "definition": "Working days from fiscal month end to official accounting period closure",
        "formula": "AVG(closed_on − end_date) working days"
    },
    "ACC-017": {
        "name": "Overdue Open Fiscal Periods",
        "definition": "Fiscal periods remaining open more than 10 days past end date",
        "formula": "COUNT(status='Open' AND end_date < CURRENT_DATE − 10)"
    },
    "ACC-018": {
        "name": "Sub-ledger Lock Compliance",
        "definition": "Closed periods where AR, AP, Fixed Asset, and Payroll subledgers are locked",
        "formula": "COUNT(lock_receivable AND lock_payable AND lock_fixed_asset AND lock_payroll) / COUNT(Closed) x100"
    },
    "ACC-019": {
        "name": "Post-close Adjustments",
        "definition": "Transactions posted into previously closed fiscal periods via reopen",
        "formula": "COUNT(voucher posting_date <= period.end_date AND creation > closed_on)"
    },
    "ACC-020": {
        "name": "Books Frozen Lag",
        "definition": "Days elapsed between prior month end and current books_frozen_until cutoff",
        "formula": "Prior month end − AccountingSettings.books_frozen_until"
    },
    "ACC-021": {
        "name": "Year-end Closing Completed",
        "definition": "Fiscal years closed with submitted Period Closing Vouchers",
        "formula": "COUNT(PeriodClosingVoucher docstatus=1 per fiscal_year)"
    },
    "ACC-022": {
        "name": "Intercompany Balance Mismatch",
        "definition": "Reconciliation imbalance between internal customer AR and internal vendor AP",
        "formula": "SUM(Internal customer AR) − SUM(Internal vendor AP)"
    },
    "ACC-023": {
        "name": "Exchange Rate Coverage at Close",
        "definition": "Active foreign currencies with verified period-end revaluation rates",
        "formula": "COUNT(CurrencyExchangeRate rate_date=period end) / COUNT(Active foreign currencies)"
    },
    "ACC-024": {
        "name": "Total Revenue",
        "definition": "Net operating revenue reported on the profit and loss statement",
        "formula": "SUM(GL accounts where root_type='Income')"
    },
    "ACC-025": {
        "name": "Gross Profit Margin",
        "definition": "Gross profit margin percentage (Revenue minus COGS over Revenue)",
        "formula": "(Income − COGS (StockSettings.cogs_account)) / Income x100"
    },
    "ACC-026": {
        "name": "Operating Expense Ratio",
        "definition": "Operating expenses (SG&A) compared to net revenue",
        "formula": "SUM(Operating expenses excluding COGS) / Income x100"
    },
    "ACC-027": {
        "name": "EBITDA Margin",
        "definition": "Operating earnings before interest, taxes, depreciation, and amortization margin",
        "formula": "(Operating profit + depreciation) / Income x100"
    },
    "ACC-028": {
        "name": "Net Profit Margin",
        "definition": "Net profit after taxes compared to net revenue",
        "formula": "PeriodClosingVoucher.net_profit or (Income − Expense) / Income x100"
    },
    "ACC-029": {
        "name": "Current Ratio",
        "definition": "Short-term liquidity ratio of current assets to current liabilities",
        "formula": "SUM(Current assets) / SUM(Current liabilities) (BalanceSheetReport)"
    },
    "ACC-030": {
        "name": "Quick Ratio",
        "definition": "Acid-test liquidity ratio of cash and receivables to current liabilities",
        "formula": "(Cash + AR) / Current liabilities"
    },
    "ACC-031": {
        "name": "Debt to Equity Ratio",
        "definition": "Financial leverage ratio of total liabilities to shareholders' equity",
        "formula": "SUM(root_type='Liability') / SUM(root_type='Equity')"
    },
    "ACC-032": {
        "name": "Working Capital",
        "definition": "Operational net liquidity (current assets minus current liabilities)",
        "formula": "Current assets − Current liabilities"
    },
    "ACC-033": {
        "name": "Cash Conversion Cycle",
        "definition": "Operating cycle duration in days (DIO + DSO − DPO)",
        "formula": "DIO + DSO − DPO"
    },
    "ACC-034": {
        "name": "Return on Assets (ROA)",
        "definition": "Net income generated per unit of total assets",
        "formula": "Net profit / SUM(root_type='Asset') x100"
    },
    "ACC-035": {
        "name": "Return on Equity (ROE)",
        "definition": "Net income generated per unit of shareholders' equity",
        "formula": "Net profit / SUM(root_type='Equity') x100"
    },
    "ACC-036": {
        "name": "Profit by Cost Center",
        "definition": "Net contribution profit generated by individual cost centers",
        "formula": "SUM(Income − Expense) GROUP BY cost_center"
    },
    "ACC-037": {
        "name": "Profit by Accounting Dimension",
        "definition": "Net profit analyzed across custom accounting dimensions (location, project)",
        "formula": "SUM(Income − Expense) GROUP BY dimension"
    },
    "ACC-038": {
        "name": "Budget vs Actual Variance",
        "definition": "Percentage variance between actual expenditures and planned budget",
        "formula": "(Actual − Budget) / Budget x100 (BudgetVsActualReport)"
    },
    "ACC-039": {
        "name": "Budget Utilization",
        "definition": "Year-to-date actual expenditure compared to annualized budget",
        "formula": "Actual YTD / total_budget x100"
    },
    "ACC-040": {
        "name": "Accounts Exceeding Budget",
        "definition": "Number of GL accounts or cost centers where actual spend exceeds budget",
        "formula": "COUNT(BudgetAccount actual > annual_amount)"
    },
    "ACC-041": {
        "name": "Budget Overrun Events",
        "definition": "Transactions triggering budget overrun warning or blocking actions",
        "formula": "COUNT(budget Stop/Warn trigger events)"
    },
    "ACC-042": {
        "name": "Cost Centers Without Budget",
        "definition": "Active operational cost centers lacking an active annual budget allocation",
        "formula": "COUNT(CostCenter is_active=1 AND is_group=0 without active annual Budget)"
    },
    "ACC-043": {
        "name": "Forecast Accuracy",
        "definition": "Financial forecasting precision compared to realized accounting actuals",
        "formula": "1 − |Actual − Forecast| / Actual x100"
    },
    "ACC-044": {
        "name": "Project Profit Margin",
        "definition": "Net project profitability (contract value minus actual project costs)",
        "formula": "(contract_value − SUM(GL project expenses)) / contract_value x100"
    },
    "ACC-045": {
        "name": "Project Cost vs Estimate",
        "definition": "Actual project expenditure compared to initial engineering cost estimate",
        "formula": "SUM(Project costs) / estimated_cost x100"
    },
    "ACC-046": {
        "name": "Project Billing Progress",
        "definition": "Invoices billed to date compared to total contracted project value",
        "formula": "SUM(SalesInvoice.tax_base WHERE project) / contract_value x100"
    },
    "ACC-047": {
        "name": "Over-schedule Projects",
        "definition": "In-progress customer projects where scheduled end date has passed",
        "formula": "COUNT(status='In Progress' AND end_date < CURRENT_DATE)"
    },
    "ACC-048": {
        "name": "On-hold Projects",
        "definition": "Projects currently suspended in On Hold status",
        "formula": "COUNT(status='On Hold')"
    }
}
