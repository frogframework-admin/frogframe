using Framework.Documents;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Hooks;

public interface IDocumentService
{
    Task<Document> GetAsync(string docType, string name, CancellationToken ct = default);
    Task<object?> GetFieldAsync(string docType, string name, string fieldName, CancellationToken ct = default);
}

public interface IDocHookLogger
{
    void LogHookFailure(Exception exception, Type hookType, string docType, DocHookStage stage);
}

public sealed class HookContext
{
    public required UserContext User { get; init; }
    public required CompiledDocType Meta { get; init; }
    public required Document Doc { get; init; }
    public Document? Previous { get; init; }
    public required IDocumentLookup Lookup { get; init; }
    public required IDocumentService Docs { get; init; }
    public required List<OutboxMessage> Events { get; init; }
    public List<string> Messages { get; init; } = [];
    /// <summary>Hook menambah baris buku besar di sini; disimpan atomik bersama dokumen.</summary>
    public List<Framework.Accounting.LedgerLine> Ledger { get; init; } = [];
    public string? Action { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HookOrderAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

public interface IBeforeValidate<TDoc>
{
    Task BeforeValidateAsync(HookContext context, CancellationToken ct);
}

public interface IBeforeInsert<TDoc>
{
    Task BeforeInsertAsync(HookContext context, CancellationToken ct);
}

public interface IBeforeUpdate<TDoc>
{
    Task BeforeUpdateAsync(HookContext context, IReadOnlyList<FieldChange> changes, CancellationToken ct);
}

public interface IAfterSave<TDoc>
{
    Task AfterSaveAsync(HookContext context, CancellationToken ct);
}

public interface IOnLoad<TDoc>
{
    Task OnLoadAsync(HookContext context, CancellationToken ct);
}

public interface IBeforeTransition<TDoc>
{
    Task BeforeTransitionAsync(HookContext context, CancellationToken ct);
}

public interface IBeforeDelete<TDoc>
{
    Task BeforeDeleteAsync(HookContext context, CancellationToken ct);
}

public interface IAfterDelete<TDoc>
{
    Task AfterDeleteAsync(HookContext context, CancellationToken ct);
}

public interface IOnCancel<TDoc>
{
    Task OnCancelAsync(HookContext context, CancellationToken ct);
}

public interface IOnAmend<TDoc>
{
    Task OnAmendAsync(HookContext context, Document amendedDraft, CancellationToken ct);
}

public enum DocHookStage
{
    BeforeValidate,
    BeforeInsert,
    BeforeUpdate,
    AfterSave,
    OnLoad,
    BeforeTransition,
    BeforeDelete,
    AfterDelete,
    OnCancel,
    OnAmend
}

public interface IDocHookRegistration
{
    string DocType { get; }
    int Order { get; }
    Type HookType { get; }
    bool Supports(DocHookStage stage);
    Task InvokeAsync(DocHookStage stage, HookContext context, IReadOnlyList<FieldChange>? changes, Document? amendedDraft, CancellationToken ct);
}

public sealed class DocHookRegistration<TDoc, THook>(THook hook) : IDocHookRegistration where THook : class
{
    public string DocType => typeof(TDoc).Name;
    public int Order => typeof(THook).GetCustomAttributes(typeof(HookOrderAttribute), false).Cast<HookOrderAttribute>().FirstOrDefault()?.Order ?? 100;
    public Type HookType => typeof(THook);

    public bool Supports(DocHookStage stage) => stage switch
    {
        DocHookStage.BeforeValidate => hook is IBeforeValidate<TDoc>,
        DocHookStage.BeforeInsert => hook is IBeforeInsert<TDoc>,
        DocHookStage.BeforeUpdate => hook is IBeforeUpdate<TDoc>,
        DocHookStage.AfterSave => hook is IAfterSave<TDoc>,
        DocHookStage.OnLoad => hook is IOnLoad<TDoc>,
        DocHookStage.BeforeTransition => hook is IBeforeTransition<TDoc>,
        DocHookStage.BeforeDelete => hook is IBeforeDelete<TDoc>,
        DocHookStage.AfterDelete => hook is IAfterDelete<TDoc>,
        DocHookStage.OnCancel => hook is IOnCancel<TDoc>,
        DocHookStage.OnAmend => hook is IOnAmend<TDoc>,
        _ => false,
    };

    public Task InvokeAsync(DocHookStage stage, HookContext context, IReadOnlyList<FieldChange>? changes, Document? amendedDraft, CancellationToken ct) => stage switch
    {
        DocHookStage.BeforeValidate when hook is IBeforeValidate<TDoc> h => h.BeforeValidateAsync(context, ct),
        DocHookStage.BeforeInsert when hook is IBeforeInsert<TDoc> h => h.BeforeInsertAsync(context, ct),
        DocHookStage.BeforeUpdate when hook is IBeforeUpdate<TDoc> h => h.BeforeUpdateAsync(context, changes ?? [], ct),
        DocHookStage.AfterSave when hook is IAfterSave<TDoc> h => h.AfterSaveAsync(context, ct),
        DocHookStage.OnLoad when hook is IOnLoad<TDoc> h => h.OnLoadAsync(context, ct),
        DocHookStage.BeforeTransition when hook is IBeforeTransition<TDoc> h => h.BeforeTransitionAsync(context, ct),
        DocHookStage.BeforeDelete when hook is IBeforeDelete<TDoc> h => h.BeforeDeleteAsync(context, ct),
        DocHookStage.AfterDelete when hook is IAfterDelete<TDoc> h => h.AfterDeleteAsync(context, ct),
        DocHookStage.OnCancel when hook is IOnCancel<TDoc> h => h.OnCancelAsync(context, ct),
        DocHookStage.OnAmend when hook is IOnAmend<TDoc> h => h.OnAmendAsync(context, amendedDraft!, ct),
        _ => Task.CompletedTask,
    };
}

public sealed class DocumentHookException(Exception innerException)
    : Exception("An error occurred while executing document hooks. Please try again or contact your administrator.", innerException);

public sealed class HookRecursionException(string message) : InvalidOperationException(message);
