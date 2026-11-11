namespace Framework.Security;

/// <summary>
/// Provides verified, permission-scoped UserContext instances for automated background tasks
/// (e.g., Auto Repeat document generation, scheduled server scripts, inbound email processing).
/// Strictly forbids the use of UserContext.System for document mutations.
/// </summary>
public interface IAutomationIdentity
{
    /// <summary>
    /// Loads the target user account, verifies its tenant isolation and active status,
    /// and constructs the corresponding permission-scoped UserContext.
    /// </summary>
    Task<UserContext> ForUserAsync(string tenantId, string userId, CancellationToken ct = default);
}

/// <summary>
/// Standard implementation of IAutomationIdentity backed by IUserAccountRepository.
/// </summary>
public sealed class AutomationIdentity(IUserAccountRepository accounts) : IAutomationIdentity
{
    public async Task<UserContext> ForUserAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User ID is required.", nameof(userId));

        var account = await accounts.FindByIdAsync(userId, ct);
        if (account is null || !string.Equals(account.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"User account '{userId}' not found in tenant '{tenantId}'.");
        }

        if (!account.IsActive)
        {
            throw new InvalidOperationException($"User account '{userId}' is inactive.");
        }

        return new UserContext(
            account.Id,
            account.FullName,
            account.TenantId,
            account.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase),
            account.Attributes());
    }
}

