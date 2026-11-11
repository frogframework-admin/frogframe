using System.Text.Json;
using Framework.Documents;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Preferences;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Store PostgreSQL untuk dokumen, audit, outbox, dan nomor seri. Satu CommitAsync = satu transaksi database:
/// dokumen, audit, dan event outbox tersimpan bersama atau tidak sama sekali (RFC §6, §12).
/// </summary>
public sealed class PostgresDocumentStore(NpgsqlDataSource db, IServiceProvider services) : IDocumentStore, IOutboxStore, IDatabaseHealthCheck
{
    public string ProviderName => "postgresql";

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return res is not null;
    }

    // IMetadataProvider di-resolve saat dipakai (bukan di konstruktor) karena MetadataRegistry sendiri
    // bergantung pada store metadata di database yang sama.
    private IMetadataProvider Metadata => services.GetRequiredService<IMetadataProvider>();

    private CompiledDocType? Meta(string docType) => Metadata.Current.TryGet(docType, out var m) ? m : null;

    private const string Columns =
        "d.tenant_id, d.doctype, d.name, d.owner, d.docstatus, d.workflow_state, d.created, d.modified, d.modified_by, " +
        "d.version, d.schema_version, d.workflow_version, d.amended_from, d.data::text";

    private Document ReadDocument(NpgsqlDataReader r)
    {
        var docType = r.GetString(1);
        return new Document
        {
            TenantId = r.GetString(0), DocType = docType, Name = r.GetString(2), Owner = r.GetString(3),
            DocStatus = r.GetInt16(4), WorkflowState = r.IsDBNull(5) ? null : r.GetString(5),
            Created = r.GetFieldValue<DateTimeOffset>(6), Modified = r.GetFieldValue<DateTimeOffset>(7), ModifiedBy = r.GetString(8),
            Version = r.GetInt32(9), SchemaVersion = r.GetInt32(10), WorkflowVersion = r.IsDBNull(11) ? null : r.GetInt32(11),
            AmendedFrom = r.IsDBNull(12) ? null : r.GetString(12),
            Data = JsonCodec.Read(r.GetString(13), Meta(docType), Meta),
        };
    }

    public async Task<Document?> GetAsync(string tenantId, string docType, string name, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_documents d WHERE d.tenant_id = $1 AND d.doctype = $2 AND d.name = $3");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(docType); cmd.Parameters.AddWithValue(name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDocument(r) : null;
    }

    public async Task<QueryResult> QueryAsync(StoreQuery q, CancellationToken ct = default)
    {
        var b = new SqlQueryBuilder(Meta(q.DocType));
        var where = new List<string>
        {
            $"d.tenant_id = {b.Param(q.TenantId, NpgsqlDbType.Text)}",
            $"d.doctype = {b.Param(q.DocType, NpgsqlDbType.Text)}",
            b.Scope(q.Scope)
        };
        where.AddRange(q.Filters.Select(b.Filter));
        if (!string.IsNullOrWhiteSpace(q.Search)) where.Add(b.Search(q.Search, q.SearchFields));
        var whereSql = string.Join(" AND ", where);
        var whereParamCount = b.Parameters.Count;
        var order = b.OrderBy(q.Sort);
        var skip = b.Param(q.Skip, NpgsqlDbType.Integer);
        var take = b.Param(q.Take, NpgsqlDbType.Integer);

        await using var conn = await db.OpenConnectionAsync(ct);
        int total;
        // Hitungan dibatasi: berhenti memindai setelah CountCap+1 baris (jutaan baris tidak dihitung satu per satu).
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM (SELECT 1 FROM frogframe_documents d WHERE {whereSql} LIMIT {QueryLimits.CountCap + 1}) AS capped", conn))
        {
            // Query hitung hanya memakai parameter WHERE (bukan ORDER BY / OFFSET / LIMIT).
            foreach (var p in b.Parameters.Take(whereParamCount)) count.Parameters.Add(p.Clone());
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }
        var capped = total > QueryLimits.CountCap;
        if (capped) total = QueryLimits.CountCap;
        var items = new List<Document>();
        await using (var cmd = new NpgsqlCommand($"SELECT {Columns} FROM frogframe_documents d WHERE {whereSql} ORDER BY {order} OFFSET {skip} LIMIT {take}", conn))
        {
            foreach (var p in b.Parameters) cmd.Parameters.Add(p.Clone());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadDocument(r));
        }
        return new QueryResult(items, total, capped);
    }

    public async Task<string> NextNameAsync(string tenantId, string docType, string pattern, DateTimeOffset now, CancellationToken ct = default)
    {
        var (prefix, width, suffix) = NamingPattern.Parse(pattern, docType, now);
        // Atomik dan aman untuk banyak instance aplikasi.
        await using var cmd = db.CreateCommand(
            "INSERT INTO frogframe_series (tenant_id, doctype, prefix, current) VALUES ($1, $2, $3, 1) " +
            "ON CONFLICT (tenant_id, doctype, prefix) DO UPDATE SET current = frogframe_series.current + 1 RETURNING current");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(docType); cmd.Parameters.AddWithValue(prefix);
        var next = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return prefix + next.ToString(new string('0', width)) + suffix;
    }

    public async Task CommitAsync(UnitOfWork uow, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var docTypes = uow.Upserts.Select(x => x.Doc.DocType)
                .Concat(uow.Deletes.Select(x => x.DocType)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var docType in docTypes)
                await LockDocType(conn, tx, docType, shared: true, ct);
            foreach (var docType in docTypes)
                await EnsurePublishedSchema(conn, tx, docType, ct);

            foreach (var (doc, expected) in uow.Upserts)
            {
                await EnsureDocumentSchema(conn, tx, doc.DocType, doc.SchemaVersion, ct);
                if (expected is null) await InsertDocument(conn, tx, doc, ct);
                else await UpdateDocument(conn, tx, doc, expected.Value, ct);
            }
            foreach (var (tenant, docType, name, expected) in uow.Deletes)
            {
                await using var del = new NpgsqlCommand("DELETE FROM frogframe_documents WHERE tenant_id = $1 AND doctype = $2 AND name = $3 AND version = $4", conn, tx);
                del.Parameters.AddWithValue(tenant); del.Parameters.AddWithValue(docType); del.Parameters.AddWithValue(name); del.Parameters.AddWithValue(expected);
                if (await del.ExecuteNonQueryAsync(ct) == 0)
                    throw new ConcurrencyException(name, expected, await CurrentVersion(conn, tx, tenant, docType, name, ct));
            }

            if (uow.Ledger.Count > 0) await WriteLedgerAsync(conn, tx, uow, ct);

            foreach (var a in uow.Audits)
            {
                await using var ins = new NpgsqlCommand(
                    "INSERT INTO frogframe_audit (tenant_id, doctype, doc_name, action, actor, at, changes, comment, from_state, to_state, workflow_version, schema_version) " +
                    "VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12)", conn, tx);
                ins.Parameters.AddWithValue(a.TenantId); ins.Parameters.AddWithValue(a.DocType); ins.Parameters.AddWithValue(a.DocName);
                ins.Parameters.AddWithValue(a.Action); ins.Parameters.AddWithValue(a.Actor); ins.Parameters.AddWithValue(a.At);
                ins.Parameters.AddWithValue(JsonSerializer.Serialize(a.Changes));
                ins.Parameters.AddWithValue((object?)a.Comment ?? DBNull.Value); ins.Parameters.AddWithValue((object?)a.FromState ?? DBNull.Value);
                ins.Parameters.AddWithValue((object?)a.ToState ?? DBNull.Value); ins.Parameters.AddWithValue((object?)a.WorkflowVersion ?? DBNull.Value);
                ins.Parameters.AddWithValue(a.SchemaVersion);
                await ins.ExecuteNonQueryAsync(ct);
            }
            foreach (var m in uow.Outbox)
            {
                await using var ins = new NpgsqlCommand(
                    "INSERT INTO frogframe_outbox (id, tenant_id, type, payload, idempotency_key, created_at) VALUES ($1,$2,$3,$4::jsonb,$5,$6) " +
                    "ON CONFLICT (idempotency_key) DO NOTHING", conn, tx);
                ins.Parameters.AddWithValue(m.Id); ins.Parameters.AddWithValue(m.TenantId); ins.Parameters.AddWithValue(m.Type);
                ins.Parameters.AddWithValue(JsonSerializer.Serialize(m.Payload)); ins.Parameters.AddWithValue(m.IdempotencyKey); ins.Parameters.AddWithValue(m.CreatedAt);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct); // constraint trigger keseimbangan GL berjalan di sini
        }
        catch (PostgresException ex) when (ex.MessageText.StartsWith("frogframe_gl:") || ex.ConstraintName?.StartsWith("ck_gl") == true
                                           || ex.ConstraintName is "uq_gl_voucher_line" or "uq_gl_reversal")
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new Framework.Accounting.LedgerException(ex.ConstraintName switch
            {
                "uq_gl_voucher_line" => "Voucher versi ini sudah diposting",
                "uq_gl_reversal" => "Baris ini sudah pernah dibalik",
                "ck_gl_one_side" => "Setiap baris harus berisi debit ATAU kredit",
                _ => ex.MessageText.Replace("frogframe_gl: ", ""),
            });
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Menulis baris buku besar dalam transaksi dokumen: kunci per tenant (urutan seq), periode Terbuka dikunci
    /// FOR SHARE (penutupan bersamaan menunggu), akun diperiksa, lalu seq dan rantai hash dihitung.
    /// </summary>
    private static async Task WriteLedgerAsync(NpgsqlConnection conn, NpgsqlTransaction tx, UnitOfWork uow, CancellationToken ct)
    {
        foreach (var tenantGroup in uow.Ledger.GroupBy(x => x.Voucher.TenantId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var tenant = tenantGroup.Key;
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('FrogFrame.gl'), hashtext($1))", conn, tx))
            { lk.Parameters.AddWithValue(tenant); await lk.ExecuteScalarAsync(ct); }

            var periods = new Dictionary<DateOnly, string>();
            foreach (var date in tenantGroup.Select(x => x.Line.PostingDate).Distinct())
            {
                await using var q = new NpgsqlCommand(
                    "SELECT name FROM frogframe_documents WHERE tenant_id = $1 AND doctype = $2 AND data->>'status' = 'Terbuka' " +
                    "AND (data->>'start_date')::date <= $3 AND (data->>'end_date')::date >= $3 ORDER BY name LIMIT 1 FOR SHARE", conn, tx);
                q.Parameters.AddWithValue(tenant); q.Parameters.AddWithValue(Framework.Accounting.Ledger.PeriodDocType); q.Parameters.AddWithValue(date);
                periods[date] = await q.ExecuteScalarAsync(ct) as string
                    ?? throw new Framework.Accounting.LedgerException($"Tanggal posting {date:yyyy-MM-dd} tidak berada di periode akuntansi yang Terbuka");
            }

            var accounts = tenantGroup.Select(x => x.Line.Account).Distinct().ToArray();
            var found = new Dictionary<string, (string? Group, string? Active)>();
            await using (var q = new NpgsqlCommand(
                "SELECT name, data->>'is_group', data->>'is_active' FROM frogframe_documents WHERE tenant_id = $1 AND doctype = $2 AND name = ANY($3)", conn, tx))
            {
                q.Parameters.AddWithValue(tenant); q.Parameters.AddWithValue(Framework.Accounting.Ledger.AccountDocType); q.Parameters.AddWithValue(accounts);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) found[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
            foreach (var a in accounts)
            {
                if (!found.TryGetValue(a, out var info)) throw new Framework.Accounting.LedgerException($"Akun '{a}' tidak ditemukan");
                if (info.Group == "true") throw new Framework.Accounting.LedgerException($"Akun '{a}' adalah akun induk dan tidak bisa menerima posting");
                if (info.Active == "false") throw new Framework.Accounting.LedgerException($"Akun '{a}' nonaktif");
            }

            long seq = 0; var prev = Framework.Accounting.Ledger.Genesis;
            await using (var q = new NpgsqlCommand("SELECT seq, entry_hash FROM frogframe_gl_entries WHERE tenant_id = $1 ORDER BY seq DESC LIMIT 1", conn, tx))
            {
                q.Parameters.AddWithValue(tenant);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { seq = r.GetInt64(0); prev = r.GetString(1); }
            }

            foreach (var voucher in tenantGroup.GroupBy(x => (x.Voucher.DocType, x.Voucher.Name, x.Voucher.Version)))
            {
                var lineNo = 0;
                foreach (var (doc, l) in voucher)
                {
                    seq++; lineNo++;
                    var period = periods[l.PostingDate];
                    var hash = Framework.Accounting.Ledger.Hash(prev, Framework.Accounting.Ledger.Canonical(tenant, seq, l.PostingDate, period, l.Account, l.Debit, l.Credit,
                        doc.DocType, doc.Name, doc.Version, lineNo, l.PartyType, l.Party, l.AgainstVoucherType, l.AgainstVoucherName, l.Department, l.Remarks, l.ReversalOf));
                    await using var ins = new NpgsqlCommand(
                        "INSERT INTO frogframe_gl_entries (id, tenant_id, seq, posting_date, period_id, account_id, debit, credit, voucher_type, voucher_name, " +
                        "voucher_version, line_number, party_type, party, against_voucher_type, against_voucher_name, department_id, remarks, reversal_of, " +
                        "prev_hash, entry_hash, created_by) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22)", conn, tx);
                    object N(object? v) => v ?? DBNull.Value;
                    foreach (var v in new object[] { Guid.NewGuid(), tenant, seq, l.PostingDate, period, l.Account, l.Debit, l.Credit, doc.DocType, doc.Name,
                                 doc.Version, lineNo, N(l.PartyType), N(l.Party), N(l.AgainstVoucherType), N(l.AgainstVoucherName), N(l.Department), N(l.Remarks),
                                 l.ReversalOf is { } rv ? rv : DBNull.Value, prev, hash, doc.ModifiedBy })
                        ins.Parameters.AddWithValue(v);
                    await ins.ExecuteNonQueryAsync(ct);
                    prev = hash;
                }
            }

            // Penjaga sisa tagihan (di bawah kunci tenant): pelunasan tidak boleh membuat saldo terbuka berbalik tanda.
            var touched = tenantGroup.Where(x => x.Line.AgainstVoucherName is not null && x.Line.AgainstVoucherName != x.Voucher.Name)
                .Select(x => (x.Line.AgainstVoucherType!, x.Line.AgainstVoucherName!, x.Line.Account)).Distinct().ToList();
            foreach (var (aType, aName, account) in touched)
            {
                var rows = new List<(string, string, string, string, decimal, decimal)>();
                await using var q = new NpgsqlCommand(
                    "SELECT voucher_name, debit, credit FROM frogframe_gl_entries WHERE tenant_id = $1 AND against_voucher_type = $2 AND against_voucher_name = $3 AND account_id = $4", conn, tx);
                q.Parameters.AddWithValue(tenant); q.Parameters.AddWithValue(aType); q.Parameters.AddWithValue(aName); q.Parameters.AddWithValue(account);
                await using (var r = await q.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) rows.Add((aType, aName, account, r.GetString(0), r.GetDecimal(1), r.GetDecimal(2)));
                if (Framework.Accounting.Ledger.OpenItemViolation(rows, [(aType, aName, account)]) is { } v)
                    throw new Framework.Accounting.LedgerException(v);
            }
        }
    }

    private static async Task LockDocType(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, bool shared, CancellationToken ct)
    {
        var mode = shared ? "pg_advisory_xact_lock_shared" : "pg_advisory_xact_lock";
        await using var cmd = new NpgsqlCommand($"SELECT {mode}(hashtext('FrogFrame.doctype'), hashtext($1))", conn, tx);
        cmd.Parameters.AddWithValue(docType);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<int> PublishedSchemaVersion(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT version FROM frogframe_metadata_versions WHERE doctype=$1 AND status='Published'", conn, tx);
        cmd.Parameters.AddWithValue(docType);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int version ? version : throw new InvalidOperationException($"Tidak ada metadata Published untuk DocType '{docType}'.");
    }

    private static async Task EnsurePublishedSchema(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, CancellationToken ct) =>
        _ = await PublishedSchemaVersion(conn, tx, docType, ct);

    private static async Task EnsureDocumentSchema(NpgsqlConnection conn, NpgsqlTransaction tx, string docType, int documentVersion, CancellationToken ct)
    {
        var activeVersion = await PublishedSchemaVersion(conn, tx, docType, ct);
        if (documentVersion != activeVersion)
            throw new InvalidOperationException($"Schema dokumen versi {documentVersion} tidak sesuai metadata aktif versi {activeVersion} untuk '{docType}'. Muat ulang aplikasi atau dokumen.");
    }

    private static async Task InsertDocument(NpgsqlConnection conn, NpgsqlTransaction tx, Document d, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, workflow_state, created, modified, modified_by, version, schema_version, workflow_version, amended_from, data) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14::jsonb)", conn, tx);
        AddDocumentParams(cmd, d);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidStateException($"Nama {d.Name} sudah dipakai");
        }
    }

    private static async Task UpdateDocument(NpgsqlConnection conn, NpgsqlTransaction tx, Document d, int expected, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE frogframe_documents SET owner=$4, docstatus=$5, workflow_state=$6, created=$7, modified=$8, modified_by=$9, version=$10, " +
            "schema_version=$11, workflow_version=$12, amended_from=$13, data=$14::jsonb " +
            "WHERE tenant_id=$1 AND doctype=$2 AND name=$3 AND version=$15", conn, tx);
        AddDocumentParams(cmd, d);
        cmd.Parameters.AddWithValue(expected);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new ConcurrencyException(d.Name, expected, await CurrentVersion(conn, tx, d.TenantId, d.DocType, d.Name, ct));
    }

    private static void AddDocumentParams(NpgsqlCommand cmd, Document d)
    {
        cmd.Parameters.AddWithValue(d.TenantId); cmd.Parameters.AddWithValue(d.DocType); cmd.Parameters.AddWithValue(d.Name);
        cmd.Parameters.AddWithValue(d.Owner); cmd.Parameters.AddWithValue((short)d.DocStatus);
        cmd.Parameters.AddWithValue((object?)d.WorkflowState ?? DBNull.Value);
        cmd.Parameters.AddWithValue(d.Created); cmd.Parameters.AddWithValue(d.Modified); cmd.Parameters.AddWithValue(d.ModifiedBy);
        cmd.Parameters.AddWithValue(d.Version); cmd.Parameters.AddWithValue(d.SchemaVersion);
        cmd.Parameters.AddWithValue((object?)d.WorkflowVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)d.AmendedFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue(JsonCodec.Write(d.Data));
    }

    private static async Task<int> CurrentVersion(NpgsqlConnection conn, NpgsqlTransaction tx, string tenant, string docType, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT version FROM frogframe_documents WHERE tenant_id=$1 AND doctype=$2 AND name=$3", conn, tx);
        cmd.Parameters.AddWithValue(tenant); cmd.Parameters.AddWithValue(docType); cmd.Parameters.AddWithValue(name);
        return await cmd.ExecuteScalarAsync(ct) is int v ? v : 0;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string tenantId, string docType, string name, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, tenant_id, doctype, doc_name, action, actor, at, changes::text, comment, from_state, to_state, workflow_version, schema_version " +
            "FROM frogframe_audit WHERE tenant_id=$1 AND doctype=$2 AND doc_name=$3 ORDER BY id DESC");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(docType); cmd.Parameters.AddWithValue(name);
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuditEntry
            {
                Id = r.GetInt64(0), TenantId = r.GetString(1), DocType = r.GetString(2), DocName = r.GetString(3),
                Action = r.GetString(4), Actor = r.GetString(5), At = r.GetFieldValue<DateTimeOffset>(6),
                Changes = JsonSerializer.Deserialize<List<FieldChange>>(r.GetString(7)) ?? [],
                Comment = r.IsDBNull(8) ? null : r.GetString(8), FromState = r.IsDBNull(9) ? null : r.GetString(9),
                ToState = r.IsDBNull(10) ? null : r.GetString(10), WorkflowVersion = r.IsDBNull(11) ? null : r.GetInt32(11),
                SchemaVersion = r.GetInt32(12),
            });
        return list;
    }

    public async Task<DocumentLinkReference?> FindFirstLinkAsync(
        string tenantId,
        string targetDocType,
        string targetName,
        IReadOnlyList<DocTypeLinkDef> linkDefs,
        CancellationToken ct = default)
    {
        if (linkDefs.Count == 0) return null;

        await using var conn = await db.OpenConnectionAsync(ct);

        foreach (var def in linkDefs)
        {
            string patternJson;
            if (!def.IsChildTable)
            {
                patternJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [def.FieldName] = targetName
                });
            }
            else
            {
                patternJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [def.TableFieldName!] = new[] { new Dictionary<string, string> { [def.FieldName] = targetName } }
                });
            }

            await using var cmd = new NpgsqlCommand(
                "SELECT doctype, name FROM frogframe_documents " +
                "WHERE tenant_id = $1 AND doctype = $2 AND docstatus != 2 " +
                "AND NOT (doctype = $3 AND name = $4) " +
                "AND data @> $5::jsonb " +
                "LIMIT 1", conn);

            cmd.Parameters.AddWithValue(tenantId);
            cmd.Parameters.AddWithValue(def.LinkingDocType);
            cmd.Parameters.AddWithValue(targetDocType);
            cmd.Parameters.AddWithValue(targetName);
            cmd.Parameters.AddWithValue(patternJson);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var linkingDocType = reader.GetString(0);
                var linkingDocName = reader.GetString(1);
                return new DocumentLinkReference(linkingDocType, linkingDocName, def.FieldName, def.FieldLabel);
            }
        }

        return null;
    }

    // ---------------- Outbox ----------------

    private const string OutboxColumns = "id, tenant_id, type, payload::text, idempotency_key, created_at, attempts, next_attempt_at, processed_at, last_error, dead_lettered";

    private static OutboxMessage ReadOutbox(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0), TenantId = r.GetString(1), Type = r.GetString(2),
        Payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(3)) ?? new(),
        IdempotencyKey = r.GetString(4), CreatedAt = r.GetFieldValue<DateTimeOffset>(5), Attempts = r.GetInt32(6),
        NextAttemptAt = r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
        ProcessedAt = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        LastError = r.IsDBNull(9) ? null : r.GetString(9), DeadLettered = r.GetBoolean(10),
    };

    /// <summary>
    /// Mengklaim pesan yang jatuh tempo dengan FOR UPDATE SKIP LOCKED + kunci sementara (locked_until),
    /// sehingga beberapa instance worker tidak memproses pesan yang sama secara bersamaan.
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> FetchDueAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"UPDATE frogframe_outbox SET locked_until = now() + interval '2 minutes' WHERE id IN (" +
            "SELECT id FROM frogframe_outbox WHERE processed_at IS NULL AND NOT dead_lettered " +
            "AND (next_attempt_at IS NULL OR next_attempt_at <= $1) AND (locked_until IS NULL OR locked_until < now()) " +
            $"ORDER BY created_at LIMIT $2 FOR UPDATE SKIP LOCKED) RETURNING {OutboxColumns}");
        cmd.Parameters.AddWithValue(now); cmd.Parameters.AddWithValue(max);
        var list = new List<OutboxMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadOutbox(r));
        return list.OrderBy(m => m.CreatedAt).ToList();
    }

    public async Task UpdateAsync(OutboxMessage m, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE frogframe_outbox SET attempts=$2, next_attempt_at=$3, processed_at=$4, last_error=$5, dead_lettered=$6, locked_until=NULL WHERE id=$1");
        cmd.Parameters.AddWithValue(m.Id); cmd.Parameters.AddWithValue(m.Attempts);
        cmd.Parameters.AddWithValue((object?)m.NextAttemptAt ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)m.ProcessedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)m.LastError ?? DBNull.Value); cmd.Parameters.AddWithValue(m.DeadLettered);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset processedBefore, int max, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "DELETE FROM frogframe_outbox WHERE id IN (SELECT id FROM frogframe_outbox WHERE processed_at < $1 AND NOT dead_lettered LIMIT $2)");
        cmd.Parameters.AddWithValue(processedBefore);
        cmd.Parameters.AddWithValue(max);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnqueueAsync(OutboxMessage m, CancellationToken ct = default)
    {
        await using var ins = db.CreateCommand(
            "INSERT INTO frogframe_outbox (id, tenant_id, type, payload, idempotency_key, created_at) VALUES ($1,$2,$3,$4::jsonb,$5,$6) " +
            "ON CONFLICT (idempotency_key) DO NOTHING");
        ins.Parameters.AddWithValue(m.Id);
        ins.Parameters.AddWithValue(m.TenantId);
        ins.Parameters.AddWithValue(m.Type);
        ins.Parameters.AddWithValue(JsonSerializer.Serialize(m.Payload));
        ins.Parameters.AddWithValue(m.IdempotencyKey);
        ins.Parameters.AddWithValue(m.CreatedAt);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public IReadOnlyList<OutboxMessage> Snapshot()
    {
        using var cmd = db.CreateCommand($"SELECT {OutboxColumns} FROM frogframe_outbox ORDER BY created_at DESC LIMIT 200");
        using var r = cmd.ExecuteReader();
        var list = new List<OutboxMessage>();
        while (r.Read()) list.Add(ReadOutbox(r));
        return list;
    }
}

public sealed class PostgresNotificationStore(NpgsqlDataSource db) : INotificationStore
{
    public void Add(Notification n)
    {
        using var cmd = db.CreateCommand(
            "INSERT INTO frogframe_notifications (id, tenant_id, user_id, subject, body, doctype, doc_name, at, read) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(n.Id); cmd.Parameters.AddWithValue(n.TenantId); cmd.Parameters.AddWithValue(n.UserId);
        cmd.Parameters.AddWithValue(n.Subject); cmd.Parameters.AddWithValue(n.Body);
        cmd.Parameters.AddWithValue((object?)n.DocType ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)n.DocName ?? DBNull.Value);
        cmd.Parameters.AddWithValue(n.At); cmd.Parameters.AddWithValue(n.Read);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Notification> ForUser(string tenantId, string userId)
    {
        using var cmd = db.CreateCommand(
            "SELECT id, tenant_id, user_id, subject, body, doctype, doc_name, at, read FROM frogframe_notifications WHERE tenant_id=$1 AND user_id=$2 ORDER BY at DESC LIMIT 200");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(userId);
        using var r = cmd.ExecuteReader();
        var list = new List<Notification>();
        while (r.Read())
            list.Add(new Notification(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetBoolean(8)));
        return list;
    }

    public void MarkRead(string tenantId, string userId, Guid id)
    {
        using var cmd = db.CreateCommand("UPDATE frogframe_notifications SET read = true WHERE id=$1 AND tenant_id=$2 AND user_id=$3");
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();
    }
}

public sealed class PostgresNotificationRuleStore(NpgsqlDataSource db) : INotificationRuleStore
{
    public async Task<IReadOnlyList<NotificationRule>> ListAsync(string tenantId, string? docType = null, bool onlyActive = false, CancellationToken ct = default)
    {
        var sql = "SELECT id, tenant_id, name, doctype, trigger, trigger_field, days_offset, condition, channel, receiver_type, receiver_value, subject_template, message_template, is_active, created_at, modified_at FROM frogframe_notification_rules WHERE tenant_id = $1";
        if (onlyActive) sql += " AND is_active = true";
        if (!string.IsNullOrEmpty(docType)) sql += " AND doctype = $2";
        sql += " ORDER BY modified_at DESC";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);
        if (!string.IsNullOrEmpty(docType)) cmd.Parameters.AddWithValue(docType);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<NotificationRule>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadRule(r));
        }
        return list;
    }

    public async Task<NotificationRule?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, tenant_id, name, doctype, trigger, trigger_field, days_offset, condition, channel, receiver_type, receiver_value, subject_template, message_template, is_active, created_at, modified_at FROM frogframe_notification_rules WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadRule(r);
        }
        return null;
    }

    public async Task SaveAsync(NotificationRule rule, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_notification_rules 
                (id, tenant_id, name, doctype, trigger, trigger_field, days_offset, condition, channel, receiver_type, receiver_value, subject_template, message_template, is_active, created_at, modified_at)
            VALUES 
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                doctype = EXCLUDED.doctype,
                trigger = EXCLUDED.trigger,
                trigger_field = EXCLUDED.trigger_field,
                days_offset = EXCLUDED.days_offset,
                condition = EXCLUDED.condition,
                channel = EXCLUDED.channel,
                receiver_type = EXCLUDED.receiver_type,
                receiver_value = EXCLUDED.receiver_value,
                subject_template = EXCLUDED.subject_template,
                message_template = EXCLUDED.message_template,
                is_active = EXCLUDED.is_active,
                modified_at = EXCLUDED.modified_at");

        cmd.Parameters.AddWithValue(rule.Id);
        cmd.Parameters.AddWithValue(rule.TenantId);
        cmd.Parameters.AddWithValue(rule.Name);
        cmd.Parameters.AddWithValue(rule.DocType);
        cmd.Parameters.AddWithValue(rule.Trigger.ToString());
        cmd.Parameters.AddWithValue((object?)rule.TriggerField ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)rule.DaysOffset ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)rule.Condition ?? DBNull.Value);
        cmd.Parameters.AddWithValue(rule.Channel.ToString());
        cmd.Parameters.AddWithValue(rule.ReceiverType.ToString());
        cmd.Parameters.AddWithValue((object?)rule.ReceiverValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue(rule.SubjectTemplate);
        cmd.Parameters.AddWithValue(rule.MessageTemplate);
        cmd.Parameters.AddWithValue(rule.IsActive);
        cmd.Parameters.AddWithValue(rule.CreatedAt);
        cmd.Parameters.AddWithValue(rule.ModifiedAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_notification_rules WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NotificationRule ReadRule(NpgsqlDataReader r)
    {
        return new NotificationRule
        {
            Id = r.GetString(0),
            TenantId = r.GetString(1),
            Name = r.GetString(2),
            DocType = r.GetString(3),
            Trigger = Enum.Parse<NotificationTrigger>(r.GetString(4)),
            TriggerField = r.IsDBNull(5) ? null : r.GetString(5),
            DaysOffset = r.IsDBNull(6) ? null : r.GetInt32(6),
            Condition = r.IsDBNull(7) ? null : r.GetString(7),
            Channel = Enum.Parse<NotificationChannel>(r.GetString(8)),
            ReceiverType = Enum.Parse<NotificationReceiverType>(r.GetString(9)),
            ReceiverValue = r.IsDBNull(10) ? null : r.GetString(10),
            SubjectTemplate = r.GetString(11),
            MessageTemplate = r.GetString(12),
            IsActive = r.GetBoolean(13),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(14),
            ModifiedAt = r.GetFieldValue<DateTimeOffset>(15)
        };
    }
}

public sealed class PostgresUserPreferenceStore(NpgsqlDataSource db) : IUserPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string tenantId, string userId, string key, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT pref_value::text FROM frogframe_user_preferences WHERE tenant_id = $1 AND user_id = $2 AND pref_key = $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(key);

        var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (val is null or DBNull) return default;

        var json = (string)val;
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public async Task SetAsync<T>(string tenantId, string userId, string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_user_preferences (tenant_id, user_id, pref_key, pref_value, updated_at)
            VALUES ($1, $2, $3, $4::jsonb, $5)
            ON CONFLICT (tenant_id, user_id, pref_key) DO UPDATE SET
                pref_value = EXCLUDED.pref_value,
                updated_at = EXCLUDED.updated_at");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(json);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, string userId, string key, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_user_preferences WHERE tenant_id = $1 AND user_id = $2 AND pref_key = $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(key);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public sealed class PostgresMetadataVersionStore(NpgsqlDataSource db) : IMetadataVersionStore, IAtomicDocTypeMigrationPublisher
{
    public DataMigrationPreviewResult PreviewMigration(CompiledDocType source, CompiledDocType target, int maxDocuments)
    {
        if (maxDocuments <= 0 || target.Def.DataMigration is not { } plan || plan.FromVersion != source.Version)
            return new(false, 0, "Rencana migrasi tidak cocok dengan metadata sumber aktif.");

        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            LockDocType(conn, tx, target.Name);
            using (var active = new NpgsqlCommand("SELECT version FROM frogframe_metadata_versions WHERE doctype=$1 AND status='Published' FOR SHARE", conn, tx))
            {
                active.Parameters.AddWithValue(target.Name);
                if (active.ExecuteScalar() is not int activeVersion || activeVersion != source.Version)
                    throw new InvalidOperationException("Metadata aktif berubah. Muat ulang draft sebelum dry-run.");
            }

            var count = 0;
            using (var select = new NpgsqlCommand(
                "SELECT tenant_id, doctype, name, owner, docstatus, workflow_state, created, modified, modified_by, version, schema_version, workflow_version, amended_from, data::text " +
                "FROM frogframe_documents WHERE doctype=$1 ORDER BY tenant_id, name LIMIT $2 FOR UPDATE", conn, tx))
            {
                select.Parameters.AddWithValue(target.Name);
                select.Parameters.AddWithValue(maxDocuments + 1);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var schemaVersion = reader.GetInt32(10);
                    if (schemaVersion != source.Version)
                        throw new InvalidOperationException($"Dokumen {reader.GetString(2)} memiliki schema versi {schemaVersion}; hanya versi {source.Version} yang dapat dimigrasikan.");
                    var document = new Document
                    {
                        TenantId = reader.GetString(0), DocType = reader.GetString(1), Name = reader.GetString(2), Owner = reader.GetString(3),
                        DocStatus = reader.GetInt16(4), WorkflowState = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Created = reader.GetFieldValue<DateTimeOffset>(6), Modified = reader.GetFieldValue<DateTimeOffset>(7), ModifiedBy = reader.GetString(8),
                        Version = reader.GetInt32(9), SchemaVersion = schemaVersion,
                        WorkflowVersion = reader.IsDBNull(11) ? null : reader.GetInt32(11), AmendedFrom = reader.IsDBNull(12) ? null : reader.GetString(12),
                        Data = JsonCodec.Read(reader.GetString(13), source, _ => null)
                    };
                    DocTypeDataMigrator.Transform(document, target, plan.Operations, "dry-run", DateTimeOffset.UtcNow);
                    count++;
                }
            }
            if (count > maxDocuments) throw new InvalidOperationException($"Migrasi dibatasi maksimal {maxDocuments} dokumen.");
            tx.Commit();
            return new(true, count);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            return new(false, 0, ex.Message);
        }
    }

    public IReadOnlyList<MetadataVersion> LoadAll()
    {
        using var cmd = db.CreateCommand("SELECT doctype, version, definition::text, status, published_by, published_at, changes::text FROM frogframe_metadata_versions ORDER BY doctype, version");
        using var r = cmd.ExecuteReader();
        var list = new List<MetadataVersion>();
        while (r.Read())
            list.Add(new MetadataVersion(r.GetString(0), r.GetInt32(1), MetadataJson.ParseDocType(r.GetString(2)),
                Enum.Parse<MetadataStatus>(r.GetString(3)), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
                JsonSerializer.Deserialize<List<MetadataChange>>(r.GetString(6)) ?? []));
        return list;
    }

    public void Save(MetadataVersion v)
    {
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        LockDocType(conn, tx, v.DocType);
        SavePublishedVersion(conn, tx, v);
        tx.Commit();
    }

    public DataMigrationResult MigrateAndPublish(MetadataVersion version, CompiledDocType source, CompiledDocType target,
        int expectedDocuments, int maxDocuments)
    {
        if (maxDocuments <= 0 || expectedDocuments < 0 || expectedDocuments > maxDocuments)
            throw new ArgumentOutOfRangeException(nameof(expectedDocuments));
        if (target.Def.DataMigration is not { } plan || plan.FromVersion != source.Version || target.Version != version.Version)
            throw new InvalidOperationException("Rencana migrasi tidak cocok dengan versi sumber dan target.");

        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            LockDocType(conn, tx, version.DocType);
            using (var active = new NpgsqlCommand("SELECT version FROM frogframe_metadata_versions WHERE doctype=$1 AND status='Published' FOR UPDATE", conn, tx))
            {
                active.Parameters.AddWithValue(version.DocType);
                if (active.ExecuteScalar() is not int activeVersion || activeVersion != source.Version)
                    throw new InvalidOperationException("Metadata aktif berubah sejak dry-run. Jalankan ulang dry-run.");
            }

            var documents = new List<Document>();
            using (var select = new NpgsqlCommand(
                "SELECT tenant_id, doctype, name, owner, docstatus, workflow_state, created, modified, modified_by, version, schema_version, workflow_version, amended_from, data::text " +
                "FROM frogframe_documents WHERE doctype=$1 ORDER BY tenant_id, name LIMIT $2 FOR UPDATE", conn, tx))
            {
                select.Parameters.AddWithValue(version.DocType);
                select.Parameters.AddWithValue(maxDocuments + 1);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var schemaVersion = reader.GetInt32(10);
                    if (schemaVersion != source.Version)
                        throw new InvalidOperationException($"Dokumen {reader.GetString(2)} memiliki schema versi {schemaVersion}; hanya versi {source.Version} yang dapat dimigrasikan.");
                    documents.Add(new Document
                    {
                        TenantId = reader.GetString(0), DocType = reader.GetString(1), Name = reader.GetString(2), Owner = reader.GetString(3),
                        DocStatus = reader.GetInt16(4), WorkflowState = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Created = reader.GetFieldValue<DateTimeOffset>(6), Modified = reader.GetFieldValue<DateTimeOffset>(7), ModifiedBy = reader.GetString(8),
                        Version = reader.GetInt32(9), SchemaVersion = schemaVersion,
                        WorkflowVersion = reader.IsDBNull(11) ? null : reader.GetInt32(11), AmendedFrom = reader.IsDBNull(12) ? null : reader.GetString(12),
                        Data = JsonCodec.Read(reader.GetString(13), source, _ => null)
                    });
                }
            }

            if (documents.Count > maxDocuments)
                throw new InvalidOperationException($"Migrasi dibatasi maksimal {maxDocuments} dokumen; tidak ada perubahan yang disimpan.");
            if (documents.Count != expectedDocuments)
                throw new InvalidOperationException("Jumlah dokumen berubah sejak dry-run. Jalankan ulang dry-run sebelum publish.");

            foreach (var document in documents)
            {
                var (migrated, changes) = DocTypeDataMigrator.Transform(document, target, plan.Operations, version.PublishedBy, version.PublishedAt);
                using (var update = new NpgsqlCommand(
                    "UPDATE frogframe_documents SET modified=$4, modified_by=$5, version=$6, schema_version=$7, data=$8::jsonb " +
                    "WHERE tenant_id=$1 AND doctype=$2 AND name=$3 AND version=$9", conn, tx))
                {
                    update.Parameters.AddWithValue(migrated.TenantId); update.Parameters.AddWithValue(migrated.DocType); update.Parameters.AddWithValue(migrated.Name);
                    update.Parameters.AddWithValue(migrated.Modified); update.Parameters.AddWithValue(migrated.ModifiedBy);
                    update.Parameters.AddWithValue(migrated.Version); update.Parameters.AddWithValue(migrated.SchemaVersion);
                    update.Parameters.AddWithValue(JsonCodec.Write(migrated.Data)); update.Parameters.AddWithValue(document.Version);
                    if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Dokumen {document.Name} berubah selama migrasi.");
                }
                using var audit = new NpgsqlCommand(
                    "INSERT INTO frogframe_audit (tenant_id, doctype, doc_name, action, actor, at, changes, schema_version) " +
                    "VALUES ($1,$2,$3,'Migrate',$4,$5,$6::jsonb,$7)", conn, tx);
                audit.Parameters.AddWithValue(migrated.TenantId); audit.Parameters.AddWithValue(migrated.DocType); audit.Parameters.AddWithValue(migrated.Name);
                audit.Parameters.AddWithValue(version.PublishedBy); audit.Parameters.AddWithValue(version.PublishedAt);
                audit.Parameters.AddWithValue(JsonSerializer.Serialize(changes)); audit.Parameters.AddWithValue(migrated.SchemaVersion);
                audit.ExecuteNonQuery();
            }

            SavePublishedVersion(conn, tx, version);
            tx.Commit();
            return new DataMigrationResult(documents.Count);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static void LockDocType(NpgsqlConnection conn, NpgsqlTransaction tx, string docType)
    {
        using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('FrogFrame.doctype'), hashtext($1))", conn, tx);
        cmd.Parameters.AddWithValue(docType);
        cmd.ExecuteScalar();
    }

    private static void SavePublishedVersion(NpgsqlConnection conn, NpgsqlTransaction tx, MetadataVersion v)
    {
        using (var dep = new NpgsqlCommand("UPDATE frogframe_metadata_versions SET status = 'Deprecated' WHERE doctype = $1 AND status = 'Published'", conn, tx))
        {
            dep.Parameters.AddWithValue(v.DocType);
            dep.ExecuteNonQuery();
        }
        using (var ins = new NpgsqlCommand(
            "INSERT INTO frogframe_metadata_versions (doctype, version, definition, status, published_by, published_at, changes) VALUES ($1,$2,$3::jsonb,$4,$5,$6,$7::jsonb)", conn, tx))
        {
            ins.Parameters.AddWithValue(v.DocType); ins.Parameters.AddWithValue(v.Version);
            ins.Parameters.AddWithValue(MetadataJson.Serialize(v.Def)); ins.Parameters.AddWithValue(v.Status.ToString());
            ins.Parameters.AddWithValue(v.PublishedBy); ins.Parameters.AddWithValue(v.PublishedAt);
            ins.Parameters.AddWithValue(JsonSerializer.Serialize(v.Changes));
            ins.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<WorkflowVersion> LoadAllWorkflows()
    {
        using var cmd = db.CreateCommand("SELECT name, doctype, version, definition::text, status, published_by, published_at, changes::text FROM frogframe_workflow_versions ORDER BY name, version");
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowVersion>();
        while (r.Read())
            list.Add(new WorkflowVersion(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2), MetadataJson.ParseWorkflow(r.GetString(3)),
                Enum.Parse<MetadataStatus>(r.GetString(4)), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
                JsonSerializer.Deserialize<List<MetadataChange>>(r.GetString(7)) ?? []));
        return list;
    }

    public void SaveWorkflow(WorkflowVersion v)
    {
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var dep = new NpgsqlCommand("UPDATE frogframe_workflow_versions SET status = 'Deprecated' WHERE name = $1 AND status = 'Published'", conn, tx))
        {
            dep.Parameters.AddWithValue(v.Name);
            dep.ExecuteNonQuery();
        }
        using (var ins = new NpgsqlCommand(
            "INSERT INTO frogframe_workflow_versions (name, doctype, version, definition, status, published_by, published_at, changes) VALUES ($1,$2,$3,$4::jsonb,$5,$6,$7,$8::jsonb)", conn, tx))
        {
            ins.Parameters.AddWithValue(v.Name); ins.Parameters.AddWithValue((object?)v.DocType ?? DBNull.Value); ins.Parameters.AddWithValue(v.Version);
            ins.Parameters.AddWithValue(MetadataJson.Serialize(v.Def)); ins.Parameters.AddWithValue(v.Status.ToString());
            ins.Parameters.AddWithValue(v.PublishedBy); ins.Parameters.AddWithValue(v.PublishedAt);
            ins.Parameters.AddWithValue(JsonSerializer.Serialize(v.Changes));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }
}

public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>Membangun indeks dari metadata (CONCURRENTLY). Aman dijalankan di latar belakang saat startup.</summary>
    public static Task SyncDocumentIndexesAsync(this IServiceProvider services, CancellationToken ct = default) =>
        services.GetService<DocumentIndexSync>()?.SyncAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// Mengganti penyimpanan in-memory dengan PostgreSQL. Boleh dipanggil sebelum atau sesudah AddFrogFramework.
    /// Jalankan <see cref="MigrateFrogFrameworkDatabaseAsync"/> saat startup sebelum aplikasi melayani request.
    /// </summary>
    /// <param name="schemaConnectionString">
    /// Opsional: koneksi PEMILIK skema untuk DDL (migrasi, sinkronisasi indeks). Bila diisi, <paramref name="connectionString"/>
    /// sebaiknya peran app tanpa hak UPDATE/DELETE pada buku besar (lihat deploy/sql/gl-roles.sql). Kosong = satu koneksi.
    /// </param>
    public static IServiceCollection AddFrogFrameworkPostgreSql(this IServiceCollection services, string connectionString, string? schemaConnectionString = null)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new SchemaDataSource(string.IsNullOrWhiteSpace(schemaConnectionString)
            ? sp.GetRequiredService<NpgsqlDataSource>() : NpgsqlDataSource.Create(schemaConnectionString), owns: !string.IsNullOrWhiteSpace(schemaConnectionString)));
        services.AddSingleton<PostgresDocumentStore>();
        services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(sp => sp.GetRequiredService<PostgresDocumentStore>()));
        services.Replace(ServiceDescriptor.Singleton<Framework.Accounting.ILedgerReader, PostgresLedgerReader>());
        services.Replace(ServiceDescriptor.Singleton<IOutboxStore>(sp => sp.GetRequiredService<PostgresDocumentStore>()));
        services.Replace(ServiceDescriptor.Singleton<IDatabaseHealthCheck>(sp => sp.GetRequiredService<PostgresDocumentStore>()));
        services.Replace(ServiceDescriptor.Singleton<INotificationStore, PostgresNotificationStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Notifications.INotificationRuleStore, PostgresNotificationRuleStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Integrations.IWebhookStore, PostgresWebhookStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Integrations.IAutoRepeatStore, PostgresAutoRepeatStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Scripting.IServerScriptStore, PostgresServerScriptStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Communication.IEmailAccountStore, PostgresEmailAccountStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Communication.ICommunicationStore, PostgresCommunicationStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Preferences.IUserPreferenceStore, PostgresUserPreferenceStore>());
        services.Replace(ServiceDescriptor.Singleton<IMetadataVersionStore, PostgresMetadataVersionStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Security.IUserAccountRepository, PostgresUserAccountRepository>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Files.IFileStore, PostgresFileStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Files.IAttachmentStore, PostgresAttachmentStore>());
        services.AddSingleton<DocumentIndexSync>();
        services.Replace(ServiceDescriptor.Singleton<Framework.Security.IApiKeyStore, PostgresApiKeyStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Api.IQueryApiStore, PostgresQueryApiStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Imports.IImportLogStore, PostgresImportLogStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Navigation.INavigationMenuStore, PostgresNavigationMenuStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Security.IRoleStore, PostgresRoleStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Jobs.IDistributedLock, PostgresDistributedLock>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Jobs.ITenantProvider, PostgresTenantProvider>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Jobs.IScheduledJobLogStore, PostgresScheduledJobLogStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Observability.IErrorLogStore, PostgresErrorLogStore>());
        services.AddDataProtection()
            .SetApplicationName("FrogFrame");
        services.AddSingleton<Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository, PostgresXmlRepository>();
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository>((options, repo) =>
            {
                options.XmlRepository = repo;
            });
        services.AddSingleton<SchemaMigrator>();
        return services;
    }

    public static Task MigrateFrogFrameworkDatabaseAsync(this IServiceProvider services, CancellationToken ct = default) =>
        services.GetRequiredService<SchemaMigrator>().MigrateAsync(ct);
}
