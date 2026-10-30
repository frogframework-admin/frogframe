using System.Collections.Concurrent;
using System.Security.Cryptography;
using Framework.Reports;

namespace Framework.Web;

/// <summary>
/// File laporan yang sudah dibuat di sirkuit Blazor diambil browser lewat /report-downloads/{token}: token acak,
/// sekali pakai, terikat pengguna & tenant, kedaluwarsa 10 menit. File besar tidak dikirim lewat SignalR dan
/// parameter laporan tidak muncul di URL.
/// </summary>
public sealed class ReportDownloads(TimeProvider clock)
{
    private sealed record Item(string TenantId, string UserId, ReportOutput Output, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Item> _items = new();

    public string Put(string tenantId, string userId, ReportOutput output)
    {
        var now = clock.GetUtcNow();
        foreach (var kv in _items.Where(kv => kv.Value.Expires < now).ToList()) _items.TryRemove(kv.Key, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _items[token] = new Item(tenantId, userId, output, now.AddMinutes(10));
        return token;
    }

    public ReportOutput? Take(string token, string tenantId, string userId)
    {
        if (!_items.TryRemove(token, out var item)) return null;
        return item.Expires >= clock.GetUtcNow() && item.TenantId == tenantId && item.UserId == userId ? item.Output : null;
    }
}
