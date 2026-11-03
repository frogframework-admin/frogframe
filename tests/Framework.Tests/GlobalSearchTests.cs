using Framework.Documents;
using Framework.Navigation;
using Framework.Security;
using Framework.Web.Services;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    [Test]
    public async Task GlobalSearch_suggestions_saat_query_kosong()
    {
        using var scope = sp.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<GlobalSearchService>();
        var user = U("dewi");

        var results = await search.SearchAsync(user, "");
        Assert.True(results.Count > 0, "hasil query kosong tidak boleh kosong");
        Assert.True(results.Any(r => r.Category == AwesomebarCategory.Navigation), "ada navigasi");
        Assert.True(results.Any(r => r.Category == AwesomebarCategory.Command && r.ActionKey == "toggle_theme"), "ada perintah theme");
        Assert.True(results.Any(r => r.Id == "menu_inbox"), "ada menu inbox");
    }

    [Test]
    public async Task GlobalSearch_navigasi_dan_new_record_berdasarkan_permission()
    {
        using var scope = sp.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<GlobalSearchService>();
        var dewi = U("dewi");

        var results = await search.SearchAsync(dewi, "purchase");
        Assert.True(results.Any(r => r.Category == AwesomebarCategory.Navigation && (r.Title == "Pengajuan Pembelian" || r.Id == "dt_PurchaseRequest")), "menemukan Purchase Request List");
        Assert.True(results.Any(r => r.Category == AwesomebarCategory.Action && r.Title.Contains("Pengajuan Pembelian")), "memiliki opsi New Purchase Request");
    }

    [Test]
    public async Task GlobalSearch_live_record_dan_isolasi_tenant()
    {
        using var scope = sp.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<GlobalSearchService>();
        var dewi = U("dewi"); // tenant acme

        var results = await search.SearchAsync(dewi, "Komputindo");
        var acmeDocs = results.Where(r => r.Category == AwesomebarCategory.Document).ToList();
        Assert.True(acmeDocs.Count > 0, "menemukan dokumen vendor PT Sinar Komputindo di acme");
        Assert.True(acmeDocs.All(d => d.Href!.StartsWith("/app/Vendor/")), "link menuju detail form dokumen");

        // Pengguna Globex tidak boleh menemukan data acme
        var tono = U("tono"); // tenant globex
        var globexResults = await search.SearchAsync(tono, "Komputindo");
        Assert.Equal(0, globexResults.Count(r => r.Category == AwesomebarCategory.Document), "data vendor acme tidak bocor ke tenant globex");
    }
}
