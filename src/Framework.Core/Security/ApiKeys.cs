using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Framework.Security;

/// <summary>
/// Kunci API untuk integrasi sistem-ke-sistem (format api_key:api_secret). Kunci melekat pada sebuah akun:
/// role, tenant, dan departemen akun itulah yang menentukan akses — lewat jalur DocumentService yang sama dengan UI.
/// Cakupan (scope) opsional membatasi lebih jauh. Secret hanya ditampilkan sekali; yang disimpan hanya hash-nya.
/// </summary>
public sealed class ApiKey
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 (hex) dari secret acak 256-bit. Secret berentropi tinggi → hash cepat sudah aman (tidak bisa ditebak).</summary>
    public string SecretHash { get; set; } = "";
    /// <summary>Cakupan, mis. "doc:PurchaseRequest:read", "doc:*:list", "method:sisa-anggaran". Kosong = semua sesuai role akun.</summary>
    public List<string> Scopes { get; set; } = [];
    /// <summary>IP atau CIDR yang boleh memakai kunci. Kosong = semua.</summary>
    public List<string> AllowedIps { get; set; } = [];
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }

    public string Status(DateTimeOffset now) => RevokedAt is not null ? "Dicabut" : ExpiresAt is { } e && e <= now ? "Kedaluwarsa" : "Aktif";
}

public interface IApiKeyStore
{
    Task<ApiKey?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKey>> ListAsync(string tenantId, CancellationToken ct = default);
    Task SaveAsync(ApiKey key, CancellationToken ct = default);
}

public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<string, string> _keys = new();
    private static ApiKey Read(string j) => JsonSerializer.Deserialize<ApiKey>(j)!;
    public Task<ApiKey?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(_keys.TryGetValue(id, out var j) ? Read(j) : null);
    public Task<IReadOnlyList<ApiKey>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApiKey>>(_keys.Values.Select(Read).Where(k => k.TenantId == tenantId).OrderByDescending(k => k.CreatedAt).ToList());
    public Task SaveAsync(ApiKey key, CancellationToken ct = default) { _keys[key.Id] = JsonSerializer.Serialize(key); return Task.CompletedTask; }
}

public static class ApiScopes
{
    public static readonly string[] DocOperations = ["list", "read", "create", "update", "delete", "action"];
    private static readonly Regex Valid = new(@"^(\*|doc:(\*|[A-Z][A-Za-z0-9]*):(\*|list|read|create|update|delete|action)|method:(\*|[a-z0-9][a-z0-9-]*)|report:(\*|[a-z0-9_]+)|query:(\*|[a-z0-9][a-z0-9-]*))$");

    public static bool IsValid(string scope) => Valid.IsMatch(scope);

    /// <summary>Apakah salah satu cakupan yang diberikan mencakup cakupan yang dibutuhkan (wildcard per segmen).</summary>
    public static bool Allows(IEnumerable<string> granted, string required)
    {
        var need = required.Split(':');
        foreach (var g in granted)
        {
            if (g == "*") return true;
            var have = g.Split(':');
            if (have.Length != need.Length) continue;
            if (have.Zip(need).All(p => p.First == "*" || p.First.Equals(p.Second, StringComparison.Ordinal))) return true;
        }
        return false;
    }
}

public sealed record CreateApiKeyRequest(string Name, string UserId, IReadOnlyList<string> Scopes, IReadOnlyList<string> AllowedIps, DateTimeOffset? ExpiresAt);
public sealed record ApiKeyVerification(ApiKey Key, UserAccount Account);

public sealed class ApiKeyException(string message) : Exception(message);

public sealed class ApiKeyService(IApiKeyStore store, IUserAccountRepository accounts, TimeProvider clock)
{
    public const string TokenPrefix = "dfk_";

    private static void DemandAdmin(UserContext u)
    {
        if (!u.Roles.Contains(UserContext.SystemManager)) throw new PermissionDeniedException("Managing API keys is restricted to System Manager.");
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Membuat kunci; token lengkap dikembalikan SEKALI dan tidak bisa diambil lagi.</summary>
    public async Task<(ApiKey Key, string Token)> CreateAsync(UserContext admin, CreateApiKeyRequest req, CancellationToken ct = default)
    {
        DemandAdmin(admin);
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiKeyException("API key name is required.");
        var account = await accounts.FindByIdAsync(req.UserId, ct);
        if (account is null || account.TenantId != admin.TenantId) throw new ApiKeyException("Account not found in this tenant.");
        if (!account.IsActive) throw new ApiKeyException("Inactive accounts cannot be granted API keys.");
        foreach (var s in req.Scopes) if (!ApiScopes.IsValid(s)) throw new ApiKeyException($"Invalid scope '{s}'.");
        foreach (var ip in req.AllowedIps) if (!IPNetwork.TryParse(ip.Contains('/') ? ip : ip + (ip.Contains(':') ? "/128" : "/32"), out _)) throw new ApiKeyException($"Invalid IP/CIDR '{ip}'.");
        if (req.ExpiresAt is { } e && e <= clock.GetUtcNow()) throw new ApiKeyException("Expiration date must be in the future.");

        var id = "k" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Id = id, TenantId = admin.TenantId, UserId = account.Id, Name = req.Name.Trim(), SecretHash = Hash(secret),
            Scopes = req.Scopes.Distinct().ToList(), AllowedIps = req.AllowedIps.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList(),
            CreatedBy = admin.Id, CreatedAt = clock.GetUtcNow(), ExpiresAt = req.ExpiresAt,
        };
        await store.SaveAsync(key, ct);
        return (key, $"{TokenPrefix}{id}.{secret}");
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(UserContext admin, CancellationToken ct = default)
    {
        DemandAdmin(admin);
        return await store.ListAsync(admin.TenantId, ct);
    }

    public async Task RevokeAsync(UserContext admin, string id, CancellationToken ct = default)
    {
        DemandAdmin(admin);
        var key = await store.GetAsync(id, ct);
        if (key is null || key.TenantId != admin.TenantId) throw new ApiKeyException("API key not found.");
        key.RevokedAt ??= clock.GetUtcNow();
        await store.SaveAsync(key, ct);
    }

    /// <summary>
    /// Verifikasi token pada setiap permintaan. Null bila format salah, kunci tidak ada / dicabut / kedaluwarsa,
    /// IP tidak diizinkan, secret salah (dibandingkan waktu-konstan), atau akun nonaktif / pindah tenant.
    /// </summary>
    public async Task<ApiKeyVerification?> VerifyAsync(string token, IPAddress? remoteIp, CancellationToken ct = default)
    {
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        var parts = token[TokenPrefix.Length..].Split('.', 2);
        if (parts.Length != 2 || parts[0].Length is < 2 or > 40 || parts[1].Length is < 20 or > 100) return null;
        var key = await store.GetAsync(parts[0], ct);
        if (key is null) return null;
        var now = clock.GetUtcNow();
        if (key.RevokedAt is not null || key.ExpiresAt is { } e && e <= now) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(parts[1])), Encoding.ASCII.GetBytes(key.SecretHash))) return null;
        if (key.AllowedIps.Count > 0 && (remoteIp is null || !key.AllowedIps.Any(a => Matches(a, remoteIp)))) return null;
        var account = await accounts.FindByIdAsync(key.UserId, ct);
        if (account is not { IsActive: true } || account.TenantId != key.TenantId) return null;

        // Catatan pemakaian (dibatasi per menit agar tidak menulis di setiap permintaan).
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1) || key.LastUsedIp != remoteIp?.ToString())
        {
            key.LastUsedAt = now; key.LastUsedIp = remoteIp?.ToString();
            await store.SaveAsync(key, ct);
        }
        return new ApiKeyVerification(key, account);
    }

    private static bool Matches(string allowed, IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var cidr = allowed.Contains('/') ? allowed : allowed + (allowed.Contains(':') ? "/128" : "/32");
        return IPNetwork.TryParse(cidr, out var net) && net.Contains(ip);
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
