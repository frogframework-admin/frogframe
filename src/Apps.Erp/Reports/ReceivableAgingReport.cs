using Framework.Accounting;

namespace Apps.Erp.Reports;

public sealed class ReceivableAgingReport(ILedgerReader ledger, TimeProvider clock) : AgingReport(ledger, clock)
{
    public override string Name => "erp-ar-aging";
    public override string Title => "Accounts Receivable Aging";
    protected override string InvoiceDocType => "SalesInvoice";
    protected override string PartyType => "Customer";
    protected override string PartyField => "customer";
    protected override bool DebitBalance => true;
}
