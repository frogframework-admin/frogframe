namespace Framework.Security;

public interface IRoleStore
{
    /// <summary>Dipicu ketika data master role berubah (tambah, edit, hapus).</summary>
    event Action? Changed;

    /// <summary>Mengambil daftar semua role untuk tenant tertentu.</summary>
    Task<IReadOnlyList<RoleDef>> ListAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Mengambil role berdasarkan Id.</summary>
    Task<RoleDef?> GetAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Mengambil role berdasarkan nama.</summary>
    Task<RoleDef?> GetByNameAsync(string name, string tenantId, CancellationToken ct = default);

    /// <summary>Menyimpan role baru atau memperbarui role yang sudah ada.</summary>
    Task SaveAsync(RoleDef role, CancellationToken ct = default);

    /// <summary>Menghapus role kustom (role dengan IsSystem=true tidak boleh dihapus).</summary>
    Task DeleteAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Memastikan role-role default (Administrator, User, HR, Purchasing, dll.) sudah di-seed untuk tenant ini.</summary>
    Task EnsureDefaultRolesAsync(string tenantId, CancellationToken ct = default);
}

