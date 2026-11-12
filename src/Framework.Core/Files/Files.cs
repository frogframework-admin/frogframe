using System.Collections.Concurrent;
using System.Security.Cryptography;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Files;

/// <summary>
/// File yang diunggah (mis. gambar field tipe Image). Selalu terikat tenant. Setelah dokumen disimpan, file
/// "ditautkan" ke dokumen tersebut; siapa pun yang boleh membaca dokumen boleh melihat filenya.
/// Sebelum ditautkan, hanya pengunggah yang boleh melihat (pratinjau di form).
/// </summary>
public sealed record FileRecord
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string UploadedBy { get; init; }
    public required string ContentType { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? DocType { get; init; }
    public string? DocName { get; init; }
    public string? Field { get; init; }
    /// <summary>Original file name (Attach fields); null for images.</summary>
    public string? FileName { get; init; }
}

public interface IFileStore
{
    Task SaveAsync(FileRecord record, byte[] content, CancellationToken ct = default);
    /// <summary>Metadata file dalam tenant (null bila tidak ada / milik tenant lain).</summary>
    Task<FileRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default);
    Task<(FileRecord Record, byte[] Content)?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task AttachAsync(string tenantId, string id, string docType, string docName, string field, CancellationToken ct = default);
    /// <summary>Menghapus file yang tidak pernah ditautkan ke dokumen dan lebih tua dari <paramref name="cutoff"/>. Mengembalikan jumlahnya.</summary>
    Task<int> PruneOrphansAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    /// Seperti <see cref="PruneOrphansAsync"/>, tetapi mengembalikan record yang dihapus (maks. 500 per panggilan),
    /// supaya isi file di object storage ikut dihapus. Implementasi lama tanpa method ini tetap terkompilasi.
    /// </summary>
    async Task<IReadOnlyList<FileRecord>> PruneOrphansReturningAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await PruneOrphansAsync(cutoff, ct);
        return [];
    }
}

public sealed class InMemoryFileStore : IFileStore
{
    private readonly ConcurrentDictionary<string, (FileRecord Record, byte[] Content)> _files = new();

    public Task SaveAsync(FileRecord record, byte[] content, CancellationToken ct = default)
    {
        _files[record.Id] = (record, content);
        return Task.CompletedTask;
    }

    public Task<FileRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_files.TryGetValue(id, out var f) && f.Record.TenantId == tenantId ? f.Record : null);

    public Task<(FileRecord Record, byte[] Content)?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult<(FileRecord, byte[])?>(_files.TryGetValue(id, out var f) && f.Record.TenantId == tenantId ? f : null);

    public Task AttachAsync(string tenantId, string id, string docType, string docName, string field, CancellationToken ct = default)
    {
        if (_files.TryGetValue(id, out var f) && f.Record.TenantId == tenantId)
            _files[id] = (f.Record with { DocType = docType, DocName = docName, Field = field }, f.Content);
        return Task.CompletedTask;
    }

    public async Task<int> PruneOrphansAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        (await PruneOrphansReturningAsync(cutoff, ct)).Count;

    public Task<IReadOnlyList<FileRecord>> PruneOrphansReturningAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var removed = new List<FileRecord>();
        foreach (var (id, f) in _files.Where(kv => kv.Value.Record.DocType is null && kv.Value.Record.CreatedAt < cutoff).Take(500).ToList())
            if (_files.TryRemove(id, out _)) removed.Add(f.Record);
        return Task.FromResult<IReadOnlyList<FileRecord>>(removed);
    }
}

public sealed class FileRejectedException(string message) : Exception(message);

/// <summary>Upload gambar dengan validasi isi file (magic bytes), ukuran, dan izin menulis DocType.</summary>
public sealed class FileService(IFileStore store, IMetadataProvider metadata, IPermissionService permissions, TimeProvider clock)
{
    public const long MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Format yang diizinkan, dikenali dari isi file — bukan dari nama/ekstensi/Content-Type kiriman browser.
    /// SVG sengaja tidak diizinkan (bisa berisi skrip).</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : null;

    public async Task<string> UploadImageAsync(UserContext user, string docType, string field, Stream content, CancellationToken ct = default)
    {
        if (!metadata.Current.TryGet(docType, out var meta) || !meta.ByName.TryGetValue(field, out var f) || f.Def.Type != "Image")
            throw new FileRejectedException("Unknown image field.");
        if (!permissions.Can(user, meta, DocActions.Create) && !permissions.Can(user, meta, DocActions.Write))
            throw new PermissionDeniedException($"You are not allowed to upload images for {meta.Title}.");
        if (!permissions.CanWriteField(user, meta, f))
            throw new PermissionDeniedException($"You are not allowed to modify {f.Label}.");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxImageBytes) throw new FileRejectedException("Image size exceeds maximum limit of 2 MB.");
        }
        var bytes = ms.ToArray();
        var type = SniffImage(bytes) ?? throw new FileRejectedException("Unsupported format. Use PNG, JPEG, WebP, or GIF.");
        var record = new FileRecord
        {
            Id = "f_" + Guid.NewGuid().ToString("N"), TenantId = user.TenantId, UploadedBy = user.Id, ContentType = type,
            Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), CreatedAt = clock.GetUtcNow(),
        };
        await store.SaveAsync(record, bytes, ct);
        return record.Id;
    }

    public const long MaxAttachBytes = AttachmentService.MaxAttachmentBytes;

    /// <summary>
    /// Upload for an Attach field: PDF, Office (.docx/.xlsx), CSV, text or image (max 10 MB), type detected from the
    /// content. Like images, the file is visible only to the uploader until the document is saved and links it.
    /// </summary>
    public async Task<string> UploadFieldFileAsync(UserContext user, string docType, string field, string fileName, Stream content, CancellationToken ct = default)
    {
        if (!metadata.Current.TryGet(docType, out var meta) || !meta.ByName.TryGetValue(field, out var f) || f.Def.Type != "Attach")
            throw new FileRejectedException("Unknown attachment field.");
        if (!permissions.Can(user, meta, DocActions.Create) && !permissions.Can(user, meta, DocActions.Write))
            throw new PermissionDeniedException($"You are not allowed to upload files for {meta.Title}.");
        if (!permissions.CanWriteField(user, meta, f))
            throw new PermissionDeniedException($"You are not allowed to modify {f.Label}.");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxAttachBytes) throw new FileRejectedException("File size exceeds maximum limit of 10 MB.");
        }
        var bytes = ms.ToArray();
        var safeName = SafeFileName(fileName);
        var type = AttachmentService.SniffContentType(bytes, safeName)
            ?? throw new FileRejectedException("File format not supported. Allowed: PDF, Office documents (.docx, .xlsx), CSV, plain text, and standard images.");
        var record = new FileRecord
        {
            Id = "f_" + Guid.NewGuid().ToString("N"), TenantId = user.TenantId, UploadedBy = user.Id, ContentType = type,
            Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), CreatedAt = clock.GetUtcNow(),
            FileName = safeName,
        };
        await store.SaveAsync(record, bytes, ct);
        return record.Id;
    }

    /// <summary>File name without path and control characters, max 200 characters.</summary>
    public static string SafeFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        n = new string(n.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (n.Length == 0) n = "file";
        return n.Length <= 200 ? n : n[..200];
    }

    /// <summary>Uploads and validates a user profile photo (avatar) for the specified user account.</summary>
    public async Task<string> UploadAvatarAsync(UserContext user, Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxImageBytes) throw new FileRejectedException("Image size exceeds maximum limit of 2 MB.");
        }
        var bytes = ms.ToArray();
        var type = SniffImage(bytes) ?? throw new FileRejectedException("Unsupported format. Use PNG, JPEG, WebP, or GIF.");
        var record = new FileRecord
        {
            Id = "f_" + Guid.NewGuid().ToString("N"),
            TenantId = user.TenantId,
            UploadedBy = user.Id,
            ContentType = type,
            Size = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            CreatedAt = clock.GetUtcNow(),
            DocType = "UserAccount",
            DocName = user.Id,
            Field = "avatar"
        };
        await store.SaveAsync(record, bytes, ct);
        return record.Id;
    }
}
