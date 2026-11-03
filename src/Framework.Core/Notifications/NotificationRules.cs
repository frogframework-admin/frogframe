using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Email;
using Framework.Expressions;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Validation;

namespace Framework.Notifications;

public enum NotificationTrigger
{
    OnCreate,
    OnSubmit,
    OnValueChange,
    DaysBeforeDate,
    DaysAfterDate
}

public enum NotificationChannel
{
    InApp,
    Email,
    Both
}

public enum NotificationReceiverType
{
    Role,
    Field,
    Owner,
    SpecificUser
}

public sealed record NotificationRule
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required string DocType { get; init; }
    public required NotificationTrigger Trigger { get; init; }
    public string? TriggerField { get; init; }
    public int? DaysOffset { get; init; }
    public string? Condition { get; init; }
    public required NotificationChannel Channel { get; init; }
    public required NotificationReceiverType ReceiverType { get; init; }
    public string? ReceiverValue { get; init; }
    public required string SubjectTemplate { get; init; }
    public required string MessageTemplate { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

public interface INotificationRuleStore
{
    Task<IReadOnlyList<NotificationRule>> ListAsync(string tenantId, string? docType = null, bool onlyActive = false, CancellationToken ct = default);
    Task<NotificationRule?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task SaveAsync(NotificationRule rule, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class InMemoryNotificationRuleStore : INotificationRuleStore
{
    private readonly ConcurrentDictionary<string, NotificationRule> _rules = new();

    private static string Key(string tenantId, string id) => $"{tenantId}:{id}";

    public Task<IReadOnlyList<NotificationRule>> ListAsync(string tenantId, string? docType = null, bool onlyActive = false, CancellationToken ct = default)
    {
        var list = _rules.Values
            .Where(r => r.TenantId == tenantId)
            .Where(r => !onlyActive || r.IsActive)
            .Where(r => string.IsNullOrEmpty(docType) || string.Equals(r.DocType, docType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.ModifiedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<NotificationRule>>(list);
    }

    public Task<NotificationRule?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _rules.TryGetValue(Key(tenantId, id), out var rule);
        return Task.FromResult(rule);
    }

    public Task SaveAsync(NotificationRule rule, CancellationToken ct = default)
    {
        _rules[Key(rule.TenantId, rule.Id)] = rule;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _rules.TryRemove(Key(tenantId, id), out _);
        return Task.CompletedTask;
    }
}

