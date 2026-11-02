using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Files;

public sealed record AttachmentRecord
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string DocType { get; init; }
    public required string DocName { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required string UploadedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsPrivate { get; init; } = true;
}

public interface IAttachmentStore
{
    Task SaveAsync(AttachmentRecord record, byte[] content, CancellationToken ct = default);
    Task<AttachmentRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default);
    Task<(AttachmentRecord Record, byte[] Content)?> GetContentAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<AttachmentRecord>> ListAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class InMemoryAttachmentStore : IAttachmentStore
{
    private readonly ConcurrentDictionary<string, (AttachmentRecord Record, byte[] Content)> _attachments = new();

    public Task SaveAsync(AttachmentRecord record, byte[] content, CancellationToken ct = default)
    {
        _attachments[record.Id] = (record, content);
        return Task.CompletedTask;
    }

    public Task<AttachmentRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_attachments.TryGetValue(id, out var item) && item.Record.TenantId == tenantId ? item.Record : null);

    public Task<(AttachmentRecord Record, byte[] Content)?> GetContentAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult<(AttachmentRecord, byte[])?>(_attachments.TryGetValue(id, out var item) && item.Record.TenantId == tenantId ? item : null);

    public Task<IReadOnlyList<AttachmentRecord>> ListAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        var list = _attachments.Values
            .Where(x => x.Record.TenantId == tenantId && x.Record.DocType == docType && x.Record.DocName == docName)
            .Select(x => x.Record)
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<AttachmentRecord>>(list);
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        if (_attachments.TryGetValue(id, out var item) && item.Record.TenantId == tenantId)
            _attachments.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

public sealed class AttachmentService(
    IAttachmentStore store,
    DocumentService docService,
    IMetadataProvider metadata,
    IPermissionService permissions,
    TimeProvider clock)
{
    public const long MaxAttachmentBytes = 10 * 1024 * 1024; // 10 MB

    public static string? SniffContentType(ReadOnlySpan<byte> bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        // 1. PDF
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8))
            return "application/pdf";

        // 2. Images
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";

        // 3. Zip-based Office formats (.docx, .xlsx)
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes.ToArray()), ZipArchiveMode.Read);
                if (zip.GetEntry("word/document.xml") is not null)
                    return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                if (zip.GetEntry("xl/workbook.xml") is not null)
                    return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            }
            catch
            {
                // Not a valid zip
            }
        }

        // 4. Plain text / CSV (must not contain null bytes and must have corresponding extension)
        if (ext is ".csv" or ".txt" or ".log")
        {
            if (!bytes.Contains((byte)0))
                return ext == ".csv" ? "text/csv" : "text/plain";
        }

        return null;
    }

    public async Task<AttachmentRecord> UploadAsync(
        UserContext user,
        string docType,
        string docName,
        string fileName,
        Stream content,
        bool isPrivate = true,
        CancellationToken ct = default)
    {
        var meta = metadata.Current.DocTypes.TryGetValue(docType, out var m) && !m.Def.IsChild
            ? m : throw new DocumentNotFoundException("DocType", docType);

        // Verify user can read and modify this document
        var doc = await docService.GetAsync(user, docType, docName, ct);

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxAttachmentBytes)
                throw new FileRejectedException($"Attachment size exceeds maximum limit of {MaxAttachmentBytes / (1024 * 1024)} MB.");
        }

        var bytes = ms.ToArray();
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = "attachment";

        var sniffed = SniffContentType(bytes, safeFileName)
            ?? throw new FileRejectedException("File format not supported. Allowed: PDF, Office documents (.docx, .xlsx), CSV, plain text, and standard images.");

        var record = new AttachmentRecord
        {
            Id = "att_" + Guid.NewGuid().ToString("N"),
            TenantId = user.TenantId,
            DocType = docType,
            DocName = docName,
            FileName = safeFileName,
            ContentType = sniffed,
            Size = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            UploadedBy = user.Id,
            CreatedAt = clock.GetUtcNow(),
            IsPrivate = isPrivate,
        };

        await store.SaveAsync(record, bytes, ct);
        return record;
    }

    public async Task<IReadOnlyList<AttachmentRecord>> ListAsync(UserContext user, string docType, string docName, CancellationToken ct = default)
    {
        // Demand read permission on the target document
        await docService.GetAsync(user, docType, docName, ct);
        return await store.ListAsync(user.TenantId, docType, docName, ct);
    }

    public async Task<(AttachmentRecord Record, byte[] Content)> GetFileAsync(UserContext user, string id, CancellationToken ct = default)
    {
        var item = await store.GetContentAsync(user.TenantId, id, ct)
            ?? throw new KeyNotFoundException($"Attachment '{id}' not found.");

        // Demand read permission on the document this attachment belongs to
        await docService.GetAsync(user, item.Record.DocType, item.Record.DocName, ct);
        return item;
    }

    public async Task DeleteAsync(UserContext user, string id, CancellationToken ct = default)
    {
        var info = await store.GetInfoAsync(user.TenantId, id, ct)
            ?? throw new KeyNotFoundException($"Attachment '{id}' not found.");

        var meta = metadata.Current.DocTypes.TryGetValue(info.DocType, out var m) && !m.Def.IsChild
            ? m : throw new DocumentNotFoundException("DocType", info.DocType);

        var doc = await docService.GetAsync(user, info.DocType, info.DocName, ct);

        // Can delete if user can write/delete the doc or is the original uploader
        var canWrite = permissions.Can(user, meta, DocActions.Write);
        var canDelete = permissions.Can(user, meta, DocActions.Delete);
        var isUploader = string.Equals(info.UploadedBy, user.Id, StringComparison.OrdinalIgnoreCase);

        if (!canWrite && !canDelete && !isUploader)
            throw new PermissionDeniedException("You are not allowed to delete this attachment.");

        await store.DeleteAsync(user.TenantId, id, ct);
    }
}

