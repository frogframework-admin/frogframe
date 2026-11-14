using Framework.Api;
using Framework.Dashboards;
using Framework.Security;

namespace Framework.Web.QueryApis;

/// <summary>
/// Option to "Also publish as API" in the dashboard widget &amp; report template wizard: same query and parameters
/// published at /api/query/{slug} and updated whenever the source is saved. Checking then unchecking deletes the API.
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

    /// <summary>Applies options after the source is saved. Returns a notification message (null = no change).</summary>
    public async Task<string?> ApplyAsync(QueryApiService apis, UserContext u, string sourceRef, string name, string? description,
        string sql, IReadOnlyList<WidgetParameter> parameters, IReadOnlyList<string> roles)
    {
        if (On)
        {
            var d = await apis.PublishFromAsync(u, sourceRef, Slug, name, description, sql, parameters, roles);
            Linked = d;
            return $"API /api/query/{d.Slug} updated (v{d.Version}).";
        }
        if (Linked is null) return null;
        await apis.DeleteAsync(u, Linked.Id);
        Linked = null;
        return "API linked to this source was deleted.";
    }
}
