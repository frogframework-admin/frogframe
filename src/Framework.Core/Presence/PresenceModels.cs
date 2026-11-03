using Framework.Security;

namespace Framework.Presence;

/// <summary>Status aktivitas pengguna pada dokumen yang sedang dibuka.</summary>
public enum PresenceStatus
{
    /// <summary>Pengguna hanya membuka / melihat dokumen tanpa perubahan form yang belum disimpan.</summary>
    Viewing,
    /// <summary>Pengguna sedang mengedit (form berstatus kotor / dirty) dan belum menyimpan perubahan.</summary>
    Editing
}

/// <summary>Data sesi pengguna yang sedang aktif membuka suatu dokumen.</summary>
public sealed record DocumentPresenceUser(
    string SessionId,
    string TenantId,
    string DocType,
    string DocName,
    string UserId,
    string FullName,
    string? AvatarId,
    PresenceStatus Status,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastHeartbeat
);

/// <summary>Event perubahan daftar atau status pengguna aktif pada suatu dokumen.</summary>
public sealed record PresenceChange(
    string TenantId,
    string DocType,
    string DocName,
    IReadOnlyList<DocumentPresenceUser> ActiveUsers
);

/// <summary>Layanan pelacak kehadiran realtime per dokumen dan per tenant.</summary>
public interface IDocumentPresenceService
{
    /// <summary>
    /// Mencatat atau memperbarui heartbeat pengguna untuk dokumen tertentu.
    /// Otomatis membersihkan sesi yang kedaluwarsa dan memberitahu pelanggan (*subscribers*).
    /// </summary>
    ValueTask<IReadOnlyList<DocumentPresenceUser>> HeartbeatAsync(
        string tenantId,
        string docType,
        string docName,
        string sessionId,
        UserContext user,
        PresenceStatus status,
        CancellationToken ct = default);

    /// <summary>Mencatat pengguna keluar dari dokumen (mis. pindah halaman atau menutup tab).</summary>
    ValueTask LeaveAsync(
        string tenantId,
        string docType,
        string docName,
        string sessionId,
        CancellationToken ct = default);

    /// <summary>Mengambil daftar pengguna yang sedang aktif pada dokumen tertentu.</summary>
    IReadOnlyList<DocumentPresenceUser> GetActiveUsers(
        string tenantId,
        string docType,
        string docName);

    /// <summary>Berlangganan perubahan kehadiran pengguna pada dokumen tertentu.</summary>
    IDisposable Subscribe(
        string tenantId,
        string docType,
        string docName,
        Action<PresenceChange> onChange);
}

