using Framework.Dashboards;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Framework.Social;
using Framework.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Web.Home;

/// <summary>A document waiting for the user's workflow action, with the actions as the form toolbar would show them.</summary>
public sealed record HomeApprovalItem(
    CompiledDocType Meta,
    Document Doc,
    IReadOnlyList<ActionView> Actions,
    string Group,
    string? Requester,
    string? Summary,
    string? Amount,
    DateTimeOffset WaitingSince);

/// <summary>One number from a "single number" widget of the user's default dashboard.</summary>
public sealed record HomeKpi(string Title, string Value, string Href);

/// <summary>Time helpers for the home page: always the application time zone, never the server's.</summary>
public static class HomeClock
{
    /// <summary>Now in <see cref="FieldTypeSettings.DefaultTimeZone"/> (Asia/Jakarta unless configured).</summary>
    public static DateTimeOffset LocalNow(TimeProvider clock) => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), FieldTypeSettings.DefaultTimeZone);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(LocalNow(clock).DateTime);

    /// <summary>
    /// Translation key of the greeting for an hour of the day. Four periods, matching Indonesian usage:
    /// pagi (04–10), siang (11–14), sore (15–17), malam (18–03). English shows "Good afternoon" for both siang and sore.
    /// </summary>
    public static string GreetingKey(int hour) => hour switch
    {
        >= 4 and < 11 => "Good morning",
        >= 11 and < 15 => "Good afternoon",
        >= 15 and < 18 => "Good late afternoon",
        _ => "Good evening",
    };

    /// <summary>"Waiting" age bucket: fresh (under 1 day), aging (1–2 days), stale (3 days or more).</summary>
    public static string AgeTone(DateTimeOffset since, DateTimeOffset now) => (now - since).TotalDays switch
    {
        < 1 => "fresh",
        < 3 => "aging",
        _ => "stale",
    };
}

/// <summary>
/// Data for the home page: the approval queue (with inline workflow actions), KPIs from the default dashboard,
/// running background jobs and saved documents. Each source fails soft: an unavailable source returns an empty list.
/// </summary>
public sealed class HomeFeed(DocumentService docs, IServiceProvider services)
{
    private static readonly string[] RequesterFields = ["employee_name", "requester_name", "customer_name", "supplier_name", "party_name", "requested_by"];
    private static readonly string[] AmountFields = ["grand_total", "total_claimed", "advance_amount", "total_amount", "net_pay", "total", "amount"];

    /// <summary>Documents waiting for this user's workflow action, oldest first.</summary>
    public async Task<IReadOnlyList<HomeApprovalItem>> ApprovalQueueAsync(UserContext user, CancellationToken ct = default)
    {
        var pending = await docs.PendingActionsAsync(user, ct);
        return pending.Where(p => NeedsDecision(p.Meta, p.Doc, p.Actions))
            .Select(p => ToItem(p.Meta, p.Doc, p.Actions)).OrderBy(i => i.WaitingSince).ToList();
    }

    /// <summary>
    /// "Needs your action" lists decisions, not every document the user could touch: documents still in the workflow's
    /// initial state (the user's own drafts, shown under recent documents) and documents whose only actions cancel them
    /// (for example a requester's "Withdraw") are left out.
    /// </summary>
    internal static bool NeedsDecision(CompiledDocType meta, Document doc, IReadOnlyList<string> actions)
    {
        if (meta.Workflow is not { } wf) return actions.Count > 0;
        var state = doc.WorkflowState ?? wf.Def.InitialState;
        if (string.Equals(state, wf.Def.InitialState, StringComparison.Ordinal)) return false;
        return actions.Any(a => wf.Transitions.Where(t => t.Def.Action == a && t.Def.From == state)
            .Any(t => !wf.States.TryGetValue(t.Def.To, out var to) || to.DocStatus != DocStatus.Cancelled));
    }

    internal static HomeApprovalItem ToItem(CompiledDocType meta, Document doc, IReadOnlyList<string> actionNames)
    {
        var wf = meta.Workflow;
        var actions = actionNames.Distinct(StringComparer.Ordinal).Select(name =>
        {
            var t = wf?.Transitions.FirstOrDefault(x => x.Def.Action == name && x.Def.From == doc.WorkflowState)
                    ?? wf?.Transitions.FirstOrDefault(x => x.Def.Action == name);
            if (t is null) return new ActionView(name, "", true, "neutral");
            var tone = wf!.States.TryGetValue(t.Def.To, out var st) ? st.Tone : "neutral";
            return new ActionView(name, t.Def.To, t.Def.RequireComment, tone);
        }).ToList();

        var (requester, summary, amount) = Describe(meta, doc);
        var group = string.IsNullOrWhiteSpace(meta.Def.Module) ? "Other" : meta.Def.Module!;
        return new HomeApprovalItem(meta, doc, actions, group, requester, summary, amount, doc.Modified);
    }

    /// <summary>Who the document is about (employee, requester, party), its title and its main amount, when the DocType has them.</summary>
    public static (string? Party, string? Summary, string? Amount) Describe(CompiledDocType meta, Document doc)
    {
        string? party = null;
        foreach (var f in RequesterFields)
            if (meta.ByName.ContainsKey(f) && doc.Get(f) is { } v && v.ToString() is { Length: > 0 } s) { party = s; break; }

        string? summary = null;
        if (meta.Def.ListView.TitleField is { } tf && doc.Get(tf) is { } tv && Fmt.Value(meta.ByName.GetValueOrDefault(tf), tv) is { Length: > 0 } ts
            && ts != "—" && ts != party && ts != doc.Name) summary = ts;

        string? amount = null;
        foreach (var f in AmountFields)
            if (meta.ByName.TryGetValue(f, out var field) && field.Def.Type == "Currency" && doc.Get(f) is { } av)
            {
                amount = Fmt.Value(field, av);
                break;
            }
        return (party, summary, amount);
    }

    /// <summary>Runs a workflow action on a queue item. Version checking protects against a document changed meanwhile.</summary>
    public Task<Document> RunAsync(UserContext user, HomeApprovalItem item, string action, string? comment, CancellationToken ct = default) =>
        docs.RunActionAsync(user, item.Meta.Name, item.Doc.Name, action, item.Doc.Version, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), ct);

    /// <summary>
    /// Values of the "single number" widgets on the user's default dashboard (the first one marked default that the
    /// user may view), in layout order. Empty when reporting is not configured or no such widget exists.
    /// </summary>
    public async Task<IReadOnlyList<HomeKpi>> KpisAsync(UserContext user, int max = 4, CancellationToken ct = default)
    {
        if (services.GetService<DashboardService>() is not { } dashboards) return [];
        try
        {
            var list = await dashboards.ListDashboardsAsync(user, ct);
            var dash = list.FirstOrDefault(d => d.IsDefault) ?? list.FirstOrDefault(d => d.Id == DashboardDef.DefaultDashboardId);
            if (dash is null) return [];
            var result = new List<HomeKpi>();
            foreach (var item in dash.Layout.Items)
            {
                if (result.Count >= max) break;
                var widget = await dashboards.GetAsync(user, item.WidgetId, ct);
                if (widget is null || widget.Config.Visual.Type != ChartTypes.Number) continue;
                var run = await dashboards.RunWidgetAsync(user, widget, new DashboardFilters(null, null, null), ct);
                if (run.Result is not { Rows.Count: > 0 } r) continue;
                var col = widget.Config.Visual.YColumns.FirstOrDefault();
                var idx = col is null ? 0 : Math.Max(0, r.Columns.ToList().FindIndex(c => c.Name == col));
                var raw = idx < r.Rows[0].Length ? r.Rows[0][idx] : null;
                result.Add(new HomeKpi(widget.Name, FormatNumber(raw), $"/dashboard/{Uri.EscapeDataString(dash.Id)}"));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // KPIs are a convenience: never break the home page.
        }
    }

    internal static string FormatNumber(object? raw) => raw switch
    {
        null => "—",
        decimal d => d.ToString(d == Math.Truncate(d) ? "N0" : "N2", System.Globalization.CultureInfo.CurrentCulture),
        double d => ((decimal)d).ToString(Math.Abs(d % 1) < double.Epsilon ? "N0" : "N2", System.Globalization.CultureInfo.CurrentCulture),
        int or long or short => Convert.ToDecimal(raw).ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        _ => raw.ToString() ?? "—",
    };

    /// <summary>The user's queued and running background jobs.</summary>
    public async Task<IReadOnlyList<BackgroundTask>> ActiveJobsAsync(UserContext user, int max = 3, CancellationToken ct = default)
    {
        if (services.GetService<BackgroundTaskService>() is not { } tasks) return [];
        var all = await tasks.ListAsync(user, allUsers: false, take: 50, ct);
        return all.Where(j => j.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued).Take(max).ToList();
    }

    /// <summary>Bookmarked and followed documents, newest first, without duplicates.</summary>
    public async Task<IReadOnlyList<SavedDocument>> SavedAsync(UserContext user, int max = 5, CancellationToken ct = default)
    {
        if (services.GetService<DocSocialService>() is not { } social) return [];
        var bookmarksTask = social.BookmarksAsync(user, ct);
        var followingTask = social.FollowingAsync(user, ct);
        await Task.WhenAll(bookmarksTask, followingTask);
        return (await bookmarksTask).Concat(await followingTask)
            .GroupBy(d => (d.DocType, d.Name)).Select(g => g.First())
            .OrderByDescending(d => d.At).Take(max).ToList();
    }
}
