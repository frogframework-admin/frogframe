using Framework.Accounting;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Accounting;

public sealed class InMemoryLedgerReader(IDocumentStore store) : ILedgerReader
{
    private async Task<List<LedgerEntry>> EntriesAsync(string tenantId, CancellationToken ct)
    {
        var result = await store.QueryAsync(new StoreQuery(tenantId, Ledger.InMemoryEntryDocType, RowScope.All, [], null, [], [], 0, int.MaxValue), ct);
        return result.Items.Select(d => d.Data.GetValueOrDefault("entry")).OfType<LedgerEntry>().OrderBy(e => e.Seq).ToList();
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetVoucherAsync(string tenantId, string voucherType, string voucherName, CancellationToken ct = default) =>
        (await EntriesAsync(tenantId, ct)).Where(e => e.VoucherType == voucherType && e.VoucherName == voucherName).ToList();

    public async Task<ChainBreak?> VerifyChainAsync(string tenantId, CancellationToken ct = default) => Ledger.Verify(await EntriesAsync(tenantId, ct));

    public async Task<bool> AccountUsedAsync(string tenantId, string account, CancellationToken ct = default) =>
        (await EntriesAsync(tenantId, ct)).Any(e => e.Account == account);

    public async Task<IReadOnlyList<LedgerEntry>> GetAgainstAsync(string tenantId, string againstType, string againstName, CancellationToken ct = default) =>
        (await EntriesAsync(tenantId, ct)).Where(e => e.AgainstVoucherType == againstType && e.AgainstVoucherName == againstName).ToList();

    public async Task<IReadOnlyList<LedgerBalance>> GetBalancesAsync(string tenantId, DateOnly? from, DateOnly to, CancellationToken ct = default) =>
        (await EntriesAsync(tenantId, ct)).Where(e => e.PostingDate <= to && (from is null || e.PostingDate >= from))
            .GroupBy(e => (e.Account, e.Department))
            .OrderBy(g => g.Key.Account, StringComparer.Ordinal)
            .Select(g => new LedgerBalance(g.Key.Account, g.Key.Department, g.Sum(e => e.Debit), g.Sum(e => e.Credit)))
            .ToList();
}
