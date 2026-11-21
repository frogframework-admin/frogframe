using Framework.Metadata;
using Framework.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Web.Home;

/// <summary>Where a registered panel is rendered on the home page (<c>/</c>).</summary>
public enum HomeSlot
{
    /// <summary>Full width, above everything else (setup checklists, important banners).</summary>
    Top,

    /// <summary>The user's own day: shift, leave balance, own requests. Shown first for users without approvals waiting.</summary>
    Personal,

    /// <summary>Main column, after the framework's own sections.</summary>
    Main,

    /// <summary>Right-hand column (company information, shortcuts).</summary>
    Side,
}

/// <summary>
/// A Blazor component an application adds to the home page. The component receives no parameters; it injects what it
/// needs (for example <see cref="CurrentUser"/>). <paramref name="Visible"/> decides per user whether it is rendered.
/// </summary>
public sealed record HomePanel(string Id, Type Component, HomeSlot Slot, int Order = 100, Func<UserContext, bool>? Visible = null);

/// <summary>
/// A "Create new" shortcut in the home page hero. With <paramref name="DocType"/> the shortcut is shown only when the
/// DocType exists and the user may create it; with <paramref name="Roles"/> only to users holding one of the roles.
/// </summary>
public sealed record HomeQuickAction(string Id, string Label, string Href, string Icon = "plus", string? DocType = null,
    IReadOnlyList<string>? Roles = null, int Order = 100);

public static class HomeServiceCollectionExtensions
{
    /// <summary>Adds a panel to the home page. Ids must be unique; a later registration with the same id replaces the earlier one.</summary>
    public static IServiceCollection AddHomePanel<TComponent>(this IServiceCollection services, string id, HomeSlot slot, int order = 100,
        Func<UserContext, bool>? visible = null) where TComponent : IComponent
    {
        services.AddSingleton(new HomePanel(id, typeof(TComponent), slot, order, visible));
        return services;
    }

    /// <summary>Adds a "Create new" shortcut to the home page hero.</summary>
    public static IServiceCollection AddHomeQuickAction(this IServiceCollection services, HomeQuickAction action)
    {
        services.AddSingleton(action);
        return services;
    }

    /// <summary>Shortcut for a "new document" action: <c>/app/{docType}/new</c>, shown when the user may create the DocType.</summary>
    public static IServiceCollection AddHomeQuickAction(this IServiceCollection services, string docType, string label, string icon = "plus", int order = 100) =>
        services.AddHomeQuickAction(new HomeQuickAction(docType, label, $"/app/{Uri.EscapeDataString(docType)}/new", icon, docType, null, order));
}

/// <summary>Resolves the panels and quick actions registered by applications for one user.</summary>
public sealed class HomeRegistry(IEnumerable<HomePanel> panels, IEnumerable<HomeQuickAction> actions, IMetadataProvider metadata, IPermissionService permissions)
{
    private readonly IReadOnlyList<HomePanel> _panels = panels.GroupBy(p => p.Id).Select(g => g.Last()).ToList();
    private readonly IReadOnlyList<HomeQuickAction> _actions = actions.GroupBy(a => a.Id).Select(g => g.Last()).ToList();

    public IReadOnlyList<HomePanel> PanelsFor(UserContext user, HomeSlot slot) =>
        _panels.Where(p => p.Slot == slot && (p.Visible is null || p.Visible(user)))
            .OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<HomeQuickAction> QuickActionsFor(UserContext user, int max = 6) =>
        _actions.Where(a => Allowed(user, a)).OrderBy(a => a.Order).ThenBy(a => a.Label, StringComparer.Ordinal).Take(max).ToList();

    private bool Allowed(UserContext user, HomeQuickAction a)
    {
        if (a.Roles is { Count: > 0 } roles && !roles.Any(user.HasRole)) return false;
        if (a.DocType is null) return true;
        return metadata.Current.TryGet(a.DocType, out var meta) && !meta.Def.IsChild && !meta.Def.IsSingle
               && permissions.Can(user, meta, DocActions.Create);
    }
}

/// <summary>Per-user home page state kept in <see cref="Framework.Preferences.IUserPreferenceStore"/>.</summary>
public static class HomePreferences
{
    /// <summary>Ids of banners and panels the user has hidden (for example "two-factor" or "erp-setup").</summary>
    public const string DismissedKey = "home.dismissed";

    public static async Task<HashSet<string>> DismissedAsync(Framework.Preferences.IUserPreferenceStore prefs, UserContext user, CancellationToken ct = default) =>
        (await prefs.GetAsync<List<string>>(user.TenantId, user.Id, DismissedKey, ct))?.ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);

    public static async Task DismissAsync(Framework.Preferences.IUserPreferenceStore prefs, UserContext user, string id, CancellationToken ct = default)
    {
        var set = await DismissedAsync(prefs, user, ct);
        if (set.Add(id)) await prefs.SetAsync(user.TenantId, user.Id, DismissedKey, set.OrderBy(x => x, StringComparer.Ordinal).ToList(), ct);
    }
}
