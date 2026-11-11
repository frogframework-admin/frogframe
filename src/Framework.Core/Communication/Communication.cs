using System.Collections.Concurrent;
using System.Text.Json;
using Framework.Documents;
using Framework.Security;

namespace Framework.Communication;

public enum CommunicationDirection
{
    In,
    Out
}

public sealed class EmailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string PasswordEnc { get; set; } = "";
    public string Folder { get; set; } = "INBOX";
    public string? AppendToDocType { get; set; }
    public Dictionary<string, string> FieldMap { get; set; } = [];
    public string ServiceUserId { get; set; } = "";
    public long UidValidity { get; set; }
    public long LastUid { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
}

public sealed class CommunicationEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "";
    public string? ReferenceDocType { get; set; }
    public string? ReferenceName { get; set; }
    public CommunicationDirection Direction { get; set; }
    public string MessageId { get; set; } = "";
    public string? InReplyTo { get; set; }
    public string Subject { get; set; } = "";
    public string Sender { get; set; } = "";
    public List<string> Recipients { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public string? ContentHtml { get; set; }
    public string? ContentText { get; set; }
    public Guid? EmailAccountId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public interface IEmailAccountStore
{
    Task<IReadOnlyList<EmailAccount>> ListActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EmailAccount>> ListByTenantAsync(string tenantId, CancellationToken ct = default);
    Task<EmailAccount?> GetByIdAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task SaveAsync(EmailAccount account, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task UpdatePollingStateAsync(Guid id, long uidValidity, long lastUid, DateTimeOffset polledAt, string? lastError, CancellationToken ct = default);
}

public interface ICommunicationStore
{
    Task<bool> ExistsMessageIdAsync(string tenantId, string messageId, CancellationToken ct = default);
    Task<CommunicationEntry?> GetByMessageIdAsync(string tenantId, string messageId, CancellationToken ct = default);
    Task<IReadOnlyList<CommunicationEntry>> ListForDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
    Task<IReadOnlyList<CommunicationEntry>> ListUnlinkedAsync(string tenantId, int limit = 50, CancellationToken ct = default);
    Task LinkAsync(string tenantId, Guid communicationId, string docType, string docName, CancellationToken ct = default);
    Task SaveAsync(CommunicationEntry entry, CancellationToken ct = default);
}

public sealed class CommunicationService(
    ICommunicationStore store,
    DocumentService docs)
{
    public async Task<IReadOnlyList<CommunicationEntry>> ListForDocumentAsync(
        UserContext user,
        string docType,
        string docName,
        CancellationToken ct = default)
    {
        // Enforce document permission and row scope. If user lacks access, GetAsync throws PermissionDeniedException.
        await docs.GetAsync(user, docType, docName, ct);
        return await store.ListForDocumentAsync(user.TenantId, docType, docName, ct);
    }

    public async Task<IReadOnlyList<CommunicationEntry>> ListUnlinkedAsync(
        UserContext user,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (!user.Roles.Contains("System Manager") && !user.Roles.Contains("Administrator"))
            throw new PermissionDeniedException("Only System Managers can view unlinked communications.");

        return await store.ListUnlinkedAsync(user.TenantId, limit, ct);
    }

    public async Task LinkAsync(
        UserContext user,
        Guid communicationId,
        string docType,
        string docName,
        CancellationToken ct = default)
    {
        // Must have access to target document
        await docs.GetAsync(user, docType, docName, ct);
        await store.LinkAsync(user.TenantId, communicationId, docType, docName, ct);
    }
}

public sealed class InMemoryEmailAccountStore : IEmailAccountStore
{
    private readonly ConcurrentDictionary<Guid, EmailAccount> _accounts = new();

    public Task<IReadOnlyList<EmailAccount>> ListActiveAsync(CancellationToken ct = default)
    {
        var active = _accounts.Values
            .Where(a => a.IsActive)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<EmailAccount>>(active);
    }

    public Task<IReadOnlyList<EmailAccount>> ListByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var list = _accounts.Values
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<EmailAccount>>(list);
    }

    public Task<EmailAccount?> GetByIdAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        _accounts.TryGetValue(id, out var acc);
        if (acc is not null && acc.TenantId == tenantId)
            return Task.FromResult<EmailAccount?>(acc);
        return Task.FromResult<EmailAccount?>(null);
    }

    public Task SaveAsync(EmailAccount account, CancellationToken ct = default)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        if (_accounts.TryGetValue(id, out var acc) && acc.TenantId == tenantId)
        {
            _accounts.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    public Task UpdatePollingStateAsync(
        Guid id,
        long uidValidity,
        long lastUid,
        DateTimeOffset polledAt,
        string? lastError,
        CancellationToken ct = default)
    {
        if (_accounts.TryGetValue(id, out var acc))
        {
            acc.UidValidity = uidValidity;
            acc.LastUid = lastUid;
            acc.LastPolledAt = polledAt;
            acc.LastError = lastError;
            acc.ModifiedAt = polledAt;
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryCommunicationStore : ICommunicationStore
{
    private readonly ConcurrentDictionary<Guid, CommunicationEntry> _entries = new();

    public Task<bool> ExistsMessageIdAsync(string tenantId, string messageId, CancellationToken ct = default)
    {
        var exists = _entries.Values.Any(e => e.TenantId == tenantId &&
            string.Equals(e.MessageId, messageId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(exists);
    }

    public Task<CommunicationEntry?> GetByMessageIdAsync(string tenantId, string messageId, CancellationToken ct = default)
    {
        var entry = _entries.Values.FirstOrDefault(e => e.TenantId == tenantId &&
            string.Equals(e.MessageId, messageId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<CommunicationEntry>> ListForDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        var list = _entries.Values
            .Where(e => e.TenantId == tenantId &&
                        string.Equals(e.ReferenceDocType, docType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.ReferenceName, docName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.OccurredAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<CommunicationEntry>>(list);
    }

    public Task<IReadOnlyList<CommunicationEntry>> ListUnlinkedAsync(string tenantId, int limit = 50, CancellationToken ct = default)
    {
        var list = _entries.Values
            .Where(e => e.TenantId == tenantId && string.IsNullOrEmpty(e.ReferenceDocType))
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<CommunicationEntry>>(list);
    }

    public Task LinkAsync(string tenantId, Guid communicationId, string docType, string docName, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(communicationId, out var entry) && entry.TenantId == tenantId)
        {
            entry.ReferenceDocType = docType;
            entry.ReferenceName = docName;
        }
        return Task.CompletedTask;
    }

    public Task SaveAsync(CommunicationEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }
}
