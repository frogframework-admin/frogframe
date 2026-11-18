# PROCUREMENT & STOCK translations (PRC-001 to PRC-021, STK-001 to STK-012)
PRC_STK_TRANSLATIONS = {
    # Procurement (21)
    "PRC-001": {
        "name": "PR Approval Cycle Time",
        "definition": "Average duration from purchase request submission to final approval",
        "formula": "AVG(approval transition duration: Approved − Pending)"
    },
    "PRC-002": {
        "name": "PR to PO Cycle Time",
        "definition": "Days from purchase request approval to purchase order issuance",
        "formula": "AVG(PurchaseOrder.order_date − PR approval date)"
    },
    "PRC-003": {
        "name": "PR Conversion Rate",
        "definition": "Percentage of approved purchase requests converted into purchase orders",
        "formula": "COUNT(order_status='Ordered') / COUNT(Approved PRs) x100"
    },
    "PRC-004": {
        "name": "PR Rejection Rate",
        "definition": "Percentage of submitted purchase requests rejected during workflow",
        "formula": "COUNT(state='Rejected') / COUNT(Submitted PRs) x100"
    },
    "PRC-005": {
        "name": "Overdue Purchase Requests",
        "definition": "Unordered purchase requests where the required-by date has passed",
        "formula": "COUNT(order_status != 'Ordered' AND required_by < CURRENT_DATE)"
    },
    "PRC-006": {
        "name": "Total Spend (PO)",
        "definition": "Total monetary value of purchase orders issued and submitted",
        "formula": "SUM(PurchaseOrder.grand_total, docstatus=1)"
    },
    "PRC-007": {
        "name": "Spend Under Management",
        "definition": "Proportion of total procurement spend routed through formal purchase orders",
        "formula": "SUM(PI with purchase_order) / SUM(PurchaseInvoice) x100"
    },
    "PRC-008": {
        "name": "Price Variance PR vs PO",
        "definition": "Percentage price variance between estimated PR cost and negotiated PO price",
        "formula": "(SUM(PO amount) − SUM(PR amount)) / SUM(PR amount) x100"
    },
    "PRC-009": {
        "name": "Procurement Cost Savings",
        "definition": "Direct monetary savings achieved through supplier negotiation below estimates",
        "formula": "SUM(PR.total_estimate − PO.total) WHERE PO < PR"
    },
    "PRC-010": {
        "name": "Open PO Value",
        "definition": "Monetary value of committed purchase orders awaiting receipt",
        "formula": "SUM(grand_total − received_amount) WHERE receipt_status != 'Fully received'"
    },
    "PRC-011": {
        "name": "Unbilled Receipts (GRNI)",
        "definition": "Goods and services received from suppliers awaiting invoice billing",
        "formula": "SUM(received_amount − billed_amount)"
    },
    "PRC-012": {
        "name": "Top 10 Vendor Spend Concentration",
        "definition": "Share of total procurement spend concentrated among the top 10 suppliers",
        "formula": "SUM(Top 10 vendors) / Total spend x100"
    },
    "PRC-013": {
        "name": "Spend by Item Group",
        "definition": "Distribution of purchasing spend across item groups and categories",
        "formula": "SUM(PurchaseOrderItem.amount) GROUP BY Item.item_group"
    },
    "PRC-014": {
        "name": "Vendor On-time Delivery Rate",
        "definition": "Percentage of goods receipts delivered on or before promised PO delivery date",
        "formula": "COUNT(GoodsReceipt.receipt_date <= PO.delivery_date) / COUNT(GR) x100"
    },
    "PRC-015": {
        "name": "Vendor In-full Delivery Rate",
        "definition": "Percentage of goods receipts meeting full ordered line quantity",
        "formula": "COUNT(received_qty >= ordered_qty) / COUNT(GR lines) x100"
    },
    "PRC-016": {
        "name": "Vendor OTIF (On-Time In-Full)",
        "definition": "Supplier deliveries fulfilled completely and strictly on time",
        "formula": "COUNT(on time AND in full) / COUNT(GR lines) x100"
    },
    "PRC-017": {
        "name": "Quality Rejection Rate",
        "definition": "Proportion of received goods rejected due to quality inspection failure",
        "formula": "SUM(rejected_qty) / SUM(received_qty + rejected_qty) x100"
    },
    "PRC-018": {
        "name": "PO Lead Time",
        "definition": "Average supplier fulfillment lead time in days from order to delivery",
        "formula": "AVG(receipt_date − order_date)"
    },
    "PRC-019": {
        "name": "Receipts Without Delivery Note",
        "definition": "Goods receipts submitted without supplier delivery note number or attachment",
        "formula": "COUNT(delivery_note_no IS NULL OR document IS NULL) / COUNT(GR) x100"
    },
    "PRC-020": {
        "name": "Active Vendors",
        "definition": "Number of unique vendors with PO transactions in the last 12 months",
        "formula": "COUNT(DISTINCT vendor POs in last 12 months)"
    },
    "PRC-021": {
        "name": "Vendors On Hold",
        "definition": "Number of vendor master records blocked from purchasing transactions",
        "formula": "COUNT(Vendor on_hold=1)"
    },

    # Stock (12)
    "STK-001": {
        "name": "Total Inventory Valuation",
        "definition": "Total monetary value of inventory on hand (StockBalanceReport)",
        "formula": "SUM(qty × valuation_rate) per warehouse"
    },
    "STK-002": {
        "name": "Inventory Turnover Ratio",
        "definition": "Cost of goods sold divided by average inventory investment",
        "formula": "COGS / AVG(Inventory value)"
    },
    "STK-003": {
        "name": "DIO (Days Inventory Outstanding)",
        "definition": "Average number of days inventory remains held in stock",
        "formula": "(Inventory value / COGS) × days in period"
    },
    "STK-004": {
        "name": "Stock Count Accuracy",
        "definition": "Percentage of stock items with zero physical count discrepancy",
        "formula": "COUNT(StockReconciliationItem qty_difference=0) / COUNT(Items counted) x100"
    },
    "STK-005": {
        "name": "Stock Take Variance Value",
        "definition": "Absolute monetary value of physical inventory count adjustments",
        "formula": "SUM(ABS(amount_difference))"
    },
    "STK-006": {
        "name": "Items Below Reorder Level",
        "definition": "Stock items where available quantity has fallen below reorder point",
        "formula": "COUNT(Item is_stock_item=1 AND available_qty < reorder_level)"
    },
    "STK-007": {
        "name": "Stock-out Count",
        "definition": "Active stock items completely depleted with zero or negative inventory",
        "formula": "COUNT(Item is_stock_item=1 AND actual_qty <= 0)"
    },
    "STK-008": {
        "name": "Negative Stock Incidents",
        "definition": "Stock ledger transaction entries resulting in negative warehouse quantity",
        "formula": "COUNT(stock ledger balance_qty < 0)"
    },
    "STK-009": {
        "name": "Slow-moving Inventory Value",
        "definition": "Valuation of inventory stock with no turnover movement in over 90 days",
        "formula": "SUM(value of items without stock movements for > 90 days)"
    },
    "STK-010": {
        "name": "Stock Scrappage & Material Issues",
        "definition": "Value of non-sales inventory write-offs and internal consumption issues",
        "formula": "SUM(StockEntry.total_value WHERE purpose='Material Issue')"
    },
    "STK-011": {
        "name": "Inter-warehouse Transfers",
        "definition": "Value and volume of inventory transferred between internal warehouses",
        "formula": "COUNT / SUM(total_value WHERE purpose='Material Transfer')"
    },
    "STK-012": {
        "name": "Gross Margin by Item",
        "definition": "Gross profit margin percentage achieved per inventory product item",
        "formula": "SUM(amount − stock_amount) / SUM(amount) x100 per item"
    }
}
