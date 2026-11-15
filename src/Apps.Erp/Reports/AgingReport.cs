using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Aging piutang/utang per tanggal dari buku besar: sisa tiap faktur (setelah pembayaran, nota, alokasi) per umur jatuh tempo, plus uang muka.</summary>
public abstract class AgingReport(ILedgerReader ledger, TimeProvider clock) : IReportProvider
{
    protected abstract string InvoiceDocType { get; }
    protected abstract string PartyType { get; }
    protected abstract string PartyField { get; }
    protected abstract bool DebitBalance { get; } // piutang bersaldo debit, utang bersaldo kredit
    public abstract string Name { get; }
    public abstract string Title { get; }
    public string Description => "Open amount per invoice as of a date (after payments, notes, and allocations), aged by due date; unallocated advances shown separately. Functional currency.";
    public string DocType => InvoiceDocType;
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("as_of", "As of date", "Date"),
        new("view", "View", "Select", ["Per invoice", "Per party"]),
    ];

    private static readonly string[] Buckets = ["current", "d1_30", "d31_60", "d61_90", "d90_plus"];

    private static string Bucket(int daysOverdue) => daysOverdue switch
    {
        <= 0 => "current", <= 30 => "d1_30", <= 60 => "d31_60", <= 90 => "d61_90", _ => "d90_plus",
    };

    private async Task<(string? Account, decimal Open)> OpenAsOfAsync(string tenant, string type, string name, string party, DateOnly asOf, CancellationToken ct)
    {
        var rows = (await ledger.GetAgainstAsync(tenant, type, name, ct))
            .Where(e => e.PartyType == PartyType && e.Party == party && e.PostingDate <= asOf).ToList();
        var own = rows.FirstOrDefault(e => e.VoucherType == type && e.VoucherName == name);
        if (own is null) return (null, 0m);
        var net = rows.Where(e => e.Account == own.Account).Sum(e => e.Debit - e.Credit);
        return (own.Account, DebitBalance ? net : -net);
    }

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var perParty = Rpt.Opt(f, "view") == "Per party";
        var lines = new List<Dictionary<string, object?>>();

        foreach (var inv in (await documents.QueryForReportAsync(user, InvoiceDocType, [], ct: ct))
                 .Where(d => d.DocStatus != DocStatus.Draft && d.Data.Date("posting_date") <= asOf))
        {
            var party = inv.Data.Str(PartyField);
            if (party is null) continue;
            var (_, open) = await OpenAsOfAsync(user.TenantId, InvoiceDocType, inv.Name, party, asOf, ct);
            if (open == 0) continue;
            var due = inv.Data.Date("due_date") ?? inv.Data.Date("posting_date")!.Value;
            var days = asOf.DayNumber - due.DayNumber;
            var row = Rpt.Row(("party", party), ("party_name", inv.Data.Str($"{PartyField}_name")), ("voucher", inv.Name),
                ("posting_date", inv.Data.Str("posting_date")), ("due_date", D.Iso(due)), ("days_overdue", (decimal)Math.Max(days, 0)), ("open", open));
            foreach (var b in Buckets) row[b] = 0m;
            row[Bucket(days)] = open;
            row["advance"] = 0m;
            lines.Add(row);
        }

        // Uang muka (pembayaran yang belum dialokasikan). Butuh izin laporan PaymentEntry; tanpa itu dilewati.
        try
        {
            foreach (var pay in (await documents.QueryForReportAsync(user, "PaymentEntry", [], ct: ct))
                     .Where(d => d.DocStatus != DocStatus.Draft && d.Data.Str("party_type") == PartyType && d.Data.Date("posting_date") <= asOf))
            {
                var party = pay.Data.Str("party")!;
                var (_, open) = await OpenAsOfAsync(user.TenantId, "PaymentEntry", pay.Name, party, asOf, ct);
                if (open >= 0) continue; // uang muka bersaldo berlawanan dengan tagihan
                var row = Rpt.Row(("party", party), ("party_name", null), ("voucher", pay.Name), ("posting_date", pay.Data.Str("posting_date")),
                    ("due_date", null), ("days_overdue", 0m), ("open", open));
                foreach (var b in Buckets) row[b] = 0m;
                row["advance"] = open;
                lines.Add(row);
            }
        }
        catch (PermissionDeniedException) { }

        List<Dictionary<string, object?>> rows;
        if (perParty)
        {
            rows = lines.GroupBy(l => (string)l["party"]!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var r = Rpt.Row(("party", g.Key), ("party_name", g.Select(x => x["party_name"]).OfType<string>().FirstOrDefault()), ("voucher", $"{g.Count()} documents"),
                    ("posting_date", null), ("due_date", null), ("days_overdue", g.Max(x => (decimal)x["days_overdue"]!)), ("open", g.Sum(x => (decimal)x["open"]!)));
                foreach (var b in Buckets.Append("advance")) r[b] = g.Sum(x => (decimal)x[b]!);
                return r;
            }).ToList();
        }
        else rows = lines.OrderBy(l => (string)l["party"]!, StringComparer.Ordinal).ThenBy(l => l["due_date"] as string).ToList();

        var total = Rpt.Row(("party", "Total"), ("party_name", null), ("voucher", null), ("posting_date", null), ("due_date", null), ("days_overdue", null),
            ("open", lines.Sum(x => (decimal)x["open"]!)));
        foreach (var b in Buckets.Append("advance")) total[b] = lines.Sum(x => (decimal)x[b]!);
        rows.Add(total);

        return new ReportResult(
        [
            new("party", PartyType), new("party_name", "Name"), new("voucher", "Document"), new("posting_date", "Posting date"),
            new("due_date", "Due date"), new("days_overdue", "Days overdue", "Int"), new("open", "Open amount", "Currency"),
            new("current", "Not yet due", "Currency"), new("d1_30", "1-30", "Currency"), new("d31_60", "31-60", "Currency"),
            new("d61_90", "61-90", "Currency"), new("d90_plus", "> 90", "Currency"), new("advance", "Advance", "Currency"),
        ], rows);
    }
}
