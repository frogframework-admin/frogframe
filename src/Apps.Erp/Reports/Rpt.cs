using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Persistence;

namespace Apps.Erp.Reports;

internal static class Rpt
{
    public static DateOnly? DateOf(IReadOnlyDictionary<string, string?> f, string key) =>
        f.GetValueOrDefault(key) is { Length: > 0 } s && DateOnly.TryParse(s[..Math.Min(10, s.Length)], out var d) ? d : null;

    public static string? Opt(IReadOnlyDictionary<string, string?> f, string key) => f.GetValueOrDefault(key) is { Length: > 0 } s ? s : null;

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    public static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);

    /// <summary>Akun (master) per kode: nama, root type, report type, grup.</summary>
    public static async Task<Dictionary<string, Document>> AccountsAsync(Masters m, CancellationToken ct) =>
        (await m.QueryAsync("Account", [], ct, take: 10_000)).ToDictionary(a => a.Name, StringComparer.Ordinal);

    /// <summary>
    /// Saldo per akun dalam [from, to]. Bila excludeClosing, jurnal Tutup Buku Tahunan dikeluarkan supaya laba rugi periode
    /// yang mencakup akhir tahun buku tidak menjadi nol (PCV menolkan akun laba rugi ke laba ditahan).
    /// </summary>
    public static async Task<Dictionary<string, (decimal Dr, decimal Cr)>> BalancesAsync(ILedgerReader ledger, Masters m, string tenant,
        DateOnly? from, DateOnly to, bool excludeClosing, CancellationToken ct)
    {
        var map = new Dictionary<string, (decimal Dr, decimal Cr)>(StringComparer.Ordinal);
        foreach (var b in await ledger.GetBalancesAsync(tenant, from, to, ct))
        {
            var cur = map.GetValueOrDefault(b.Account);
            map[b.Account] = (cur.Dr + b.Debit, cur.Cr + b.Credit);
        }
        if (!excludeClosing) return map;
        foreach (var pcv in await m.QueryAsync("PeriodClosingVoucher", [], ct, take: 1000))
        {
            if (pcv.DocStatus == DocStatus.Draft) continue;
            foreach (var e in await ledger.GetVoucherAsync(tenant, "PeriodClosingVoucher", pcv.Name, ct))
            {
                if (e.PostingDate > to || (from is { } f && e.PostingDate < f)) continue;
                var cur = map.GetValueOrDefault(e.Account);
                map[e.Account] = (cur.Dr - e.Debit, cur.Cr - e.Credit); // pembalik PCV yang dibatalkan ikut ternetral
            }
        }
        return map;
    }

    public static string AccountLabel(Dictionary<string, Document> accounts, string code) =>
        accounts.TryGetValue(code, out var a) && a.Data.Str("account_name") is { } n ? $"{code} {n}" : code;
}
