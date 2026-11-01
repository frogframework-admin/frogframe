using Framework.Metadata;
using Framework.Security;

namespace Framework.Documents;

public static class DocStatus
{
    public const int Draft = 0, Submitted = 1, Cancelled = 2;
    public static string Label(int s) => s switch { 0 => "Draft", 1 => "Submitted", 2 => "Cancelled", _ => "?" };
}

public sealed class Document
{
    public required string DocType { get; init; }
    public required string Name { get; init; }
    public required string TenantId { get; init; }
    public required string Owner { get; init; }
    public int DocStatus { get; set; }
    public string? WorkflowState { get; set; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; set; }
    public string ModifiedBy { get; set; } = "";
    /// <summary>Nomor versi untuk optimistic concurrency.</summary>
    public int Version { get; set; } = 1;
    public int SchemaVersion { get; set; }
    public int? WorkflowVersion { get; set; }
    public string? AmendedFrom { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    /// <summary>Transient messages emitted by hooks (e.g. msgprint) during the operation lifecycle.</summary>
    public List<string> TransientMessages { get; } = [];

    public object? Get(string key) => key switch
    {
        StandardFields.Name => Name,
        StandardFields.Owner => Owner,
        StandardFields.DocStatus => (decimal)DocStatus,
        StandardFields.WorkflowState => WorkflowState,
        StandardFields.Created => Created.ToString("yyyy-MM-dd HH:mm:ss"),
        StandardFields.Modified => Modified.ToString("yyyy-MM-dd HH:mm:ss"),
        StandardFields.ModifiedBy => ModifiedBy,
        StandardFields.AmendedFrom => AmendedFrom,
        _ => Data.TryGetValue(key, out var v) ? v : null
    };

    public Document Clone()
    {
        var copy = new Document
        {
            DocType = DocType, Name = Name, TenantId = TenantId, Owner = Owner, DocStatus = DocStatus,
            WorkflowState = WorkflowState, Created = Created, Modified = Modified, ModifiedBy = ModifiedBy,
            Version = Version, SchemaVersion = SchemaVersion, WorkflowVersion = WorkflowVersion,
            AmendedFrom = AmendedFrom, Data = DeepCopy(Data)
        };
        copy.TransientMessages.AddRange(TransientMessages);
        return copy;
    }

    public static Dictionary<string, object?> DeepCopy(IReadOnlyDictionary<string, object?> src) =>
        src.ToDictionary(kv => kv.Key, kv => kv.Value is List<Dictionary<string, object?>> rows
            ? (object?)rows.Select(r => DeepCopy(r)).ToList()
            : kv.Value);
}

// ---------- Error model ----------

public sealed record FieldError(string Path, string Message);

/// <summary>Kegagalan pada satu baris dalam insert batch (impor atomik); tidak ada yang tersimpan.</summary>
public sealed class BatchRowException(int index, Exception inner) : Exception($"Baris ke-{index + 1}: {inner.Message}", inner)
{
    public int Index { get; } = index;
}

public sealed class ValidationFailedException(IReadOnlyList<FieldError> errors)
    : Exception(errors.Count == 1 ? errors[0].Message : $"{errors.Count} kesalahan validasi")
{
    public IReadOnlyList<FieldError> Errors { get; } = errors;
}

public sealed class DocumentNotFoundException(string docType, string name)
    : Exception($"{docType} '{name}' tidak ditemukan");

public sealed class ConcurrencyException(string name, int expected, int actual)
    : Exception($"Dokumen {name} sudah diubah pengguna lain (versi Anda {expected}, versi terkini {actual}). Muat ulang lalu ulangi.");

public sealed class InvalidPayloadException(string message, IReadOnlyList<string>? fields = null) : Exception(message)
{
    public IReadOnlyList<string> Fields { get; } = fields ?? [];
}

public sealed class InvalidStateException(string message) : Exception(message);

// ---------- Lifecycle hooks (RFC §6) ----------

public enum DocumentOperation { Insert, Update, Submit, Cancel, Delete, Transition, Amend }

public sealed class DocumentContext
{
    public required CompiledDocType Meta { get; init; }
    public required Document Doc { get; init; }
    public Document? Previous { get; init; }
    public required UserContext User { get; init; }
    public required DocumentOperation Operation { get; init; }
    public required IDocumentLookup Lookup { get; init; }
    public string? Comment { get; init; }
    public List<FieldError> Errors { get; } = [];
    /// <summary>Transient non-fatal notification messages emitted during document operations.</summary>
    public List<string> Messages { get; } = [];
    /// <summary>Event domain yang akan ditulis ke outbox dalam transaksi yang sama.</summary>
    public List<OutboxMessage> Events { get; } = [];

    public void AddError(string path, string message) => Errors.Add(new FieldError(path, message));
    public void AddMessage(string message) => Messages.Add(message);
}

/// <summary>Akses baca terbatas tenant yang diberikan ke handler (tanpa melewati batas tenant).</summary>
public interface IDocumentLookup
{
    Task<Document?> GetAsync(string docType, string name, CancellationToken ct = default);
}

public interface IDocumentHandler
{
    string DocType { get; }
}

// Hook opsional dipisah menjadi interface kecil + base class virtual, sesuai catatan RFC §6.
public interface IBeforeValidate : IDocumentHandler { Task BeforeValidateAsync(DocumentContext ctx, CancellationToken ct); }
public interface IValidateDocument : IDocumentHandler { Task ValidateAsync(DocumentContext ctx, CancellationToken ct); }
public interface IBeforeSave : IDocumentHandler { Task BeforeSaveAsync(DocumentContext ctx, CancellationToken ct); }
public interface IAfterSave : IDocumentHandler { Task AfterSaveAsync(DocumentContext ctx, CancellationToken ct); }
public interface IBeforeSubmit : IDocumentHandler { Task BeforeSubmitAsync(DocumentContext ctx, CancellationToken ct); }
public interface IBeforeCancel : IDocumentHandler { Task BeforeCancelAsync(DocumentContext ctx, CancellationToken ct); }

public abstract class DocumentHandlerBase : IBeforeValidate, IValidateDocument, IBeforeSave, IAfterSave, IBeforeSubmit, IBeforeCancel
{
    public abstract string DocType { get; }
    public virtual Task BeforeValidateAsync(DocumentContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual Task ValidateAsync(DocumentContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual Task BeforeSaveAsync(DocumentContext ctx, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Dijalankan di dalam unit of work, sebelum commit. Jangan memanggil sistem eksternal di sini — tulis event.</summary>
    public virtual Task AfterSaveAsync(DocumentContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual Task BeforeSubmitAsync(DocumentContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual Task BeforeCancelAsync(DocumentContext ctx, CancellationToken ct) => Task.CompletedTask;
}

public interface IDocumentHandlerRegistry
{
    IEnumerable<T> For<T>(string docType) where T : IDocumentHandler;
}

public sealed class DocumentHandlerRegistry(IEnumerable<IDocumentHandler> handlers) : IDocumentHandlerRegistry
{
    private readonly ILookup<string, IDocumentHandler> _byDocType = handlers.ToLookup(h => h.DocType);
    public IEnumerable<T> For<T>(string docType) where T : IDocumentHandler => _byDocType[docType].OfType<T>();
}

// ---------- Validator kustom terdaftar (dirujuk dari metadata dengan nama stabil) ----------

public interface IFieldValidator
{
    string Name { get; }
    string? Validate(object? value, CompiledField field);
}

public interface IValidatorRegistry
{
    bool TryGet(string name, out IFieldValidator validator);
}

public sealed class ValidatorRegistry(IEnumerable<IFieldValidator> validators) : IValidatorRegistry
{
    private readonly Dictionary<string, IFieldValidator> _map = validators.ToDictionary(v => v.Name);
    public bool TryGet(string name, out IFieldValidator validator) => _map.TryGetValue(name, out validator!);
}

// ---------- Audit & outbox ----------

public sealed record FieldChange(string Field, string? Old, string? New, int PermLevel = 0);

public sealed record AuditEntry
{
    public long Id { get; init; }
    public required string TenantId { get; init; }
    public required string DocType { get; init; }
    public required string DocName { get; init; }
    public required string Action { get; init; }
    public required string Actor { get; init; }
    public required DateTimeOffset At { get; init; }
    public IReadOnlyList<FieldChange> Changes { get; init; } = [];
    public string? Comment { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public int? WorkflowVersion { get; init; }
    public int SchemaVersion { get; init; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TenantId { get; init; }
    public required string Type { get; init; }
    public required Dictionary<string, string?> Payload { get; init; }
    /// <summary>Kunci idempotensi agar pemrosesan ulang tidak menggandakan efek samping.</summary>
    public required string IdempotencyKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
    public bool DeadLettered { get; set; }
}

public sealed record Notification(Guid Id, string TenantId, string UserId, string Subject, string Body,
    string? DocType, string? DocName, DateTimeOffset At, bool Read = false);
