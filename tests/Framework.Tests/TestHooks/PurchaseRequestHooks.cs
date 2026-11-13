using System.Globalization;
using Framework.Documents;
using Framework.Hooks;

namespace Framework.Tests.TestHooks;

public sealed class PurchaseRequest { }

public sealed class PurchaseRequestBeforeValidateHook : IBeforeValidate<PurchaseRequest>
{
    public Task BeforeValidateAsync(HookContext context, CancellationToken ct)
    {
        if (context.Doc.Data.GetValueOrDefault("justification") is string justification)
            context.Doc.Data["justification"] = justification.Trim();
        return Task.CompletedTask;
    }
}

public sealed class PurchaseRequestBeforeInsertHook(TimeProvider clock) : IBeforeInsert<PurchaseRequest>
{
    public Task BeforeInsertAsync(HookContext context, CancellationToken ct)
    {
        if (context.Doc.Data.GetValueOrDefault("request_date") is string value
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requestDate)
            && requestDate < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-30))
            throw new ValidationFailedException([new FieldError("request_date", "Tanggal pengajuan tidak boleh lebih dari 30 hari di masa lalu")]);

        return Task.CompletedTask;
    }
}

public sealed class PurchaseRequestAfterSaveHook(TimeProvider clock) : IAfterSave<PurchaseRequest>
{
    public Task AfterSaveAsync(HookContext context, CancellationToken ct)
    {
        if (context.Doc.Data.GetValueOrDefault("priority") is not string priority || priority != "Tinggi")
            return Task.CompletedTask;

        var type = "pr.high_priority";
        context.Events.Add(new OutboxMessage
        {
            TenantId = context.Doc.TenantId,
            Type = type,
            CreatedAt = clock.GetUtcNow(),
            IdempotencyKey = $"{context.Doc.TenantId}:{context.Doc.DocType}:{context.Doc.Name}:v{context.Doc.Version}:{type}",
            Payload = new()
            {
                ["docType"] = context.Doc.DocType,
                ["name"] = context.Doc.Name,
                ["owner"] = context.Doc.Owner,
                ["actor"] = context.User.Id,
                ["version"] = context.Doc.Version.ToString(CultureInfo.InvariantCulture),
            },
        });
        return Task.CompletedTask;
    }
}

public sealed class PurchaseRequestOnLoadHook : IOnLoad<PurchaseRequest>
{
    public async Task OnLoadAsync(HookContext context, CancellationToken ct)
    {
        if (context.Doc.Data.GetValueOrDefault("requester") is not string employeeId) return;
        var employee = await context.Docs.GetAsync("Employee", employeeId, ct);
        if (employee.Data.GetValueOrDefault("employee_name") is string employeeName)
            context.Doc.Data["requester_name"] = employeeName;
    }
}

public sealed class PurchaseRequestBeforeTransitionHook : IBeforeTransition<PurchaseRequest>
{
    public Task BeforeTransitionAsync(HookContext context, CancellationToken ct)
    {
        if (context.Action == "Ajukan"
            && context.Doc.Data.GetValueOrDefault("items") is not List<Dictionary<string, object?>> { Count: > 0 })
            throw new ValidationFailedException([new FieldError("items", "Tambahkan minimal satu item sebelum mengajukan")]);

        return Task.CompletedTask;
    }
}