using System.Collections.Concurrent;
using System.Globalization;
using Framework.Documents;
using Framework.Expressions;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Persistence;

// Kontrak store dibuat terstruktur (bukan string SQL) supaya implementasi PostgreSQL/SQL Server
// dapat menerjemahkannya menjadi query berparameter dari field/operator yang sudah di-allowlist.

public static class FilterOps
{
    public static readonly IReadOnlySet<string> All = new HashSet<string> { "=", "!=", ">", ">=", "<", "<=", "like", "in" };
}

/// <summary>Pola nama dokumen, mis. "PR-{YYYY}-{#####}" → prefix "PR-2026-", lebar 5, suffix "".</summary>
public static class NamingPattern
{
    public static (string Prefix, int Width, string Suffix) Parse(string pattern, string docType, DateTimeOffset now)
    {
        var hashStart = pattern.IndexOf("{#", StringComparison.Ordinal);
        var hashEnd = pattern.IndexOf('}', hashStart);
        var prefix = pattern[..hashStart].Replace("{YYYY}", now.ToString("yyyy")).Replace("{MM}", now.ToString("MM")).Replace("{doctype}", docType);
        return (prefix, hashEnd - hashStart - 1, pattern[(hashEnd + 1)..]);
    }
}

public sealed record FilterClause(string Field, string Op, object? Value);

public sealed record StoreQuery(
    string TenantId,
    string DocType,
    RowScope Scope,
    IReadOnlyList<FilterClause> Filters,
    string? Search,
    IReadOnlyList<string> SearchFields,
    IReadOnlyList<SortDef> Sort,
    int Skip,
    int Take);

/// <param name="TotalCapped">True bila jumlah sebenarnya melebihi <see cref="QueryLimits.CountCap"/> (Total = batas).</param>
public sealed record QueryResult(IReadOnlyList<Document> Items, int Total, bool TotalCapped = false);

public static class QueryLimits
{
    /// <summary>
    /// Menghitung total persis pada jutaan baris jauh lebih mahal daripada mengambil satu halaman. Di atas batas ini
    /// daftar menampilkan "10.000+" dan pengguna diarahkan memakai filter/pencarian.
    /// </summary>
    public const int CountCap = 10_000;
}

public sealed class UnitOfWork
{
    public List<(Document Doc, int? ExpectedVersion)> Upserts { get; } = [];
    public List<(string TenantId, string DocType, string Name, int ExpectedVersion)> Deletes { get; } = [];
    public List<AuditEntry> Audits { get; } = [];
    public List<OutboxMessage> Outbox { get; } = [];
}

public interface IDocumentStore
{
    Task<Document?> GetAsync(string tenantId, string docType, string name, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(StoreQuery query, CancellationToken ct = default);
    Task<string> NextNameAsync(string tenantId, string docType, string pattern, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Menyimpan dokumen, audit, dan outbox secara atomik dengan pemeriksaan versi.</summary>
    Task CommitAsync(UnitOfWork uow, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string tenantId, string docType, string name, CancellationToken ct = default);
}

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> FetchDueAsync(DateTimeOffset now, int max, CancellationToken ct = default);
    Task UpdateAsync(OutboxMessage message, CancellationToken ct = default);
    IReadOnlyList<OutboxMessage> Snapshot();
    /// <summary>Menghapus pesan yang SUDAH diproses sebelum waktu tertentu (dead-letter tidak dihapus). Mengembalikan jumlahnya.</summary>
    Task<int> PruneAsync(DateTimeOffset processedBefore, int max, CancellationToken ct = default);
}

public interface INotificationStore
{
    /// <summary>Idempoten berdasarkan Id.</summary>
    void Add(Notification n);
    IReadOnlyList<Notification> ForUser(string tenantId, string userId);
    void MarkRead(string tenantId, string userId, Guid id);
}

/// <summary>
/// Store in-memory untuk prototipe (strategi "tabel generik + payload", RFC §7A).
/// Satu lock global mensimulasikan transaksi serializable. Ganti dengan implementasi EF Core/Dapper untuk produksi.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore, IOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string DocType, string Name), Document> _docs = new();
    private readonly Dictionary<(string Tenant, string DocType, string Prefix), int> _series = new();
    private readonly List<AuditEntry> _audit = [];
    private readonly List<OutboxMessage> _outbox = [];
    private long _auditSeq;

    public Task<Document?> GetAsync(string tenantId, string docType, string name, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_docs.TryGetValue((tenantId, docType, name), out var d) ? d.Clone() : null);
    }

    public Task<QueryResult> QueryAsync(StoreQuery q, CancellationToken ct = default)
    {
        List<Document> rows;
        lock (_gate)
            rows = _docs.Values.Where(d => d.TenantId == q.TenantId && d.DocType == q.DocType).Select(d => d.Clone()).ToList();

        IEnumerable<Document> result = rows.Where(d => MatchScope(d, q.Scope));
        foreach (var f in q.Filters) result = result.Where(d => Match(d.Get(f.Field), f.Op, f.Value));
        if (!string.IsNullOrWhiteSpace(q.Search))
            result = result.Where(d => q.SearchFields.Any(sf =>
                Convert.ToString(d.Get(sf), CultureInfo.InvariantCulture)?.Contains(q.Search, StringComparison.OrdinalIgnoreCase) == true));

        IOrderedEnumerable<Document>? ordered = null;
        foreach (var s in q.Sort)
        {
            Func<Document, object?> key = d => SortKey(d.Get(s.Field));
            var desc = s.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
            ordered = ordered is null
                ? (desc ? result.OrderByDescending(key, Comparer.Instance) : result.OrderBy(key, Comparer.Instance))
                : (desc ? ordered.ThenByDescending(key, Comparer.Instance) : ordered.ThenBy(key, Comparer.Instance));
        }
        var list = (ordered ?? result).ToList();
        return Task.FromResult(new QueryResult(list.Skip(q.Skip).Take(q.Take).ToList(), list.Count));
    }

    private static object? SortKey(object? v) => Values.IsNumber(v) ? Values.ToDecimal(v) : v;

    private sealed class Comparer : IComparer<object?>
    {
        public static readonly Comparer Instance = new();
        public int Compare(object? x, object? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;
            if (x is decimal a && y is decimal b) return a.CompareTo(b);
            return string.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool MatchScope(Document d, RowScope scope) => scope switch
    {
        RowScope.AllRows => true,
        RowScope.AnyOf any => any.Conditions.Any(c => Convert.ToString(d.Get(c.Field), CultureInfo.InvariantCulture) == c.Value),
        _ => false
    };

    private static bool Match(object? actual, string op, object? expected)
    {
        try
        {
            return op switch
            {
                "=" => Values.AreEqual(actual, expected),
                "!=" => !Values.AreEqual(actual, expected),
                ">" => actual is not null && Values.Compare(actual, expected) > 0,
                ">=" => actual is not null && Values.Compare(actual, expected) >= 0,
                "<" => actual is not null && Values.Compare(actual, expected) < 0,
                "<=" => actual is not null && Values.Compare(actual, expected) <= 0,
                "like" => Convert.ToString(actual, CultureInfo.InvariantCulture)?.Contains(Convert.ToString(expected, CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase) == true,
                "in" => expected is IEnumerable<object?> list && list.Any(x => Values.AreEqual(actual, x)),
                _ => false
            };
        }
        catch (ExpressionException) { return false; }
    }

    public Task<string> NextNameAsync(string tenantId, string docType, string pattern, DateTimeOffset now, CancellationToken ct = default)
    {
        var (prefix, width, suffix) = NamingPattern.Parse(pattern, docType, now);
        lock (_gate)
        {
            var key = (tenantId, docType, prefix);
            var next = _series.TryGetValue(key, out var n) ? n + 1 : 1;
            _series[key] = next;
            return Task.FromResult(prefix + next.ToString(new string('0', width)) + suffix);
        }
    }

    public Task CommitAsync(UnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Fase 1: periksa semua prasyarat. Tidak ada perubahan bila satu pun gagal.
            foreach (var (doc, expected) in uow.Upserts)
            {
                var exists = _docs.TryGetValue((doc.TenantId, doc.DocType, doc.Name), out var current);
                if (expected is null && exists) throw new InvalidStateException($"Nama {doc.Name} sudah dipakai");
                if (expected is not null && (!exists || current!.Version != expected))
                    throw new ConcurrencyException(doc.Name, expected.Value, current?.Version ?? 0);
            }
            foreach (var (tenant, docType, name, expected) in uow.Deletes)
            {
                if (!_docs.TryGetValue((tenant, docType, name), out var current) || current.Version != expected)
                    throw new ConcurrencyException(name, expected, current?.Version ?? 0);
            }

            // Fase 2: terapkan.
            foreach (var (doc, _) in uow.Upserts) _docs[(doc.TenantId, doc.DocType, doc.Name)] = doc.Clone();
            foreach (var (tenant, docType, name, _) in uow.Deletes) _docs.Remove((tenant, docType, name));
            foreach (var a in uow.Audits) _audit.Add(a with { Id = ++_auditSeq });
            _outbox.AddRange(uow.Outbox);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string tenantId, string docType, string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<AuditEntry>>(_audit
                .Where(a => a.TenantId == tenantId && a.DocType == docType && a.DocName == name)
                .OrderByDescending(a => a.Id).ToList());
    }

    public Task<IReadOnlyList<OutboxMessage>> FetchDueAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(_outbox
                .Where(m => m.ProcessedAt is null && !m.DeadLettered && (m.NextAttemptAt is null || m.NextAttemptAt <= now))
                .OrderBy(m => m.CreatedAt).Take(max).ToList());
    }

    public Task UpdateAsync(OutboxMessage message, CancellationToken ct = default) => Task.CompletedTask; // objek sudah tersimpan by-reference

    public IReadOnlyList<OutboxMessage> Snapshot() { lock (_gate) return _outbox.ToList(); }

    public Task<int> PruneAsync(DateTimeOffset processedBefore, int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var old = _outbox.Where(m => m.ProcessedAt is { } p && p < processedBefore && !m.DeadLettered).Take(max).ToHashSet();
            _outbox.RemoveAll(old.Contains);
            return Task.FromResult(old.Count);
        }
    }
}

public sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly ConcurrentDictionary<Guid, Notification> _items = new();

    public void Add(Notification n) => _items.TryAdd(n.Id, n);

    public IReadOnlyList<Notification> ForUser(string tenantId, string userId) =>
        _items.Values.Where(n => n.TenantId == tenantId && n.UserId == userId).OrderByDescending(n => n.At).ToList();

    public void MarkRead(string tenantId, string userId, Guid id)
    {
        if (_items.TryGetValue(id, out var n) && n.TenantId == tenantId && n.UserId == userId)
            _items[id] = n with { Read = true };
    }
}
