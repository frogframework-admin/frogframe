using System.Collections.Concurrent;

namespace Framework.Sharing;

/// <summary>
/// One document shared with one user (row sharing). Read is always included; <see cref="CanWrite"/> lets the user
/// edit the document and <see cref="CanShare"/> lets them share it further (never with more rights than they have).
/// A share widens the user's row scope only: the user still needs the DocType permission (read / write) from a
/// role, as in record-level sharing elsewhere. Delete, submit and workflow actions are not granted by a share.
/// </summary>
public sealed record DocShare(
    string TenantId,
    string DocType,
    string DocName,
    string UserId,
    bool CanWrite,
    bool CanShare,
    string SharedBy,
    DateTimeOffset SharedAt);

/// <summary>Storage of document shares (table <c>frogframe_doc_shares</c> in PostgreSQL). User ids compare case-insensitively.</summary>
public interface IDocShareStore
{
    Task<IReadOnlyList<DocShare>> ForDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
    Task<DocShare?> FindAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default);
    /// <summary>Names of the documents of a DocType shared with the user (write shares only when <paramref name="writeOnly"/>).</summary>
    Task<IReadOnlyList<string>> SharedNamesAsync(string tenantId, string docType, string userId, bool writeOnly, int max, CancellationToken ct = default);
    /// <summary>Creates the share or replaces the rights of an existing one (same document and user).</summary>
    Task UpsertAsync(DocShare share, CancellationToken ct = default);
    Task<bool> RemoveAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default);
    Task<int> RemoveDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
}

public static class DocShareLimits
{
    /// <summary>Shared documents per user and DocType that widen a list query (the rest is still reachable by link).</summary>
    public const int MaxSharedNamesPerQuery = 5_000;
    /// <summary>Users one document can be shared with.</summary>
    public const int MaxSharesPerDocument = 200;
}

public sealed class InMemoryDocShareStore : IDocShareStore
{
    private readonly ConcurrentDictionary<(string Tenant, string DocType, string DocName, string User), DocShare> shares = new();

    private static (string, string, string, string) Key(string tenantId, string docType, string docName, string userId) =>
        (tenantId, docType, docName, userId.ToLowerInvariant());

    public Task<IReadOnlyList<DocShare>> ForDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocShare>>(shares
            .Where(kv => kv.Key.Tenant == tenantId && kv.Key.DocType == docType && kv.Key.DocName == docName)
            .Select(kv => kv.Value).OrderBy(s => s.SharedAt).ThenBy(s => s.UserId, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<DocShare?> FindAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default) =>
        Task.FromResult(shares.GetValueOrDefault(Key(tenantId, docType, docName, userId)));

    public Task<IReadOnlyList<string>> SharedNamesAsync(string tenantId, string docType, string userId, bool writeOnly, int max, CancellationToken ct = default)
    {
        var user = userId.ToLowerInvariant();
        return Task.FromResult<IReadOnlyList<string>>(shares
            .Where(kv => kv.Key.Tenant == tenantId && kv.Key.DocType == docType && kv.Key.User == user && (!writeOnly || kv.Value.CanWrite))
            .Select(kv => kv.Key.DocName).OrderBy(n => n, StringComparer.Ordinal).Take(max).ToList());
    }

    public Task UpsertAsync(DocShare share, CancellationToken ct = default)
    {
        shares.AddOrUpdate(Key(share.TenantId, share.DocType, share.DocName, share.UserId), share,
            (_, existing) => share with { SharedAt = existing.SharedAt });
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default) =>
        Task.FromResult(shares.TryRemove(Key(tenantId, docType, docName, userId), out _));

    public Task<int> RemoveDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        var keys = shares.Keys.Where(k => k.Tenant == tenantId && k.DocType == docType && k.DocName == docName).ToList();
        return Task.FromResult(keys.Count(k => shares.TryRemove(k, out _)));
    }
}
