using Framework.Accounting;
using Framework.Documents;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Npgsql;

namespace Apps.Erp.Accounting;

public sealed class PostgresLedgerExtensionHandler : IPostgresUnitOfWorkExtensionHandler
{
    public bool CanHandle(UnitOfWorkExtension extension) => extension.Payload is LedgerPosting;

    public async Task ApplyAsync(IReadOnlyList<UnitOfWorkExtension> extensions, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var lines = extensions.SelectMany(extension => ((LedgerPosting)extension.Payload).Lines
            .Select(line => (extension.Document, line))).ToList();
        try { Ledger.ValidateShape(lines); }
        catch (ValidationFailedException ex) { throw new UnitOfWorkValidationException(ex.Errors); }
        try { await WriteLedgerAsync(connection, transaction, lines, ct); }
        catch (LedgerException ex) { throw Rejected(ex.Message); }
    }

    public Exception? TranslateException(PostgresException exception)
    {
        if (!exception.MessageText.StartsWith("frogframe_gl:", StringComparison.Ordinal)
            && exception.ConstraintName?.StartsWith("ck_gl", StringComparison.Ordinal) != true
            && exception.ConstraintName is not ("uq_gl_voucher_line" or "uq_gl_reversal")) return null;
        var message = exception.ConstraintName switch
        {
            "uq_gl_voucher_line" => "Voucher versi ini sudah diposting",
            "uq_gl_reversal" => "Baris ini sudah pernah dibalik",
            "ck_gl_one_side" => "Setiap baris harus berisi debit ATAU kredit",
            _ => exception.MessageText.Replace("frogframe_gl: ", "", StringComparison.Ordinal),
        };
        return Rejected(message);
    }

    private static UnitOfWorkValidationException Rejected(string message) => new([new FieldError("ledger", message)]);

    private static async Task WriteLedgerAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        IReadOnlyList<(Document Voucher, LedgerLine Line)> lines, CancellationToken ct)
    {
        foreach (var tenantGroup in lines.GroupBy(x => x.Voucher.TenantId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var tenant = tenantGroup.Key;
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('FrogFrame.gl'), hashtext($1))", conn, tx))
            { lk.Parameters.AddWithValue(tenant); await lk.ExecuteScalarAsync(ct); }

            var periods = new Dictionary<DateOnly, string>();
            foreach (var date in tenantGroup.Select(x => x.Line.PostingDate).Distinct())
            {
                await using var q = new NpgsqlCommand(
                    "SELECT name FROM frogframe_documents WHERE tenant_id = $1 AND doctype = $2 AND data->>'status' = ANY($4) " +
                    "AND (data->>'start_date')::date <= $3 AND (data->>'end_date')::date >= $3 ORDER BY name LIMIT 1 FOR SHARE", conn, tx);
                q.Parameters.AddWithValue(tenant); q.Parameters.AddWithValue(Ledger.PeriodDocType); q.Parameters.AddWithValue(date);
                q.Parameters.AddWithValue(Ledger.OpenPeriodStatuses);
                periods[date] = await q.ExecuteScalarAsync(ct) as string
                    ?? throw new LedgerException($"Tanggal posting {date:yyyy-MM-dd} tidak berada di periode akuntansi yang Terbuka (Open)");
            }

            var accounts = tenantGroup.Select(x => x.Line.Account).Distinct().ToArray();
            var found = new Dictionary<string, (string? Group, string? Active)>();
            await using (var q = new NpgsqlCommand(
                "SELECT name, data->>'is_group', data->>'is_active' FROM frogframe_documents WHERE tenant_id = $1 AND doctype = $2 AND name = ANY($3)", conn, tx))
            {
                q.Parameters.AddWithValue(tenant); q.Parameters.AddWithValue(Ledger.AccountDocType); q.Parameters.AddWithValue(accounts);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) found[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
            foreach (var account in accounts)
            {
                if (!found.TryGetValue(account, out var info)) throw new LedgerException($"Akun '{account}' tidak ditemukan");
                if (info.Group == "true") throw new LedgerException($"Akun '{account}' adalah akun induk dan tidak bisa menerima posting");
                if (info.Active == "false") throw new LedgerException($"Akun '{account}' nonaktif");
            }

            long seq = 0; var prev = Ledger.Genesis;
            await using (var q = new NpgsqlCommand("SELECT seq, entry_hash FROM frogframe_gl_entries WHERE tenant_id = $1 ORDER BY seq DESC LIMIT 1", conn, tx))
            {
                q.Parameters.AddWithValue(tenant);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { seq = r.GetInt64(0); prev = r.GetString(1); }
            }

            foreach (var voucher in tenantGroup.GroupBy(x => (x.Voucher.DocType, x.Voucher.Name, x.Voucher.Version)))
            {
                var lineNo = 0;
                foreach (var (doc, line) in voucher)
                {
                    seq++; lineNo++;
                    var period = periods[line.PostingDate];
                    var hash = Ledger.Hash(prev, Ledger.Canonical(tenant, seq, line.PostingDate, period, line.Account, line.Debit, line.Credit,
                        doc.DocType, doc.Name, doc.Version, lineNo, line.PartyType, line.Party, line.AgainstVoucherType, line.AgainstVoucherName,
                        line.Department, line.Remarks, line.ReversalOf));
                    await using var ins = new NpgsqlCommand(
                        "INSERT INTO frogframe_gl_entries (id, tenant_id, seq, posting_date, period_id, account_id, debit, credit, voucher_type, voucher_name, " +
                        "voucher_version, line_number, party_type, party, against_voucher_type, against_voucher_name, department_id, remarks, reversal_of, " +
                        "prev_hash, entry_hash, created_by) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22)", conn, tx);
                    object N(object? value) => value ?? DBNull.Value;
                    foreach (var value in new object[] { Guid.NewGuid(), tenant, seq, line.PostingDate, period, line.Account, line.Debit, line.Credit, doc.DocType, doc.Name,
                                 doc.Version, lineNo, N(line.PartyType), N(line.Party), N(line.AgainstVoucherType), N(line.AgainstVoucherName), N(line.Department), N(line.Remarks),
                                 line.ReversalOf is { } reversal ? reversal : DBNull.Value, prev, hash, doc.ModifiedBy })
                        ins.Parameters.AddWithValue(value);
                    await ins.ExecuteNonQueryAsync(ct);
                    prev = hash;
                }
            }

            var touched = tenantGroup.Where(x => x.Line.AgainstVoucherName is not null && x.Line.AgainstVoucherName != x.Voucher.Name)
                .Select(x => (x.Line.AgainstVoucherType!, x.Line.AgainstVoucherName!, x.Line.Account)).Distinct().ToList();
            foreach (var (againstType, againstName, account) in touched)
            {
                var rows = new List<(string, string, string, string, decimal, decimal)>();
                await using var q = new NpgsqlCommand(
                    "SELECT voucher_name, debit, credit FROM frogframe_gl_entries WHERE tenant_id = $1 AND against_voucher_type = $2 AND against_voucher_name = $3 AND account_id = $4", conn, tx);
                q.Parameters.AddWithValue(tenant); q.Parameters.AddWithValue(againstType); q.Parameters.AddWithValue(againstName); q.Parameters.AddWithValue(account);
                await using (var r = await q.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) rows.Add((againstType, againstName, account, r.GetString(0), r.GetDecimal(1), r.GetDecimal(2)));
                if (Ledger.OpenItemViolation(rows, [(againstType, againstName, account)]) is { } violation)
                    throw new LedgerException(violation);
            }
        }
    }
}
