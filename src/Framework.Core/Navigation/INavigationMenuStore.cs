namespace Framework.Navigation;

public interface INavigationMenuStore
{
    /// <summary>Dipicu ketika data menu berubah (tambah, edit, hapus) agar UI sidebar dapat langsung memperbarui tampilan.</summary>
    event Action? Changed;

    /// <summary>Mengambil semua menu navigasi untuk tenant tertentu, terurut berdasarkan sort_order.</summary>
    Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default);

    /// <summary>Mengambil satu item menu berdasarkan id dan tenant.</summary>
    Task<NavigationMenuItem?> GetAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Menyimpan menu baru atau memperbarui menu yang sudah ada.</summary>
    Task SaveAsync(NavigationMenuItem item, CancellationToken ct = default);

    /// <summary>Menghapus menu kustom (menu dengan IsSystem=true tidak boleh dihapus).</summary>
    Task DeleteAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Memastikan menu default universal dan menu wajib sistem sudah tersedia di database untuk tenant ini.</summary>
    Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default);
}

