using Framework.Api;
using Framework.Dashboards;
using Framework.Security;

namespace Framework.Web.QueryApis;

/// <summary>
/// Opsi "Terbitkan juga sebagai API" di wizard widget dashboard & template laporan: query dan parameter yang sama
/// diterbitkan di /api/query/{slug} dan ikut diperbarui setiap kali sumbernya disimpan. Dicentang lalu dilepas = API dihapus.
/// </summary>
public sealed class ApiPublishOption
{
    public bool On { get; set; }
    public string Slug { get; set; } = "";
    public QueryApiDef? Linked { get; private set; }

    public async Task LoadAsync(QueryApiService apis, UserContext u, string? sourceRef)
    {
        if (sourceRef is null || !apis.IsAvailable || !QueryApiService.CanDesign(u)) return;
        Linked = await apis.GetBySourceAsync(u, sourceRef);
        On = Linked is not null;
        Slug = Linked?.Slug ?? "";
    }

    /// <summary>Menerapkan pilihan setelah sumber disimpan. Mengembalikan keterangan untuk notifikasi (null = tidak ada perubahan).</summary>
    public async Task<string?> ApplyAsync(QueryApiService apis, UserContext u, string sourceRef, string name, string? description,
        string sql, IReadOnlyList<WidgetParameter> parameters, IReadOnlyList<string> roles)
    {
        if (On)
        {
            var d = await apis.PublishFromAsync(u, sourceRef, Slug, name, description, sql, parameters, roles);
            Linked = d;
            return $"API /api/query/{d.Slug} diperbarui (v{d.Version}).";
        }
        if (Linked is null) return null;
        await apis.DeleteAsync(u, Linked.Id);
        Linked = null;
        return "API dari sumber ini dihapus.";
    }
}
