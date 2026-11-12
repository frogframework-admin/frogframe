using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Framework.Dashboards;
using Framework.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

public sealed class ReportingOptions
{
    /// <summary>Connection string user database yang HANYA boleh SELECT pada skema reporting.</summary>
    public string ReadOnlyConnectionString { get; set; } = "";
    /// <summary>Role database read-only yang diberi SELECT pada view reporting saat sinkronisasi.</summary>
    public string ReadOnlyRole { get; set; } = "frogframe_ro";
    public int StatementTimeoutSeconds { get; set; } = 5;
    /// <summary>
    /// Opsional: koneksi (bisa menulis) ke database ANALITIK tempat view reporting dan tabel sesi berada — mis. instance
    /// replika LOGIS dari frogframe_documents. Kosong = database aplikasi. Replika FISIK tidak didukung: read-only,
    /// sehingga tabel sesi (isolasi tenant) tidak bisa ditulis di sana.
    /// </summary>
    public string? AnalyticsConnectionString { get; set; }
}

/// <summary>Database tempat view reporting + tabel sesi berada (database aplikasi, atau instance analitik terpisah).</summary>
/// <param name="owns">True bila data source dibuat khusus (instance analitik) dan harus ditutup bersama wrapper ini.</param>
public sealed class AnalyticsDataSource(NpgsqlDataSource source, bool owns = false) : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = source;
    public ValueTask DisposeAsync() => owns ? Source.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>Sumber data koneksi read-only (dipisah dari NpgsqlDataSource utama). Menutup pool-nya saat dibuang.</summary>
public sealed class ReadOnlyDataSource(NpgsqlDataSource source) : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = source;
    public ValueTask DisposeAsync() => Source.DisposeAsync();
}

/// <summary>
/// Membuat view reporting.&lt;doctype&gt; (dan reporting.&lt;doctype&gt;_&lt;tabel&gt; untuk child table) dari metadata aktif,
/// berisi kolom standar + field dengan tipe SQL yang sesuai. Field berlevel izin &gt; 0 dan field gambar tidak diekspos.
/// Setiap view memfilter tenant dari frogframe_reporting_sessions → query user otomatis terbatas pada tenantnya.
/// </summary>
public sealed class ReportingSchemaSync(AnalyticsDataSource analytics, IMetadataProvider metadata, ReportingOptions options, ILogger<ReportingSchemaSync> log)
{
    private const long LockKey = 0x5265706F7274; // "Report"
    private int _subscribed;

    private static string Ident(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Kolom bertipe dari JSONB. Cast LANGSUNG (tanpa CASE/regex): data hanya ditulis lewat DocumentService yang
    /// menormalkan angka → angka JSON, centang → boolean JSON, tanggal → "yyyy-MM-dd". Pembungkus CASE + regex sebelumnya
    /// mahal per baris dan menyembunyikan filter dari planner — benchmark 1 juta dokumen: agregasi 1.970 ms → 860 ms.
    /// </summary>
    private static string Column(string source, CompiledField f)
    {
        var key = Lit(f.Name);
        return f.Type.ClrType == typeof(long) || f.Type.ClrType == typeof(decimal) ? $"NULLIF({source}->>{key}, '')::numeric"
            : f.Type.ClrType == typeof(bool) ? $"({source}->>{key})::boolean"
            : f.Def.Type == "Date" ? $"NULLIF({source}->>{key}, '')::date"
            : f.Def.Type == "Datetime" ? $"NULLIF({source}->>{key}, '')::timestamptz"
            : f.Def.Type == "Time" ? $"NULLIF({source}->>{key}, '')::time"
            : $"({source}->>{key})";
    }

    private const string TenantFilter =
        "d.tenant_id = (SELECT s.tenant_id FROM public.frogframe_reporting_sessions s WHERE s.pid = pg_backend_pid())";

    public async Task SyncAsync(CancellationToken ct = default)
    {
        var snap = metadata.Current;
        var ddl = new StringBuilder();
        var views = new List<string>();
        foreach (var meta in snap.DocTypes.Values.Where(m => !m.Def.IsChild))
        {
            var view = ReportingCatalog.SnakeCase(meta.Name);
            views.Add(view);
            var cols = new List<string> { "d.name", "d.owner", "d.docstatus", "d.workflow_state", "d.created", "d.modified" };
            cols.AddRange(meta.Fields.Where(ReportingCatalog.Exposed).Select(f => $"{Column("d.data", f)} AS {Ident(f.Name)}"));
            ddl.AppendLine($"CREATE VIEW reporting.{Ident(view)} AS SELECT {string.Join(", ", cols)} FROM public.frogframe_documents d " +
                           $"WHERE d.doctype = {Lit(meta.Name)} AND {TenantFilter};");
            ddl.AppendLine($"COMMENT ON VIEW reporting.{Ident(view)} IS {Lit($"{meta.Title} (DocType {meta.Name} v{meta.Version})")};");
            foreach (var t in meta.TableFields.Where(t => t.Def.PermLevel == 0))
            {
                if (!snap.TryGet(t.Def.ChildDocType!, out var child)) continue;
                var cview = $"{view}_{t.Name}";
                views.Add(cview);
                var key = Lit(t.Name);
                var ccols = new List<string> { "d.name AS parent", "r.ordinality::integer AS idx" };
                ccols.AddRange(child.Fields.Where(ReportingCatalog.Exposed).Select(f => $"{Column("r.value", f)} AS {Ident(f.Name)}"));
                ddl.AppendLine($"CREATE VIEW reporting.{Ident(cview)} AS SELECT {string.Join(", ", ccols)} FROM public.frogframe_documents d " +
                               $"CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(d.data->{key}) = 'array' THEN d.data->{key} ELSE '[]'::jsonb END) WITH ORDINALITY AS r(value, ordinality) " +
                               $"WHERE d.doctype = {Lit(meta.Name)} AND {TenantFilter};");
            }
        }

        await using var conn = await analytics.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, $"SELECT pg_advisory_xact_lock({LockKey})", ct);
        await Exec(conn, tx, "CREATE SCHEMA IF NOT EXISTS reporting", ct);
        // Di database aplikasi tabel ini dibuat migrasi 005; di instance analitik terpisah dibuat di sini.
        await Exec(conn, tx, "CREATE TABLE IF NOT EXISTS public.frogframe_reporting_sessions (pid integer PRIMARY KEY, tenant_id text NOT NULL, created_at timestamptz NOT NULL)", ct);
        var existing = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT viewname FROM pg_views WHERE schemaname = 'reporting'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) existing.Add(r.GetString(0));
        foreach (var v in existing) await Exec(conn, tx, $"DROP VIEW IF EXISTS reporting.{Ident(v)} CASCADE", ct);
        await Exec(conn, tx, ddl.ToString(), ct);

        bool roleExists;
        await using (var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)", conn, tx))
        {
            cmd.Parameters.AddWithValue(options.ReadOnlyRole);
            roleExists = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        if (roleExists)
        {
            await Exec(conn, tx, $"GRANT USAGE ON SCHEMA reporting TO {Ident(options.ReadOnlyRole)}", ct);
            await Exec(conn, tx, $"GRANT SELECT ON ALL TABLES IN SCHEMA reporting TO {Ident(options.ReadOnlyRole)}", ct);
        }
        else log.LogWarning("Role read-only '{Role}' tidak ditemukan; view reporting dibuat tanpa GRANT", options.ReadOnlyRole);
        await tx.CommitAsync(ct);
        log.LogInformation("Skema reporting disinkronkan: {Count} view", views.Count);

        // Sinkron ulang setiap kali metadata dipublikasikan (DocType/field baru langsung bisa di-query).
        if (Interlocked.Exchange(ref _subscribed, 1) == 0 && metadata is MetadataRegistry reg)
            reg.Published += snapshot => _ = Task.Run(async () =>
            {
                try { await SyncAsync(); } catch (Exception ex) { log.LogError(ex, "Sinkronisasi skema reporting gagal"); }
            });
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Menjalankan query widget: koneksi read-only yang diverifikasi, tenant dikunci per sesi, transaksi READ ONLY,
/// statement_timeout, dibungkus LIMIT, parameter dikirim native (bukan digabung ke teks SQL).
/// </summary>
public sealed class PostgresReportingQueryExecutor(AnalyticsDataSource analytics, ReadOnlyDataSource roDb, ReportingOptions options,
    ILogger<PostgresReportingQueryExecutor> log) : IReportingQueryExecutor
{
    private volatile string? _verified;

    public bool IsAvailable => true;

    /// <summary>Menolak connection string yang ternyata bukan read-only (bisa menulis, superuser, atau bisa membaca tabel inti).</summary>
    private async Task VerifyReadOnlyAsync(NpgsqlConnection ro, CancellationToken ct)
    {
        if (_verified == ro.ConnectionString) return;
        // Tabel dicari lewat katalog (OID), bukan nama "public.x": user read-only yang benar memang tidak punya USAGE
        // pada skema public, dan resolusi nama akan gagal. Hak tabel dicek langsung pada OID-nya.
        const string sql = """
            WITH t AS (
                SELECT c.relname, c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname IN ('frogframe_documents', 'frogframe_users', 'frogframe_reporting_sessions')
            )
            SELECT current_user,
                   r.rolsuper OR r.rolcreaterole OR r.rolcreatedb OR r.rolbypassrls,
                   EXISTS (SELECT 1 FROM information_schema.role_table_grants g
                           WHERE g.grantee = current_user AND g.privilege_type IN ('INSERT','UPDATE','DELETE','TRUNCATE')),
                   COALESCE((SELECT has_table_privilege(current_user, t.oid, 'SELECT') FROM t WHERE t.relname = 'frogframe_documents'), false),
                   COALESCE((SELECT has_table_privilege(current_user, t.oid, 'SELECT') FROM t WHERE t.relname = 'frogframe_users'), false),
                   COALESCE((SELECT has_table_privilege(current_user, t.oid, 'SELECT,INSERT,UPDATE,DELETE') FROM t WHERE t.relname = 'frogframe_reporting_sessions'), false),
                   COALESCE((SELECT has_schema_privilege(current_user, n.oid, 'CREATE') FROM pg_namespace n WHERE n.nspname = 'reporting'), false)
            FROM pg_roles r WHERE r.rolname = current_user
            """;
        await using var cmd = new NpgsqlCommand(sql, ro);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new ReportingUnavailableException("Role read-only tidak ditemukan");
        var problems = new List<string>();
        if (r.GetBoolean(1)) problems.Add("role punya hak superuser/create");
        if (r.GetBoolean(2)) problems.Add("role punya hak INSERT/UPDATE/DELETE");
        if (r.GetBoolean(3)) problems.Add("role bisa membaca tabel dokumen langsung (melewati isolasi tenant)");
        if (r.GetBoolean(4)) problems.Add("role bisa membaca tabel akun");
        if (r.GetBoolean(5)) problems.Add("role punya hak atas tabel sesi pelaporan");
        if (r.GetBoolean(6)) problems.Add("role bisa membuat objek di skema reporting");
        var user = r.GetString(0);
        if (problems.Count > 0)
        {
            log.LogError("Connection string read-only ditolak ({User}): {Problems}", user, string.Join("; ", problems));
            throw new ReportingUnavailableException($"Koneksi dashboard bukan read-only yang aman ({user}): {string.Join("; ", problems)}. Gunakan user khusus SELECT pada skema reporting.");
        }
        _verified = ro.ConnectionString;
    }

    public async Task<QueryResultSet> ExecuteAsync(string tenantId, string parameterizedSql, IReadOnlyList<SqlParam> parameters, int limit, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        NpgsqlConnection ro;
        try { ro = await roDb.Source.OpenConnectionAsync(ct); }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Tanpa ini exception mentah lolos ke halaman dashboard dan menghentikannya (kartu tanpa grafik).
            throw new ReportingUnavailableException(ConnectionHint(ex));
        }
        await using var _ro = ro;
        try { await VerifyReadOnlyAsync(ro, ct); }
        catch (PostgresException ex) { throw new ReportingUnavailableException($"Gagal memverifikasi koneksi read-only: {ex.MessageText}"); }
        var pid = ro.ProcessID;
        await using (var reg = analytics.Source.CreateCommand(
            "INSERT INTO frogframe_reporting_sessions (pid, tenant_id, created_at) VALUES ($1, $2, now()) ON CONFLICT (pid) DO UPDATE SET tenant_id = $2, created_at = now()"))
        {
            reg.Parameters.AddWithValue(pid); reg.Parameters.AddWithValue(tenantId);
            await reg.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var tx = await ro.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using (var set = new NpgsqlCommand($"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '{Math.Clamp(timeout is { } t ? (int)Math.Ceiling(t.TotalSeconds) : options.StatementTimeoutSeconds, 1, 600)}s'", ro, tx))
                await set.ExecuteNonQueryAsync(ct);

            // Pembungkus LIMIT: query yang salah tulis tidak bisa membawa jutaan baris ke memori aplikasi.
            await using var cmd = new NpgsqlCommand($"SELECT * FROM (\n{parameterizedSql}\n) AS __q LIMIT {limit + 1}", ro, tx);
            foreach (var p in parameters)
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = p.Value ?? DBNull.Value,
                    NpgsqlDbType = p.Type switch
                    {
                        ParamTypes.Number => NpgsqlDbType.Numeric,
                        ParamTypes.Date => NpgsqlDbType.Date,
                        ParamTypes.Boolean => NpgsqlDbType.Boolean,
                        ParamTypes.Text => NpgsqlDbType.Text,
                        _ => throw new QueryRejectedException("Tipe parameter tidak didukung"),
                    },
                });

            var rows = new List<object?[]>();
            var truncated = false;
            List<ResultColumn> columns;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                columns = Enumerable.Range(0, r.FieldCount).Select(i => new ResultColumn(r.GetName(i), Kind(r.GetFieldType(i)))).ToList();
                while (await r.ReadAsync(ct))
                {
                    if (rows.Count == limit) { truncated = true; break; }
                    var row = new object?[r.FieldCount];
                    for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : Normalize(r.GetValue(i));
                    rows.Add(row);
                }
            }
            await tx.RollbackAsync(ct);
            return new QueryResultSet(columns, rows, truncated, sw.ElapsedMilliseconds);
        }
        catch (NpgsqlException ex) when (ex is not PostgresException)
        {
            throw new ReportingUnavailableException($"Koneksi dashboard terputus: {ex.Message}");
        }
        catch (PostgresException ex)
        {
            throw new QueryRejectedException(ex.SqlState switch
            {
                PostgresErrorCodes.QueryCanceled => $"Query melebihi batas waktu {(timeout is { } t ? (int)Math.Ceiling(t.TotalSeconds) : options.StatementTimeoutSeconds)} detik",
                PostgresErrorCodes.InsufficientPrivilege => $"Akses ditolak: {ex.MessageText}. Query hanya boleh membaca view di skema reporting.",
                PostgresErrorCodes.ReadOnlySqlTransaction => "Query mencoba mengubah data; hanya SELECT yang diizinkan.",
                _ => ex.MessageText,
            });
        }
        finally
        {
            await using var del = analytics.Source.CreateCommand("DELETE FROM frogframe_reporting_sessions WHERE pid = $1");
            del.Parameters.AddWithValue(pid);
            await del.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>Pesan yang bisa ditindaklanjuti untuk kegagalan koneksi read-only (bukan exception mentah).</summary>
    private string ConnectionHint(Exception ex)
    {
        var csb = new NpgsqlConnectionStringBuilder(roDb.Source.ConnectionString);
        var who = $"user '{csb.Username}' ke database '{csb.Database}' di {csb.Host}";
        return ex is PostgresException pg ? pg.SqlState switch
        {
            "28P01" or "28000" => $"Login dashboard gagal untuk {who}. Pastikan role read-only sudah dibuat dengan deploy/sql/reporting-readonly-user.sql " +
                                  "dan password-nya sama dengan ConnectionStrings:FrogFrameReadOnly. (Docker compose hanya menjalankan script itu saat volume database baru.)",
            "3D000" => $"Database untuk dashboard tidak ditemukan ({who}). Periksa ConnectionStrings:FrogFrameReadOnly.",
            "42501" => $"{who} tidak punya izin CONNECT. Jalankan deploy/sql/reporting-readonly-user.sql (GRANT CONNECT).",
            _ => $"Tidak bisa terhubung sebagai {who}: {pg.MessageText}",
        } : $"Tidak bisa terhubung ke database dashboard sebagai {who}: {ex.Message}";
    }

    private static string Kind(Type t) =>
        t == typeof(decimal) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(double) || t == typeof(float) ? "number"
        : t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(DateTimeOffset) ? "date"
        : t == typeof(bool) ? "boolean" : "text";

    private static object? Normalize(object v) => v switch
    {
        int i => (decimal)i, long l => (decimal)l, short s => (decimal)s, double d => (decimal)d, float f => (decimal)f,
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        decimal or string or bool => v,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };
}

public sealed class PostgresWidgetStore(NpgsqlDataSource db) : IWidgetStore
{
    private static WidgetDef Read(NpgsqlDataReader r) => new()
    {
        TenantId = r.GetString(0), Id = r.GetString(1), Name = r.GetString(2), Description = r.IsDBNull(3) ? null : r.GetString(3),
        Config = JsonSerializer.Deserialize<WidgetConfig>(r.GetString(4), DashboardJson.Options) ?? new(),
        CreatedBy = r.GetString(5), CreatedAt = r.GetFieldValue<DateTimeOffset>(6), UpdatedAt = r.GetFieldValue<DateTimeOffset>(7), Version = r.GetInt32(8),
    };
    private const string Cols = "tenant_id, id, name, description, config::text, created_by, created_at, updated_at, version";

    public async Task<IReadOnlyList<WidgetDef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_dashboard_widgets WHERE tenant_id = $1 ORDER BY name");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<WidgetDef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<WidgetDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_dashboard_widgets WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task SaveAsync(WidgetDef w, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO frogframe_dashboard_widgets (tenant_id, id, name, description, config, created_by, created_at, updated_at, version) " +
            "VALUES ($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9) ON CONFLICT (tenant_id, id) DO UPDATE SET name=$3, description=$4, config=$5::jsonb, updated_at=$8, version=$9");
        cmd.Parameters.AddWithValue(w.TenantId); cmd.Parameters.AddWithValue(w.Id); cmd.Parameters.AddWithValue(w.Name);
        cmd.Parameters.AddWithValue((object?)w.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(w.Config, DashboardJson.Options));
        cmd.Parameters.AddWithValue(w.CreatedBy); cmd.Parameters.AddWithValue(w.CreatedAt); cmd.Parameters.AddWithValue(w.UpdatedAt); cmd.Parameters.AddWithValue(w.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_dashboard_widgets WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DashboardLayout> GetLayoutAsync(string tenantId, CancellationToken ct = default)
    {
        var d = await GetDashboardAsync(tenantId, DashboardDef.DefaultDashboardId, ct);
        return d?.Layout ?? new();
    }

    public async Task SaveLayoutAsync(string tenantId, DashboardLayout layout, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await GetDashboardAsync(tenantId, DashboardDef.DefaultDashboardId, ct) ?? new DashboardDef
        {
            Id = DashboardDef.DefaultDashboardId,
            TenantId = tenantId,
            Name = "Main Dashboard",
            Description = "Default shared tenant dashboard",
            OwnerId = "admin",
            IsShared = true,
            IsDefault = true,
            CreatedAt = now
        };
        existing.Layout = layout;
        existing.UpdatedAt = now;
        await SaveDashboardAsync(existing, ct);
    }

    private const string DashCols = "tenant_id, id, name, description, owner_id, is_shared, is_default, roles::text, layout::text, created_at, updated_at";

    private static DashboardDef ReadDashboard(NpgsqlDataReader r) => new()
    {
        TenantId = r.GetString(0),
        Id = r.GetString(1),
        Name = r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        OwnerId = r.GetString(4),
        IsShared = r.GetBoolean(5),
        IsDefault = r.GetBoolean(6),
        Roles = JsonSerializer.Deserialize<List<string>>(r.GetString(7), DashboardJson.Options) ?? [],
        Layout = JsonSerializer.Deserialize<DashboardLayout>(r.GetString(8), DashboardJson.Options) ?? new(),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(10),
    };

    public async Task<IReadOnlyList<DashboardDef>> ListDashboardsAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {DashCols} FROM frogframe_dashboards WHERE tenant_id = $1 ORDER BY is_default DESC, is_shared DESC, name");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<DashboardDef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDashboard(r));
        return list;
    }

    public async Task<DashboardDef?> GetDashboardAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {DashCols} FROM frogframe_dashboards WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDashboard(r) : null;
    }

    public async Task SaveDashboardAsync(DashboardDef d, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO frogframe_dashboards (tenant_id, id, name, description, owner_id, is_shared, is_default, roles, layout, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9::jsonb, $10, $11) " +
            "ON CONFLICT (tenant_id, id) DO UPDATE SET " +
            "name = $3, description = $4, owner_id = $5, is_shared = $6, is_default = $7, roles = $8::jsonb, layout = $9::jsonb, updated_at = $11");
        cmd.Parameters.AddWithValue(d.TenantId);
        cmd.Parameters.AddWithValue(d.Id);
        cmd.Parameters.AddWithValue(d.Name);
        cmd.Parameters.AddWithValue((object?)d.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(d.OwnerId);
        cmd.Parameters.AddWithValue(d.IsShared);
        cmd.Parameters.AddWithValue(d.IsDefault);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(d.Roles, DashboardJson.Options));
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(d.Layout, DashboardJson.Options));
        cmd.Parameters.AddWithValue(d.CreatedAt);
        cmd.Parameters.AddWithValue(d.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteDashboardAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_dashboards WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class ReportingServiceCollectionExtensions
{
    /// <summary>
    /// Mengaktifkan dashboard SQL. Wajib bersama AddFrogFrameworkPostgreSql. <paramref name="readOnlyConnectionString"/>
    /// harus user database yang hanya punya SELECT pada skema reporting (lihat deploy/sql/reporting-readonly-user.sql).
    /// </summary>
    public static IServiceCollection AddFrogFrameworkReporting(this IServiceCollection services, string readOnlyConnectionString, Action<ReportingOptions>? configure = null)
    {
        var options = new ReportingOptions { ReadOnlyConnectionString = readOnlyConnectionString };
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(_ => new ReadOnlyDataSource(NpgsqlDataSource.Create(readOnlyConnectionString)));
        services.AddSingleton(sp => new AnalyticsDataSource(string.IsNullOrWhiteSpace(options.AnalyticsConnectionString)
            // Tanpa instance analitik terpisah: pakai koneksi PEMILIK skema, karena sinkronisasi membuat skema & view (DDL).
            ? sp.GetRequiredService<SchemaDataSource>().Source
            : NpgsqlDataSource.Create(options.AnalyticsConnectionString), owns: !string.IsNullOrWhiteSpace(options.AnalyticsConnectionString)));
        services.AddSingleton<ReportingSchemaSync>();
        services.Replace(ServiceDescriptor.Singleton<IReportingQueryExecutor, PostgresReportingQueryExecutor>());
        services.Replace(ServiceDescriptor.Singleton<IWidgetStore, PostgresWidgetStore>());
        services.Replace(ServiceDescriptor.Singleton<Framework.Reports.IReportTemplateStore, PostgresReportTemplateStore>());
        return services;
    }

    /// <summary>
    /// Memeriksa koneksi read-only dashboard saat startup dan menulis PERINGATAN yang jelas bila bermasalah
    /// (mis. role belum dibuat). Tidak menghentikan aplikasi: fitur lain tetap berjalan.
    /// </summary>
    public static async Task<string?> CheckReportingConnectionAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        if (services.GetService<IReportingQueryExecutor>() is not PostgresReportingQueryExecutor exec) return null;
        try
        {
            await exec.ExecuteAsync("__startup_check__", "SELECT 1", [], 1, ct);
            return null;
        }
        catch (Exception ex) when (ex is ReportingUnavailableException or QueryRejectedException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Framework.Dashboards")
                .LogWarning("Dashboard SQL tidak bisa dipakai: {Problem}", ex.Message);
            return ex.Message;
        }
    }

    public static Task SyncReportingSchemaAsync(this IServiceProvider services, CancellationToken ct = default) =>
        services.GetService<ReportingSchemaSync>()?.SyncAsync(ct) ?? Task.CompletedTask;
}
