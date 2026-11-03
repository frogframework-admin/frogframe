using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Navigation;
using Framework.Security;

namespace Framework.Web.Services;

public enum AwesomebarCategory
{
    Navigation,
    Action,
    Document,
    Report,
    Command
}

public sealed record AwesomebarItem(
    string Id,
    string Title,
    string? Subtitle,
    AwesomebarCategory Category,
    string? Href,
    string? Icon,
    string? Badge = null,
    string? ActionKey = null
);

public sealed class GlobalSearchService(
    IMetadataProvider metadata,
    IPermissionService permissions,
    INavigationMenuStore menuStore,
    IEnumerable<DeskMenuItem> menuItems,
    ReportService reports,
    DocumentService documents)
{
    public async Task<IReadOnlyList<AwesomebarItem>> SearchAsync(UserContext user, string? query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        var results = new List<AwesomebarItem>();

        var readableDocTypes = metadata.Current.DocTypes.Values
            .Where(d => !d.Def.IsChild && permissions.Can(user, d, DocActions.Read))
            .OrderBy(d => d.Def.Module)
            .ThenBy(d => d.Title)
            .ToList();

        var navModules = (await menuStore.ListModulesAsync(user.TenantId, onlyActive: true, ct))
            .Where(m => m.IsVisibleFor(user))
            .ToList();

        var navMenus = (await menuStore.ListAsync(user.TenantId, onlyActive: true, ct))
            .Where(m => m.IsVisibleFor(user))
            .ToList();

        // 1. Jika query kosong: berikan saran cepat (DocTypes utama, Menu penting, dan Commands)
        if (string.IsNullOrEmpty(q))
        {
            // Top DocTypes
            foreach (var dt in readableDocTypes.Take(5))
            {
                results.Add(new AwesomebarItem(
                    Id: $"dt_{dt.Name}",
                    Title: dt.Title,
                    Subtitle: $"DocType · {dt.Def.Module ?? "Umum"}",
                    Category: AwesomebarCategory.Navigation,
                    Href: $"/app/{dt.Name}",
                    Icon: IconFor(dt.Name),
                    Badge: "DocType"
                ));
            }

            // Quick Menu Items
            results.Add(new AwesomebarItem(
                Id: "menu_inbox",
                Title: "Inbox",
                Subtitle: "Workflow & notifications",
                Category: AwesomebarCategory.Navigation,
                Href: "/inbox",
                Icon: "inbox",
                Badge: "App"
            ));

            if (user.IsAdmin)
            {
                results.Add(new AwesomebarItem(
                    Id: "menu_workflows",
                    Title: "Workflows",
                    Subtitle: "Workflow engine & state transitions",
                    Category: AwesomebarCategory.Navigation,
                    Href: "/admin/workflows",
                    Icon: "git-fork",
                    Badge: "Admin"
                ));
                results.Add(new AwesomebarItem(
                    Id: "menu_users",
                    Title: "Pengguna & Akun",
                    Subtitle: "User access & role management",
                    Category: AwesomebarCategory.Navigation,
                    Href: "/admin/users",
                    Icon: "users",
                    Badge: "Admin"
                ));
            }

            // Quick Commands
            results.Add(new AwesomebarItem(
                Id: "cmd_profile",
                Title: "My Profile",
                Subtitle: $"{user.FullName} ({user.Id})",
                Category: AwesomebarCategory.Command,
                Href: null,
                Icon: "user",
                Badge: "Action",
                ActionKey: "open_profile"
            ));
            results.Add(new AwesomebarItem(
                Id: "cmd_theme",
                Title: "Toggle Theme (Dark / Light)",
                Subtitle: "Switch application theme appearance",
                Category: AwesomebarCategory.Command,
                Href: null,
                Icon: "moon",
                Badge: "Action",
                ActionKey: "toggle_theme"
            ));

            return results;
        }

        // 2. Query berisi teks: Cocokkan Navigasi (DocTypes & New Records)
        foreach (var dt in readableDocTypes)
        {
            var titleMatch = dt.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
            var nameMatch = dt.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            var moduleMatch = (dt.Def.Module ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);

            if (titleMatch || nameMatch || moduleMatch)
            {
                // Navigasi ke daftar dokumen
                results.Add(new AwesomebarItem(
                    Id: $"dt_{dt.Name}",
                    Title: dt.Title,
                    Subtitle: $"{dt.Def.Module ?? "Umum"} · Document List",
                    Category: AwesomebarCategory.Navigation,
                    Href: $"/app/{dt.Name}",
                    Icon: IconFor(dt.Name),
                    Badge: "List"
                ));

                // Aksi buat baru jika memiliki izin Create
                if (permissions.Can(user, dt, DocActions.Create))
                {
                    results.Add(new AwesomebarItem(
                        Id: $"new_{dt.Name}",
                        Title: $"+ New {dt.Title}",
                        Subtitle: $"Create new draft {dt.Title.ToLower()}",
                        Category: AwesomebarCategory.Action,
                        Href: $"/app/{dt.Name}/new",
                        Icon: "plus-circle",
                        Badge: "Create"
                    ));
                }
            }
            else if (q.StartsWith("new ", StringComparison.OrdinalIgnoreCase))
            {
                var cleanQ = q[4..].Trim();
                if (dt.Title.Contains(cleanQ, StringComparison.OrdinalIgnoreCase) || dt.Name.Contains(cleanQ, StringComparison.OrdinalIgnoreCase))
                {
                    if (permissions.Can(user, dt, DocActions.Create))
                    {
                        results.Add(new AwesomebarItem(
                            Id: $"new_{dt.Name}",
                            Title: $"+ New {dt.Title}",
                            Subtitle: $"Create new draft {dt.Title.ToLower()}",
                            Category: AwesomebarCategory.Action,
                            Href: $"/app/{dt.Name}/new",
                            Icon: "plus-circle",
                            Badge: "Create"
                        ));
                    }
                }
            }
        }

        // 3. Cocokkan Menu Navigasi (Custom menus & built-in Desk items)
        foreach (var m in navMenus)
        {
            if (string.IsNullOrEmpty(m.Href)) continue;
            if (m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (m.GroupName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new AwesomebarItem(
                    Id: $"nav_{m.Id}",
                    Title: m.Title,
                    Subtitle: $"Menu · {m.GroupName ?? "General"}",
                    Category: AwesomebarCategory.Navigation,
                    Href: "/" + m.Href.TrimStart('/'),
                    Icon: string.IsNullOrWhiteSpace(m.Icon) ? "compass" : m.Icon,
                    Badge: "Menu"
                ));
            }
        }

        foreach (var item in menuItems.Where(i => i.RequiresReadOf is null || (metadata.Current.TryGet(i.RequiresReadOf, out var m) && permissions.Can(user, m, DocActions.Read))))
        {
            if (item.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || item.Group.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                var href = "/" + item.Href.TrimStart('/');
                if (!results.Any(r => string.Equals(r.Href, href, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new AwesomebarItem(
                        Id: $"desk_{item.Href}",
                        Title: item.Title,
                        Subtitle: $"Desk · {item.Group}",
                        Category: AwesomebarCategory.Navigation,
                        Href: href,
                        Icon: string.IsNullOrWhiteSpace(item.Icon) ? "file-text" : item.Icon,
                        Badge: "Desk"
                    ));
                }
            }
        }

        // 4. Laporan Terdaftar
        foreach (var rep in reports.AvailableFor(user))
        {
            if (rep.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || rep.DocType.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new AwesomebarItem(
                    Id: $"report_{rep.Name}",
                    Title: rep.Name,
                    Subtitle: $"Report · Based on {rep.DocType}",
                    Category: AwesomebarCategory.Report,
                    Href: $"/reports/{Uri.EscapeDataString(rep.Name)}",
                    Icon: "bar-chart-2",
                    Badge: "Report"
                ));
            }
        }

        // 5. Modul Sistem (Switch Module)
        foreach (var mod in navModules)
        {
            if (mod.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new AwesomebarItem(
                    Id: $"mod_{mod.Id}",
                    Title: $"Switch to {mod.Name} Module",
                    Subtitle: "Change active navigation workspace module",
                    Category: AwesomebarCategory.Command,
                    Href: null,
                    Icon: string.IsNullOrWhiteSpace(mod.Icon) ? "layout-grid" : mod.Icon,
                    Badge: "Module",
                    ActionKey: $"switch_mod:{mod.Id}"
                ));
            }
        }

        // 6. Commands Sistem
        if ("theme dark light mode gelap terang".Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new AwesomebarItem(
                Id: "cmd_theme",
                Title: "Toggle Theme (Dark / Light)",
                Subtitle: "Switch application theme appearance",
                Category: AwesomebarCategory.Command,
                Href: null,
                Icon: "moon",
                Badge: "Action",
                ActionKey: "toggle_theme"
            ));
        }

        if ("profile user akun me saya".Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new AwesomebarItem(
                Id: "cmd_profile",
                Title: "My Profile",
                Subtitle: $"{user.FullName} ({user.Id})",
                Category: AwesomebarCategory.Command,
                Href: null,
                Icon: "user",
                Badge: "Action",
                ActionKey: "open_profile"
            ));
        }

        if ("logout keluar sign out signout".Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new AwesomebarItem(
                Id: "cmd_logout",
                Title: "Log Out",
                Subtitle: "Sign out of your current session",
                Category: AwesomebarCategory.Command,
                Href: "/account/logout",
                Icon: "log-out",
                Badge: "Session"
            ));
        }

        // 7. Live Record Search di Database (bila panjang kata kunci >= 2 karakter)
        if (q.Length >= 2)
        {
            var searchTargets = readableDocTypes
                .OrderByDescending(d => d.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList();

            var recordTasks = searchTargets.Select(async dt =>
            {
                try
                {
                    var res = await documents.ListAsync(user, dt.Name, new ListRequest(Search: q, PageSize: 3), ct);
                    return (DocType: dt, res.Items);
                }
                catch
                {
                    return (DocType: dt, Items: (IReadOnlyList<IReadOnlyDictionary<string, object?>>)[]);
                }
            });

            var searchResponses = await Task.WhenAll(recordTasks);

            foreach (var (dt, items) in searchResponses)
            {
                foreach (var row in items)
                {
                    var name = row.TryGetValue(StandardFields.Name, out var nameVal) ? nameVal?.ToString() : null;
                    if (string.IsNullOrEmpty(name)) continue;

                    var titleVal = row.TryGetValue("_title", out var tv) && tv is not null && !string.Equals(tv.ToString(), name)
                        ? tv.ToString()
                        : null;

                    var docStatusText = row.TryGetValue(StandardFields.DocStatus, out var dsVal) && dsVal is int ds
                        ? ds switch { 1 => "Submitted", 2 => "Cancelled", _ => "Draft" }
                        : null;

                    var subtitle = $"{dt.Title}{(titleVal is not null ? $" · {titleVal}" : "")}";

                    results.Add(new AwesomebarItem(
                        Id: $"rec_{dt.Name}_{name}",
                        Title: name,
                        Subtitle: subtitle,
                        Category: AwesomebarCategory.Document,
                        Href: $"/app/{dt.Name}/{Uri.EscapeDataString(name)}",
                        Icon: IconFor(dt.Name),
                        Badge: docStatusText ?? dt.Title
                    ));
                }
            }
        }

        return results;
    }

    public static string IconFor(string docTypeName) => docTypeName switch
    {
        "Employee" => "users",
        "Department" => "building",
        "Vendor" => "building-2",
        "Product" => "package",
        "PurchaseRequest" => "shopping-cart",
        _ => "file-text"
    };
}
