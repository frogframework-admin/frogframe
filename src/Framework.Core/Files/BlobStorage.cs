using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Files;

// ================================================================================================================
//  Object storage for file contents (S3-compatible: SeaweedFS, MinIO, Ceph RGW, AWS S3, …).
//
//  The file and attachment stores keep their records (owner, tenant, document link, size, hash) in the database:
//  permission checks and orphan cleanup work as before. With Storage:Provider = S3 only the CONTENT goes to the
//  bucket; the database row then holds an empty content. Rows written before the switch keep their content in the
//  database and stay readable, so the switch needs no downtime (and Storage:MigrateExisting moves them over).
// ================================================================================================================

public interface IBlobStorage
{
    /// <summary>Short name for logs and health output, e.g. "s3://frogframe".</summary>
    string Name { get; }
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default);
    /// <summary>Null when the object does not exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
    /// <summary>Deleting a missing object is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Object keys: one prefix per tenant, so a bucket can be inspected, backed up or purged per tenant.</summary>
public static class BlobKeys
{
    public static string File(string tenantId, string id) => $"{Uri.EscapeDataString(tenantId)}/files/{Uri.EscapeDataString(id)}";
    public static string Attachment(string tenantId, string id) => $"{Uri.EscapeDataString(tenantId)}/attachments/{Uri.EscapeDataString(id)}";
}

/// <summary>For tests and local development without an object store.</summary>
public sealed class InMemoryBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    public string Name => "memory";
    public IReadOnlyCollection<string> Keys => _objects.Keys.ToList();
    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default) { _objects[key] = content.ToArray(); return Task.CompletedTask; }
    public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_objects.TryGetValue(key, out var b) ? b.ToArray() : null);
    public Task DeleteAsync(string key, CancellationToken ct = default) { _objects.TryRemove(key, out _); return Task.CompletedTask; }
}

/// <summary>
/// File store whose contents live in object storage. Records stay in <paramref name="inner"/> (database);
/// rows that still hold their content (written before the switch) are served from the database.
/// </summary>
public sealed class BlobBackedFileStore(IFileStore inner, IBlobStorage blobs, ILogger<BlobBackedFileStore>? logger = null) : IFileStore
{
    private readonly ILogger _log = logger ?? NullLogger<BlobBackedFileStore>.Instance;

    public async Task SaveAsync(FileRecord record, byte[] content, CancellationToken ct = default)
    {
        var key = BlobKeys.File(record.TenantId, record.Id);
        await blobs.PutAsync(key, content, record.ContentType, ct);
        try
        {
            await inner.SaveAsync(record, [], ct);
        }
        catch
        {
            await BlobCleanup.TryDelete(blobs, key, _log);
            throw;
        }
    }

    public Task<FileRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default) => inner.GetInfoAsync(tenantId, id, ct);

    public async Task<(FileRecord Record, byte[] Content)?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        if (await inner.GetAsync(tenantId, id, ct) is not { } found) return null;
        if (found.Content.Length > 0 || found.Record.Size == 0) return found; // still in the database
        var content = await blobs.GetAsync(BlobKeys.File(tenantId, id), ct);
        if (content is null)
        {
            _log.LogWarning("Content of file {Id} (tenant {Tenant}) is missing in {Storage}", id, tenantId, blobs.Name);
            return null;
        }
        return (found.Record, content);
    }

    public Task AttachAsync(string tenantId, string id, string docType, string docName, string field, CancellationToken ct = default) =>
        inner.AttachAsync(tenantId, id, docType, docName, field, ct);

    public async Task<int> PruneOrphansAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        (await PruneOrphansReturningAsync(cutoff, ct)).Count;

    public async Task<IReadOnlyList<FileRecord>> PruneOrphansReturningAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var removed = await inner.PruneOrphansReturningAsync(cutoff, ct);
        foreach (var f in removed) await BlobCleanup.TryDelete(blobs, BlobKeys.File(f.TenantId, f.Id), _log);
        return removed;
    }
}

/// <summary>Attachment store whose contents live in object storage (see <see cref="BlobBackedFileStore"/>).</summary>
public sealed class BlobBackedAttachmentStore(IAttachmentStore inner, IBlobStorage blobs, ILogger<BlobBackedAttachmentStore>? logger = null) : IAttachmentStore
{
    private readonly ILogger _log = logger ?? NullLogger<BlobBackedAttachmentStore>.Instance;

    public async Task SaveAsync(AttachmentRecord record, byte[] content, CancellationToken ct = default)
    {
        var key = BlobKeys.Attachment(record.TenantId, record.Id);
        await blobs.PutAsync(key, content, record.ContentType, ct);
        try
        {
            await inner.SaveAsync(record, [], ct);
        }
        catch
        {
            await BlobCleanup.TryDelete(blobs, key, _log);
            throw;
        }
    }

    public Task<AttachmentRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default) => inner.GetInfoAsync(tenantId, id, ct);

    public async Task<(AttachmentRecord Record, byte[] Content)?> GetContentAsync(string tenantId, string id, CancellationToken ct = default)
    {
        if (await inner.GetContentAsync(tenantId, id, ct) is not { } found) return null;
        if (found.Content.Length > 0 || found.Record.Size == 0) return found;
        var content = await blobs.GetAsync(BlobKeys.Attachment(tenantId, id), ct);
        if (content is null)
        {
            _log.LogWarning("Content of attachment {Id} (tenant {Tenant}) is missing in {Storage}", id, tenantId, blobs.Name);
            return null;
        }
        return (found.Record, content);
    }

    public Task<IReadOnlyList<AttachmentRecord>> ListAsync(string tenantId, string docType, string docName, CancellationToken ct = default) =>
        inner.ListAsync(tenantId, docType, docName, ct);

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await inner.DeleteAsync(tenantId, id, ct);
        await BlobCleanup.TryDelete(blobs, BlobKeys.Attachment(tenantId, id), _log);
    }
}

internal static class BlobCleanup
{
    /// <summary>Best effort: a leftover object is harmless (no record points to it), a failed request must not hide the real error.</summary>
    public static async Task TryDelete(IBlobStorage blobs, string key, ILogger log)
    {
        try { await blobs.DeleteAsync(key, CancellationToken.None); }
        catch (Exception ex) { log.LogWarning(ex, "Object {Key} could not be deleted from {Storage}", key, blobs.Name); }
    }
}
