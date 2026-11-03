using System.Collections.Concurrent;
using System.Text.Json;

namespace Framework.Security;

/// <summary>
/// Akun login. Disimpan oleh <see cref="IUserAccountRepository"/>; dipakai ASP.NET Core Identity lewat adapter
/// di lapisan web. Tenant, departemen, karyawan, dan role menjadi klaim, lalu dipetakan ke <see cref="UserContext"/>.
/// </summary>
public sealed class UserAccount
{
    /// <summary>Id stabil (dipakai sebagai owner dokumen dan di audit). Tidak berubah walau username diganti.</summary>
    public string Id { get; set; } = "";
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public string FullName { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string? Department { get; set; }
    /// <summary>Nama dokumen Employee yang terhubung (mis. "EMP-0001").</summary>
    public string? EmployeeId { get; set; }
    /// <summary>File ID of the user's uploaded profile photo stored in IFileStore.</summary>
    public string? AvatarId { get; set; }
    public List<string> Roles { get; set; } = [];
    public bool IsActive { get; set; } = true;

    // Dikelola ASP.NET Core Identity
    public string? PasswordHash { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool LockoutEnabled { get; set; } = true;
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public UserAccount Clone() => JsonSerializer.Deserialize<UserAccount>(JsonSerializer.Serialize(this))!;

    /// <summary>Atribut yang menjadi klaim dan muncul di UserContext.Attributes (dipakai row scope "match:").</summary>
    public IReadOnlyDictionary<string, string> Attributes()
    {
        var a = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(Department)) a["department"] = Department;
        if (!string.IsNullOrWhiteSpace(EmployeeId)) a["employee"] = EmployeeId;
        if (!string.IsNullOrWhiteSpace(AvatarId)) a["avatar"] = AvatarId;
        if (!string.IsNullOrWhiteSpace(Email)) a["email"] = Email;
        return a;
    }
}

public sealed class DuplicateAccountException(string message) : Exception(message);

public interface IUserAccountRepository
{
    Task<UserAccount?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<UserAccount?> FindByNormalizedUserNameAsync(string normalizedUserName, CancellationToken ct = default);
    Task<UserAccount?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<IReadOnlyList<UserAccount>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task CreateAsync(UserAccount account, CancellationToken ct = default);
    Task UpdateAsync(UserAccount account, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class InMemoryUserAccountRepository : IUserAccountRepository
{
    private readonly ConcurrentDictionary<string, UserAccount> _byId = new();

    public Task<UserAccount?> FindByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var u) ? u.Clone() : null);

    public Task<UserAccount?> FindByNormalizedUserNameAsync(string normalizedUserName, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(u => u.NormalizedUserName == normalizedUserName)?.Clone());

    public Task<UserAccount?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail)?.Clone());

    public Task<IReadOnlyList<UserAccount>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserAccount>>(_byId.Values.Where(u => u.TenantId == tenantId).OrderBy(u => u.FullName).Select(u => u.Clone()).ToList());

    public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(!_byId.IsEmpty);

    public Task CreateAsync(UserAccount account, CancellationToken ct = default)
    {
        lock (_byId)
        {
            if (_byId.ContainsKey(account.Id) || _byId.Values.Any(u => u.NormalizedUserName == account.NormalizedUserName))
                throw new DuplicateAccountException($"Username '{account.UserName}' is already in use.");
            _byId[account.Id] = account.Clone();
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(UserAccount account, CancellationToken ct = default)
    {
        lock (_byId)
        {
            if (_byId.Values.Any(u => u.Id != account.Id && u.NormalizedUserName == account.NormalizedUserName))
                throw new DuplicateAccountException($"Username '{account.UserName}' is already in use.");
            _byId[account.Id] = account.Clone();
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _byId.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
