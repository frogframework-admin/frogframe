using System.Collections.Concurrent;
using Framework.Documents;

namespace Framework.Persistence;

/// <summary>
/// Read cache for one save operation (insert, update, action, delete). While a document is being saved, validation,
/// "fetch from" fields and hooks often read the same master documents several times (the requester's Employee record
/// four times for one Purchase Request). Inside a scope, a store returns the first read of a document again instead
/// of asking the database, each caller getting its own copy.
/// <para>
/// Scopes nest: an operation started inside another one (a hook saving another document) uses the outer scope.
/// Documents written through <see cref="IDocumentStore.CommitAsync"/> are removed from the cache, so a document is
/// never read back older than what this operation saved. Outside a scope nothing is cached.
/// </para>
/// </summary>
public static class DocumentReadScope
{
    private sealed class Cache
    {
        public readonly ConcurrentDictionary<(string Tenant, string DocType, string Name), Document?> Docs = new();
    }

    private static readonly AsyncLocal<Cache?> Current = new();

    /// <summary>True inside a scope.</summary>
    public static bool Active => Current.Value is not null;

    /// <summary>Starts a scope, or joins the scope already active (then disposing does nothing).</summary>
    public static IDisposable Begin()
    {
        if (Current.Value is not null) return Joined.Instance;
        Current.Value = new Cache();
        return new Owner();
    }

    /// <summary>A copy of the cached read of a document (null = cached "not found"); false when it was not read yet.</summary>
    public static bool TryGet(string tenantId, string docType, string name, out Document? doc)
    {
        doc = null;
        if (Current.Value is not { } cache || !cache.Docs.TryGetValue((tenantId, docType, name), out var cached)) return false;
        doc = cached?.Clone();
        return true;
    }

    /// <summary>Remembers a read (a copy, so later changes by the caller do not leak into the cache).</summary>
    public static void Remember(string tenantId, string docType, string name, Document? doc)
    {
        if (Current.Value is { } cache) cache.Docs[(tenantId, docType, name)] = doc?.Clone();
    }

    /// <summary>Forgets the documents written or deleted by a unit of work.</summary>
    public static void Forget(UnitOfWork uow)
    {
        if (Current.Value is not { } cache) return;
        foreach (var (doc, _) in uow.Upserts) cache.Docs.TryRemove((doc.TenantId, doc.DocType, doc.Name), out _);
        foreach (var (tenant, docType, name, _) in uow.Deletes) cache.Docs.TryRemove((tenant, docType, name), out _);
    }

    private sealed class Owner : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }

    private sealed class Joined : IDisposable
    {
        public static readonly Joined Instance = new();
        public void Dispose() { }
    }
}
