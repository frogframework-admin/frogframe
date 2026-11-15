using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Daftar transfer dari Payment Run (dasar upload bulk transfer ke internet banking; format file tiap bank berbeda, jadi ekspor CSV lalu sesuaikan).</summary>
public sealed class BankTransferListReport(IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-bank-transfer-list";
    public string Title => "Bank Transfer List";
    public string Description => "Transfers created by submitted Payment Runs, one row per Payment Entry, with the beneficiary bank account. Export as CSV for bulk transfer upload.";
    public string DocType => "PaymentRun";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("from", "From payment date", "Date"), new("to", "To payment date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? to;
        var m = new Masters(store, user.TenantId);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var run in (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
                 .Where(r => r.DocStatus == DocStatus.Submitted && r.Data.Date("posting_date") >= from && r.Data.Date("posting_date") <= to)
                 .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var payments = (await m.QueryAsync("PaymentEntry", [new FilterClause("payment_run", "=", run.Name)], ct, take: 5000))
                .Where(p => p.DocStatus == DocStatus.Submitted).ToList();
            foreach (var g in run.Data.Rows("items").GroupBy(r => (Type: r.Str("party_type"), Party: r.Str("party"))))
            {
                var first = g.First();
                var pe = payments.FirstOrDefault(p => p.Data.Str("party_type") == g.Key.Type && p.Data.Str("party") == g.Key.Party);
                rows.Add(Rpt.Row(("payment_run", run.Name), ("payment_date", run.Data.Str("posting_date")), ("payment_entry", pe?.Name ?? "(not created)"),
                    ("party", g.Key.Party), ("bank_name", first.Str("bank_name")), ("account_no", first.Str("account_no")),
                    ("account_holder", first.Str("account_holder")), ("amount", g.Sum(r => r.Dec("amount"))),
                    ("description", string.Join(", ", g.Select(r => r.Str("reference_name"))))));
            }
        }
        rows.Add(Rpt.Row(("payment_run", "Total"), ("payment_entry", $"{rows.Count} transfers"), ("amount", rows.Sum(r => (decimal)r["amount"]!))));
        return new ReportResult(
        [
            new("payment_run", "Payment run"), new("payment_date", "Date"), new("payment_entry", "Payment"), new("party", "Party"),
            new("bank_name", "Bank"), new("account_no", "Account no."), new("account_holder", "Account name"), new("amount", "Amount", "Currency"),
            new("description", "Description"),
        ], rows);
    }
}
