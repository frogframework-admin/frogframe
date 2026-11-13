using System.Globalization;
using Framework.Accounting;
using Framework.Documents;
using Framework.Persistence;

namespace Apps.Erp.Accounting;

public sealed class InMemoryLedgerExtensionHandler : IInMemoryUnitOfWorkExtensionHandler
{
    public bool CanHandle(UnitOfWorkExtension extension) => extension.Payload is LedgerPosting;

    public IReadOnlyList<Document> Plan(IReadOnlyList<UnitOfWorkExtension> extensions, IReadOnlyList<Document> documents, UnitOfWork uow)
    {
        var lines = extensions.SelectMany(extension => ((LedgerPosting)extension.Payload).Lines
            .Select(line => (extension.Document, line))).ToList();
        try { Ledger.ValidateShape(lines); }
        catch (ValidationFailedException ex) { throw new UnitOfWorkValidationException(ex.Errors); }

        var existing = documents.Where(d => d.DocType == Ledger.InMemoryEntryDocType)
            .Select(d => d.Data.GetValueOrDefault("entry")).OfType<LedgerEntry>().ToList();
        var plan = new List<LedgerEntry>();
        foreach (var tenantGroup in lines.GroupBy(x => x.Document.TenantId))
        {
            var tenant = tenantGroup.Key;
            var last = existing.Where(e => e.TenantId == tenant).OrderBy(e => e.Seq).LastOrDefault();
            long seq = last?.Seq ?? 0; var prev = last?.EntryHash ?? Ledger.Genesis;
            foreach (var voucher in tenantGroup.GroupBy(x => (x.Document.DocType, x.Document.Name, x.Document.Version)))
            {
                if (existing.Any(e => e.TenantId == tenant && e.VoucherType == voucher.Key.DocType && e.VoucherName == voucher.Key.Name && e.VoucherVersion == voucher.Key.Version))
                    throw Rejected($"{voucher.Key.DocType} {voucher.Key.Name} versi {voucher.Key.Version} sudah diposting");
                var lineNo = 0;
                foreach (var (doc, line) in voucher)
                {
                    var period = OpenPeriod(documents, tenant, line.PostingDate)
                        ?? throw Rejected($"Tanggal posting {line.PostingDate:yyyy-MM-dd} tidak berada di periode akuntansi yang Terbuka (Open)");
                    CheckAccount(documents, tenant, line.Account);
                    if (line.ReversalOf is { } reversal && (existing.All(e => e.Id != reversal) || existing.Any(e => e.ReversalOf == reversal) || plan.Any(e => e.ReversalOf == reversal)))
                        throw Rejected("Baris yang dibalik tidak ada atau sudah pernah dibalik");
                    seq++; lineNo++;
                    var hash = Ledger.Hash(prev, Ledger.Canonical(tenant, seq, line.PostingDate, period, line.Account, line.Debit, line.Credit,
                        doc.DocType, doc.Name, doc.Version, lineNo, line.PartyType, line.Party, line.AgainstVoucherType, line.AgainstVoucherName,
                        line.Department, line.Remarks, line.ReversalOf));
                    plan.Add(new(Guid.NewGuid(), tenant, seq, line.PostingDate, period, line.Account, line.Debit, line.Credit, doc.DocType, doc.Name,
                        doc.Version, lineNo, line.PartyType, line.Party, line.AgainstVoucherType, line.AgainstVoucherName, line.Department, line.Remarks,
                        line.ReversalOf, prev, hash, doc.ModifiedBy, DateTimeOffset.UtcNow));
                    prev = hash;
                }
            }
        }

        var touched = plan.Where(e => e.AgainstVoucherName is not null && e.AgainstVoucherName != e.VoucherName)
            .Select(e => (e.AgainstVoucherType!, e.AgainstVoucherName!, e.Account)).ToList();
        if (touched.Count > 0)
        {
            var all = existing.Concat(plan).Where(e => e.AgainstVoucherName is not null)
                .Select(e => (e.AgainstVoucherType!, e.AgainstVoucherName!, e.Account, e.VoucherName, e.Debit, e.Credit));
            if (Ledger.OpenItemViolation(all, touched) is { } violation) throw Rejected(violation);
        }

        return plan.Select(ToDocument).ToList();
    }

    private static Document ToDocument(LedgerEntry entry) => new()
    {
        DocType = Ledger.InMemoryEntryDocType,
        Name = entry.Seq.ToString("D20", CultureInfo.InvariantCulture),
        TenantId = entry.TenantId,
        Owner = entry.CreatedBy,
        DocStatus = DocStatus.Submitted,
        Created = entry.CreatedAt,
        Modified = entry.CreatedAt,
        ModifiedBy = entry.CreatedBy,
        Version = 1,
        Data = new() { ["entry"] = entry }
    };

    private static string? OpenPeriod(IReadOnlyList<Document> documents, string tenant, DateOnly date) => documents
        .Where(d => d.TenantId == tenant && d.DocType == Ledger.PeriodDocType && Ledger.IsOpenPeriodStatus(d.Data.GetValueOrDefault("status"))
                    && d.Data.GetValueOrDefault("start_date") is string start && d.Data.GetValueOrDefault("end_date") is string end
                    && DateOnly.Parse(start, CultureInfo.InvariantCulture) <= date && date <= DateOnly.Parse(end, CultureInfo.InvariantCulture))
        .OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => d.Name).FirstOrDefault();

    private static void CheckAccount(IReadOnlyList<Document> documents, string tenant, string account)
    {
        var doc = documents.FirstOrDefault(d => d.TenantId == tenant && d.DocType == Ledger.AccountDocType && d.Name == account);
        if (doc is null) throw Rejected($"Akun '{account}' tidak ditemukan");
        if (doc.Data.GetValueOrDefault("is_group") is true) throw Rejected($"Akun '{account}' adalah akun induk dan tidak bisa menerima posting");
        if (doc.Data.GetValueOrDefault("is_active") is false) throw Rejected($"Akun '{account}' nonaktif");
    }

    private static UnitOfWorkValidationException Rejected(string message) => new([new FieldError("ledger", message)]);
}
