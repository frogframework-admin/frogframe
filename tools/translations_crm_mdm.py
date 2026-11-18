# CRM & CORE/MDM translations (CRM-001 to CRM-055, MDM-001 to MDM-010)
CRM_MDM_TRANSLATIONS = {
    # CRM (55)
    "CRM-001": {
        "name": "New Lead Volume",
        "definition": "Total number of new sales leads generated in the period",
        "formula": "COUNT(Lead created in period)"
    },
    "CRM-002": {
        "name": "Lead Conversion Rate",
        "definition": "Percentage of leads successfully converted to opportunities or customers",
        "formula": "COUNT(status='Converted') / COUNT(Lead) x100"
    },
    "CRM-003": {
        "name": "Lead Qualification Rate",
        "definition": "Percentage of leads qualifying as sales-ready (Qualified or Converted)",
        "formula": "COUNT(status IN ('Qualified','Converted')) / COUNT(Lead) x100"
    },
    "CRM-004": {
        "name": "Lead Response Time",
        "definition": "Average hours elapsed from lead creation to initial outreach activity",
        "formula": "AVG(MIN(Activity.start_at) − Lead.creation)"
    },
    "CRM-005": {
        "name": "Time to Convert",
        "definition": "Average days required from lead generation to formal conversion",
        "formula": "AVG(converted_on − creation)"
    },
    "CRM-006": {
        "name": "Leads by Source",
        "definition": "Distribution and conversion efficiency analyzed across lead sources",
        "formula": "COUNT and conversion rate GROUP BY lead_source"
    },
    "CRM-007": {
        "name": "Hot Lead Ratio",
        "definition": "Percentage of prospective leads categorized with Hot priority rating",
        "formula": "COUNT(rating='Hot') / COUNT(Lead) x100"
    },
    "CRM-008": {
        "name": "Unqualified Rate & Reasons",
        "definition": "Distribution of disqualified leads analyzed by disqualification reason",
        "formula": "COUNT(status='Unqualified') GROUP BY disqualify_reason"
    },
    "CRM-009": {
        "name": "Unassigned Leads",
        "definition": "Active prospects without an assigned sales representative owner",
        "formula": "COUNT(sales_rep IS NULL AND status IN ('New','Contacted'))"
    },
    "CRM-010": {
        "name": "Marketing Campaign ROI",
        "definition": "Return on investment achieved from closed-won revenue against campaign cost",
        "formula": "(won_amount − actual_cost) / actual_cost x100"
    },
    "CRM-011": {
        "name": "Cost per Lead (CPL)",
        "definition": "Average marketing acquisition cost per generated lead",
        "formula": "actual_cost / lead_count"
    },
    "CRM-012": {
        "name": "Campaign Budget Variance",
        "definition": "Percentage variance between actual campaign expenditure and budget",
        "formula": "(actual_cost − budgeted_cost) / budgeted_cost x100"
    },
    "CRM-013": {
        "name": "Campaign Revenue Attainment",
        "definition": "Won opportunity revenue realized against initial campaign projection",
        "formula": "won_amount / expected_revenue x100"
    },
    "CRM-014": {
        "name": "Campaign Lead-to-Opportunity Rate",
        "definition": "Proportion of campaign leads converted into formal sales opportunities",
        "formula": "opportunity_count / lead_count x100"
    },
    "CRM-015": {
        "name": "Total Pipeline Value",
        "definition": "Total aggregate potential value of open sales opportunities",
        "formula": "SUM(amount WHERE stage_type='Open')"
    },
    "CRM-016": {
        "name": "Weighted Pipeline Value",
        "definition": "Probability-weighted forecast value of open sales pipeline",
        "formula": "SUM(weighted_amount) WHERE stage_type='Open'"
    },
    "CRM-017": {
        "name": "Pipeline Coverage Ratio",
        "definition": "Open pipeline revenue multiple relative to periodic sales quota",
        "formula": "Pipeline value / Quota target for period"
    },
    "CRM-018": {
        "name": "Opportunity Win Rate",
        "definition": "Percentage of closed opportunities won by deal count",
        "formula": "COUNT(stage_type='Won') / COUNT(stage_type IN ('Won','Lost')) x100"
    },
    "CRM-019": {
        "name": "Win Rate by Value",
        "definition": "Percentage of closed opportunity value won in currency",
        "formula": "SUM(amount Won) / SUM(amount Won + Lost) x100"
    },
    "CRM-020": {
        "name": "Average Deal Size",
        "definition": "Average monetary contract value of closed-won opportunities",
        "formula": "AVG(amount WHERE stage_type='Won')"
    },
    "CRM-021": {
        "name": "Sales Cycle Length",
        "definition": "Average elapsed days from opportunity creation to Won closing date",
        "formula": "AVG(closed_date − creation)"
    },
    "CRM-022": {
        "name": "Sales Velocity",
        "definition": "Expected revenue velocity ($/day = Opportunities × Win Rate × Deal Size / Cycle)",
        "formula": "(# open opportunities × win rate × avg deal size) / avg cycle length"
    },
    "CRM-023": {
        "name": "Stale Opportunities",
        "definition": "Open opportunities with no activity or updates beyond configured threshold",
        "formula": "COUNT(stage_type='Open' AND modified < CURRENT_DATE − stale_days)"
    },
    "CRM-024": {
        "name": "Overdue Close Date Opportunities",
        "definition": "Open sales opportunities where target close date has expired",
        "formula": "COUNT(stage_type='Open' AND expected_close_date < CURRENT_DATE)"
    },
    "CRM-025": {
        "name": "Lost Deal Reason Analysis",
        "definition": "Distribution of lost opportunities categorized by primary lost reason",
        "formula": "COUNT(stage_type='Lost') GROUP BY lost_reason"
    },
    "CRM-026": {
        "name": "Competitive Win Rate",
        "definition": "Win rate achieved on competitive deals with named rivals involved",
        "formula": "Win rate WHERE competitor IS NOT NULL"
    },
    "CRM-027": {
        "name": "Sales Stage Conversion Rate",
        "definition": "Progression conversion rate between sequential pipeline stages",
        "formula": "COUNT(reached stage n+1) / COUNT(entered stage n)"
    },
    "CRM-028": {
        "name": "Forecast Category Mix",
        "definition": "Pipeline value distribution across forecast tiers (Commit / Best Case / Pipeline)",
        "formula": "SUM(amount) GROUP BY forecast_category"
    },
    "CRM-029": {
        "name": "Buying Committee Multithreading",
        "definition": "Percentage of open enterprise deals with two or more stakeholder contact roles",
        "formula": "COUNT(contact_roles >= 2) / COUNT(open) x100"
    },
    "CRM-030": {
        "name": "Quote-to-Order Conversion",
        "definition": "Percentage of customer quotations converted into confirmed sales orders",
        "formula": "COUNT(SalesOrder with quotation) / COUNT(Submitted Quotations) x100"
    },
    "CRM-031": {
        "name": "Expired Quotations",
        "definition": "Quotations past valid-until date without customer acceptance or order",
        "formula": "COUNT(workflow_state='Expired' OR (workflow_state='Sent' AND valid_until < CURRENT_DATE))"
    },
    "CRM-032": {
        "name": "Average Quotation Discount",
        "definition": "Average percentage discount extended on quotation line items",
        "formula": "AVG(QuotationItem.discount_percent)"
    },
    "CRM-033": {
        "name": "Quotation Approval Turnaround",
        "definition": "Average workflow turnaround duration for quotation approval cycles",
        "formula": "AVG(Draft to Sent duration in QuotationLifecycle workflow)"
    },
    "CRM-034": {
        "name": "Time to Quote",
        "definition": "Days required from opportunity discovery to initial quote presentation",
        "formula": "AVG(Quotation.quotation_date − Opportunity.creation)"
    },
    "CRM-035": {
        "name": "Bookings (Sales Order Value)",
        "definition": "Total gross monetary bookings confirmed via submitted sales orders",
        "formula": "SUM(SalesOrder.grand_total, docstatus=1)"
    },
    "CRM-036": {
        "name": "Sales Order Backlog (Unbilled)",
        "definition": "Confirmed customer order value pending final billing fulfillment",
        "formula": "SUM(grand_total − billed_amount) WHERE billing_status != 'Fully billed'"
    },
    "CRM-037": {
        "name": "Order-to-Invoice Cycle Time",
        "definition": "Average days between sales order confirmation and primary invoice issuance",
        "formula": "AVG(SalesInvoice.posting_date − SalesOrder.order_date)"
    },
    "CRM-038": {
        "name": "On-time Delivery to Customer",
        "definition": "Sales orders delivered on or before customer promised delivery date",
        "formula": "COUNT(delivery <= delivery_date) / COUNT(SalesOrder) x100"
    },
    "CRM-039": {
        "name": "Sales Revenue by Representative",
        "definition": "Net invoiced sales volume achieved per individual sales representative",
        "formula": "SUM(SalesInvoice.tax_base) GROUP BY sales_rep"
    },
    "CRM-040": {
        "name": "Sales Revenue by Territory",
        "definition": "Invoiced sales performance distributed across geographic territories",
        "formula": "SUM(SalesInvoice.tax_base) GROUP BY Customer.territory"
    },
    "CRM-041": {
        "name": "Active Customer Count",
        "definition": "Number of unique customer accounts with invoice activity in the last 12 months",
        "formula": "COUNT(DISTINCT customer SalesInvoices in last 12 months)"
    },
    "CRM-042": {
        "name": "New Customer Acquisition",
        "definition": "First-time purchasing customer logos acquiring their inaugural invoice",
        "formula": "COUNT(customers with MIN(posting_date) in period)"
    },
    "CRM-043": {
        "name": "Customer Retention Rate",
        "definition": "Percentage of prior-year active customer accounts retained this year",
        "formula": "COUNT(active customers in current year ∩ prior year) / COUNT(prior year) x100"
    },
    "CRM-044": {
        "name": "Top 10 Customer Revenue Concentration",
        "definition": "Share of total company revenue contributed by top 10 customer accounts",
        "formula": "SUM(Top 10 customer revenue) / Total revenue x100"
    },
    "CRM-045": {
        "name": "Active Contract Value (ACV)",
        "definition": "Total aggregate annualized monetary value of active customer contracts",
        "formula": "SUM(contract_value WHERE contract_status='Active')"
    },
    "CRM-046": {
        "name": "Expiring Customer Contracts",
        "definition": "Active revenue contracts scheduled to terminate within advance notice window",
        "formula": "COUNT(contract_status IN ('Active','Expiring soon') AND end_date <= CURRENT_DATE + notice_days)"
    },
    "CRM-047": {
        "name": "Customer Contract Renewal Rate",
        "definition": "Percentage of concluding customer contracts successfully renewed",
        "formula": "COUNT(contract_status='Renewed') / COUNT(status IN ('Renewed','Expired')) x100"
    },
    "CRM-048": {
        "name": "Contract Revenue Churn Rate",
        "definition": "Annualized recurring contract value lost through expiration or cancellation",
        "formula": "SUM(contract_value status IN ('Expired','Cancelled')) / baseline contract value x100"
    },
    "CRM-049": {
        "name": "Auto-renewal Contract Ratio",
        "definition": "Percentage of customer service contracts containing evergreen auto-renewal clauses",
        "formula": "COUNT(auto_renew=1) / COUNT(Active contracts) x100"
    },
    "CRM-050": {
        "name": "Activities per Sales Representative",
        "definition": "Completed customer touchpoints (meetings, calls, tasks) per sales rep",
        "formula": "COUNT(Activity status='Completed') GROUP BY assigned_to"
    },
    "CRM-051": {
        "name": "Sales Activity Completion Rate",
        "definition": "Proportion of scheduled sales activities executed to completion",
        "formula": "COUNT(status='Completed') / COUNT(status IN ('Completed','Planned overdue')) x100"
    },
    "CRM-052": {
        "name": "Overdue Sales Activities",
        "definition": "Planned customer engagement activities past scheduled execution deadline",
        "formula": "COUNT(status='Planned' AND end_at < CURRENT_TIMESTAMP)"
    },
    "CRM-053": {
        "name": "Total Sales Commission Earned",
        "definition": "Total sales commission compensation earned across sales teams in period",
        "formula": "SUM(CommissionStatement.total_commission)"
    },
    "CRM-054": {
        "name": "Commission as % of Revenue",
        "definition": "Sales commissions earned compared to qualifying revenue base",
        "formula": "SUM(total_commission) / SUM(total_base) x100"
    },
    "CRM-055": {
        "name": "Unprocessed Sales Commissions",
        "definition": "Active sales representatives lacking a generated commission statement for the cycle",
        "formula": "COUNT(active reps in active plan without CommissionStatement in current cycle)"
    },

    # CORE / Master Data (10)
    "MDM-001": {
        "name": "Customer Master Completeness",
        "definition": "Active customer accounts with tax ID, primary address, contact, and payment terms",
        "formula": "COUNT(tax_id, address, contact, payment_term filled) / COUNT(Active) x100"
    },
    "MDM-002": {
        "name": "Vendor Master Completeness",
        "definition": "Active supplier records with tax ID, verified bank details, and payment terms",
        "formula": "COUNT(tax_id, bank accounts, payment_term filled) / COUNT(Active) x100"
    },
    "MDM-003": {
        "name": "Duplicate Customer / Vendor Records",
        "definition": "Potential duplicate master records identified by shared tax ID or legal name",
        "formula": "COUNT(GROUP BY tax_id HAVING COUNT(*) > 1)"
    },
    "MDM-004": {
        "name": "Items Without Default Accounting Accounts",
        "definition": "Active items missing assigned income or expense accounts on item and group levels",
        "formula": "COUNT(is_active=1 AND missing income_account or expense_account on item and group)"
    },
    "MDM-005": {
        "name": "Expired Item Price Lists",
        "definition": "Active price list rates where valid-to date expired without successor pricing",
        "formula": "COUNT(ItemPrice valid_to < CURRENT_DATE without current replacement)"
    },
    "MDM-006": {
        "name": "Stock Items Without Default Warehouse",
        "definition": "Inventory items missing an assigned default operating storage warehouse",
        "formula": "COUNT(is_stock_item=1 AND default_warehouse IS NULL)"
    },
    "MDM-007": {
        "name": "Incomplete Contact Records",
        "definition": "Active business contacts missing both email address and phone number",
        "formula": "COUNT(is_active=1 AND email IS NULL AND mobile IS NULL)"
    },
    "MDM-008": {
        "name": "Current Fiscal Year Holiday List",
        "definition": "Operational calendar verification confirming holiday list covers current fiscal year",
        "formula": "COUNT(HolidayList to_date >= end of year)"
    },
    "MDM-009": {
        "name": "Valid Payment Terms Configuration",
        "definition": "Active payment terms verified where schedule portions total exactly 100%",
        "formula": "COUNT(total_portion=100) / COUNT(PaymentTerm) x100"
    },
    "MDM-010": {
        "name": "Chart of Accounts Dormancy Hygiene",
        "definition": "Active posting leaf accounts with zero transaction activity in the last 12 months",
        "formula": "COUNT(Account is_group=0 AND is_active=1 without GL in 12 months)"
    }
}
