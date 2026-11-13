using Framework.Accounting;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresLedgerReader(NpgsqlDataSource db) : ILedgerReader
{
    private const string Cols = "id, tenant_id, seq, posting_date, period_id, account_id, debit, credit, voucher_type, voucher_name, voucher_version, line_number, " +
                                "party_type, party, against_voucher_type, against_voucher_name, department_id, remarks, reversal_of, prev_hash, entry_hash, created_by, created_at";

    private static LedgerEntry Read(NpgsqlDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetFieldValue<DateOnly>(3), r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetDecimal(7),
            r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetInt32(11), S(12), S(13), S(14), S(15), S(16), S(17),
            r.IsDBNull(18) ? null : r.GetGuid(18), r.GetString(19), r.GetString(20), r.GetString(21), r.GetFieldValue<DateTimeOffset>(22));
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetVoucherAsync(string tenantId, string voucherType, string voucherName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_gl_entries WHERE tenant_id = $1 AND voucher_type = $2 AND voucher_name = $3 ORDER BY seq");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(voucherType); cmd.Parameters.AddWithValue(voucherName);
        var list = new List<LedgerEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetAgainstAsync(string tenantId, string againstType, string againstName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_gl_entries WHERE tenant_id = $1 AND against_voucher_type = $2 AND against_voucher_name = $3 ORDER BY seq");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(againstType); cmd.Parameters.AddWithValue(againstName);
        var list = new List<LedgerEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<bool> AccountUsedAsync(string tenantId, string account, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM frogframe_gl_entries WHERE tenant_id = $1 AND account_id = $2)");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(account);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<LedgerBalance>> GetBalancesAsync(string tenantId, DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var sql = "SELECT account_id, department_id, SUM(debit), SUM(credit) FROM frogframe_gl_entries WHERE tenant_id = $1 AND posting_date <= $2"
                  + (from is null ? "" : " AND posting_date >= $3")
                  + " GROUP BY account_id, department_id ORDER BY account_id";
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(to);
        if (from is { } f) cmd.Parameters.AddWithValue(f);
        var list = new List<LedgerBalance>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetDecimal(2), r.GetDecimal(3)));
        return list;
    }

    /// <summary>Memeriksa seluruh rantai tenant secara berurutan (streaming), melaporkan baris pertama yang rusak.</summary>
    public async Task<ChainBreak?> VerifyChainAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_gl_entries WHERE tenant_id = $1 ORDER BY seq");
        cmd.Parameters.AddWithValue(tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var prev = Ledger.Genesis; long expect = 1;
        while (await r.ReadAsync(ct))
        {
            var e = Read(r);
            if (e.Seq != expect) return new(expect, $"Nomor urut hilang: diharapkan {expect}, ditemukan {e.Seq}");
            if (e.PrevHash != prev) return new(e.Seq, "prev_hash tidak cocok dengan baris sebelumnya");
            if (Ledger.Hash(prev, Ledger.CanonicalOf(e)) != e.EntryHash) return new(e.Seq, "Isi baris berubah (entry_hash tidak cocok)");
            prev = e.EntryHash; expect++;
        }
        return null;
    }
}
