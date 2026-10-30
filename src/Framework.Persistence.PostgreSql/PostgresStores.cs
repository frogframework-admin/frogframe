using System.Text.Json;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Store PostgreSQL untuk dokumen, audit, outbox, dan nomor seri. Satu CommitAsync = satu transaksi database:
/// dokumen, audit, dan event outbox tersimpan bersama atau tidak sama sekali (RFC §6, §12).
/// </summary>
public sealed class PostgresDocumentStore(NpgsqlDataSource db, IServiceProvider services) : IDocumentStore, IOutboxStore
{
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
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM docframe_documents d WHERE d.tenant_id = $1 AND d.doctype = $2 AND d.name = $3");
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
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM (SELECT 1 FROM docframe_documents d WHERE {whereSql} LIMIT {QueryLimits.CountCap + 1}) AS capped", conn))
        {
            // Query hitung hanya memakai parameter WHERE (bukan ORDER BY / OFFSET / LIMIT).
            foreach (var p in b.Parameters.Take(whereParamCount)) count.Parameters.Add(p.Clone());
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }
        var capped = total > QueryLimits.CountCap;
        if (capped) total = QueryLimits.CountCap;
        var items = new List<Document>();
        await using (var cmd = new NpgsqlCommand($"SELECT {Columns} FROM docframe_documents d WHERE {whereSql} ORDER BY {order} OFFSET {skip} LIMIT {take}", conn))
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
            "INSERT INTO docframe_series (tenant_id, doctype, prefix, current) VALUES ($1, $2, $3, 1) " +
            "ON CONFLICT (tenant_id, doctype, prefix) DO UPDATE SET current = docframe_series.current + 1 RETURNING current");
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
            foreach (var (doc, expected) in uow.Upserts)
            {
                if (expected is null) await InsertDocument(conn, tx, doc, ct);
                else await UpdateDocument(conn, tx, doc, expected.Value, ct);
            }
            foreach (var (tenant, docType, name, expected) in uow.Deletes)
            {
                await using var del = new NpgsqlCommand("DELETE FROM docframe_documents WHERE tenant_id = $1 AND doctype = $2 AND name = $3 AND version = $4", conn, tx);
                del.Parameters.AddWithValue(tenant); del.Parameters.AddWithValue(docType); del.Parameters.AddWithValue(name); del.Parameters.AddWithValue(expected);
                if (await del.ExecuteNonQueryAsync(ct) == 0)
                    throw new ConcurrencyException(name, expected, await CurrentVersion(conn, tx, tenant, docType, name, ct));
            }
            foreach (var a in uow.Audits)
            {
                await using var ins = new NpgsqlCommand(
                    "INSERT INTO docframe_audit (tenant_id, doctype, doc_name, action, actor, at, changes, comment, from_state, to_state, workflow_version, schema_version) " +
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
                    "INSERT INTO docframe_outbox (id, tenant_id, type, payload, idempotency_key, created_at) VALUES ($1,$2,$3,$4::jsonb,$5,$6) " +
                    "ON CONFLICT (idempotency_key) DO NOTHING", conn, tx);
                ins.Parameters.AddWithValue(m.Id); ins.Parameters.AddWithValue(m.TenantId); ins.Parameters.AddWithValue(m.Type);
                ins.Parameters.AddWithValue(JsonSerializer.Serialize(m.Payload)); ins.Parameters.AddWithValue(m.IdempotencyKey); ins.Parameters.AddWithValue(m.CreatedAt);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InsertDocument(NpgsqlConnection conn, NpgsqlTransaction tx, Document d, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO docframe_documents (tenant_id, doctype, name, owner, docstatus, workflow_state, created, modified, modified_by, version, schema_version, workflow_version, amended_from, data) " +
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
            "UPDATE docframe_documents SET owner=$4, docstatus=$5, workflow_state=$6, created=$7, modified=$8, modified_by=$9, version=$10, " +
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
        await using var cmd = new NpgsqlCommand("SELECT version FROM docframe_documents WHERE tenant_id=$1 AND doctype=$2 AND name=$3", conn, tx);
        cmd.Parameters.AddWithValue(tenant); cmd.Parameters.AddWithValue(docType); cmd.Parameters.AddWithValue(name);
        return await cmd.ExecuteScalarAsync(ct) is int v ? v : 0;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string tenantId, string docType, string name, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, tenant_id, doctype, doc_name, action, actor, at, changes::text, comment, from_state, to_state, workflow_version, schema_version " +
            "FROM docframe_audit WHERE tenant_id=$1 AND doctype=$2 AND doc_name=$3 ORDER BY id DESC");
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
            $"UPDATE docframe_outbox SET locked_until = now() + interval '2 minutes' WHERE id IN (" +
            "SELECT id FROM docframe_outbox WHERE processed_at IS NULL AND NOT dead_lettered " +
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
            "UPDATE docframe_outbox SET attempts=$2, next_attempt_at=$3, processed_at=$4, last_error=$5, dead_lettered=$6, locked_until=NULL WHERE id=$1");
        cmd.Parameters.AddWithValue(m.Id); cmd.Parameters.AddWithValue(m.Attempts);
        cmd.Parameters.AddWithValue((object?)m.NextAttemptAt ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)m.ProcessedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)m.LastError ?? DBNull.Value); cmd.Parameters.AddWithValue(m.DeadLettered);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset processedBefore, int max, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "DELETE FROM docframe_outbox WHERE id IN (SELECT id FROM docframe_outbox WHERE processed_at < $1 AND NOT dead_lettered LIMIT $2)");
        cmd.Parameters.AddWithValue(processedBefore);
        cmd.Parameters.AddWithValue(max);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public IReadOnlyList<OutboxMessage> Snapshot()
    {
        using var cmd = db.CreateCommand($"SELECT {OutboxColumns} FROM docframe_outbox ORDER BY created_at DESC LIMIT 200");
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
            "INSERT INTO docframe_notifications (id, tenant_id, user_id, subject, body, doctype, doc_name, at, read) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(n.Id); cmd.Parameters.AddWithValue(n.TenantId); cmd.Parameters.AddWithValue(n.UserId);
        cmd.Parameters.AddWithValue(n.Subject); cmd.Parameters.AddWithValue(n.Body);
        cmd.Parameters.AddWithValue((object?)n.DocType ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)n.DocName ?? DBNull.Value);
        cmd.Parameters.AddWithValue(n.At); cmd.Parameters.AddWithValue(n.Read);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Notification> ForUser(string tenantId, string userId)
    {
        using var cmd = db.CreateCommand(
            "SELECT id, tenant_id, user_id, subject, body, doctype, doc_name, at, read FROM docframe_notifications WHERE tenant_id=$1 AND user_id=$2 ORDER BY at DESC LIMIT 200");
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
        using var cmd = db.CreateCommand("UPDATE docframe_notifications SET read = true WHERE id=$1 AND tenant_id=$2 AND user_id=$3");
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();
    }
}

public sealed class PostgresMetadataVersionStore(NpgsqlDataSource db) : IMetadataVersionStore
{
    public IReadOnlyList<MetadataVersion> LoadAll()
    {
        using var cmd = db.CreateCommand("SELECT doctype, version, definition::text, status, published_by, published_at, changes::text FROM docframe_metadata_versions ORDER BY doctype, version");
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
        using (var dep = new NpgsqlCommand("UPDATE docframe_metadata_versions SET status = 'Deprecated' WHERE doctype = $1 AND status = 'Published'", conn, tx))
        {
            dep.Parameters.AddWithValue(v.DocType);
            dep.ExecuteNonQuery();
        }
        using (var ins = new NpgsqlCommand(
            "INSERT INTO docframe_metadata_versions (doctype, version, definition, status, published_by, published_at, changes) VALUES ($1,$2,$3::jsonb,$4,$5,$6,$7::jsonb)", conn, tx))
        {
            ins.Parameters.AddWithValue(v.DocType); ins.Parameters.AddWithValue(v.Version);
            ins.Parameters.AddWithValue(MetadataJson.Serialize(v.Def)); ins.Parameters.AddWithValue(v.Status.ToString());
            ins.Parameters.AddWithValue(v.PublishedBy); ins.Parameters.AddWithValue(v.PublishedAt);
            ins.Parameters.AddWithValue(JsonSerializer.Serialize(v.Changes));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<WorkflowVersion> LoadAllWorkflows()
    {
        using var cmd = db.CreateCommand("SELECT name, doctype, version, definition::text, status, published_by, published_at, changes::text FROM docframe_workflow_versions ORDER BY name, version");
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowVersion>();
        while (r.Read())
            list.Add(new WorkflowVersion(r.GetString(0), r.GetString(1), r.GetInt32(2), MetadataJson.ParseWorkflow(r.GetString(3)),
                Enum.Parse<MetadataStatus>(r.GetString(4)), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
                JsonSerializer.Deserialize<List<MetadataChange>>(r.GetString(7)) ?? []));
        return list;
    }

    public void SaveWorkflow(WorkflowVersion v)
    {
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var dep = new NpgsqlCommand("UPDATE docframe_workflow_versions SET status = 'Deprecated' WHERE name = $1 AND status = 'Published'", conn, tx))
        {
            dep.Parameters.AddWithValue(v.Name);
            dep.ExecuteNonQuery();
        }
        using (var ins = new NpgsqlCommand(
            "INSERT INTO docframe_workflow_versions (name, doctype, version, definition, status, published_by, published_at, changes) VALUES ($1,$2,$3,$4::jsonb,$5,$6,$7,$8::jsonb)", conn, tx))
        {
            ins.Parameters.AddWithValue(v.Name); ins.Parameters.AddWithValue(v.DocType); ins.Parameters.AddWithValue(v.Version);
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
    /// Mengganti penyimpanan in-memory dengan PostgreSQL. Boleh dipanggil sebelum atau sesudah AddDocFramework.
    /// Jalankan <see cref="MigrateDocFrameworkDatabaseAsync"/> saat startup sebelum aplikasi melayani request.
    /// </summary>
    public static IServiceCollection AddDocFrameworkPostgreSql(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<PostgresDocumentStore>();
        services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(sp => sp.GetRequiredService<PostgresDocumentStore>()));
        services.Replace(ServiceDescriptor.Singleton<IOutboxStore>(sp => sp.GetRequiredService<PostgresDocumentStore>()));
        services.Replace(ServiceDescriptor.Singleton<INotificationStore, PostgresNotificationStore>());
        services.Replace(ServiceDescriptor.Singleton<IMetadataVersionStore, PostgresMetadataVersionStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Security.IUserAccountRepository, PostgresUserAccountRepository>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Files.IFileStore, PostgresFileStore>());
        services.AddSingleton<DocumentIndexSync>();
        services.Replace(ServiceDescriptor.Singleton<Framework.Security.IApiKeyStore, PostgresApiKeyStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Api.IQueryApiStore, PostgresQueryApiStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Imports.IImportLogStore, PostgresImportLogStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Navigation.INavigationMenuStore, PostgresNavigationMenuStore>());
        services.AddSingleton<SchemaMigrator>();
        return services;
    }

    public static Task MigrateDocFrameworkDatabaseAsync(this IServiceProvider services, CancellationToken ct = default) =>
        services.GetRequiredService<SchemaMigrator>().MigrateAsync(ct);
}
