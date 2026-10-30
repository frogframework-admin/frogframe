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
}

public interface IFileStore
{
    Task SaveAsync(FileRecord record, byte[] content, CancellationToken ct = default);
    /// <summary>Metadata file dalam tenant (null bila tidak ada / milik tenant lain).</summary>
    Task<FileRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default);
    Task<(FileRecord Record, byte[] Content)?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task AttachAsync(string tenantId, string id, string docType, string docName, string field, CancellationToken ct = default);
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
            throw new FileRejectedException("Field gambar tidak dikenal");
        if (!permissions.Can(user, meta, DocActions.Create) && !permissions.Can(user, meta, DocActions.Write))
            throw new PermissionDeniedException($"Anda tidak boleh mengunggah gambar untuk {meta.Title}");
        if (!permissions.CanWriteField(user, meta, f))
            throw new PermissionDeniedException($"Anda tidak boleh mengubah {f.Label}");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxImageBytes) throw new FileRejectedException("Ukuran gambar maksimal 2 MB");
        }
        var bytes = ms.ToArray();
        var type = SniffImage(bytes) ?? throw new FileRejectedException("Format tidak didukung. Gunakan PNG, JPEG, WebP, atau GIF.");
        var record = new FileRecord
        {
            Id = "f_" + Guid.NewGuid().ToString("N"), TenantId = user.TenantId, UploadedBy = user.Id, ContentType = type,
            Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), CreatedAt = clock.GetUtcNow(),
        };
        await store.SaveAsync(record, bytes, ct);
        return record.Id;
    }
}
