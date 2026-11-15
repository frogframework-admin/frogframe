using Framework.Accounting;

namespace Apps.Erp.Reports;

public sealed class PayableAgingReport(ILedgerReader ledger, TimeProvider clock) : AgingReport(ledger, clock)
{
    public override string Name => "erp-ap-aging";
    public override string Title => "Accounts Payable Aging";
    protected override string InvoiceDocType => "PurchaseInvoice";
    protected override string PartyType => "Vendor";
    protected override string PartyField => "vendor";
    protected override bool DebitBalance => false;
}
