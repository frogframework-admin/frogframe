using System.Globalization;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

public sealed class Announcement { }

/// <summary>
/// Which announcements an employee sees on the Home page: published, inside the publish window, for the employee's
/// company (or all), and for the employee's unit (or all). With "include sub-units" an announcement for a unit also
/// reaches every unit below it. The employee's current company and unit are read from the Employee record, so a
/// transfer takes effect at once.
/// </summary>
internal static class Announcements
{
    public const int MaxShown = 5;

    public static async Task<IReadOnlyList<Document>> VisibleAsync(Masters m, string? employeeId, DateOnly today, CancellationToken ct)
    {
        var emp = string.IsNullOrEmpty(employeeId) ? null : await m.GetAsync("Employee", employeeId, ct);
        var company = emp?.Data.Str("company");
        var unitChain = await UnitChainAsync(m, emp?.Data.Str("organization_unit"), ct);

        var result = new List<Document>();
        foreach (var a in await m.QueryAsync("Announcement", [new FilterClause("is_published", "=", true)], ct, "publish_from", "desc", 200))
        {
            var d = a.Data;
            if (d.Date("publish_from") is { } from && from > today) continue;
            if (d.Date("publish_to") is { } to && to < today) continue;
            if (d.Str("target_company") is { } tc && !string.Equals(tc, company, StringComparison.Ordinal)) continue;
            if (d.Str("target_unit") is { } tu)
            {
                var reaches = unitChain.Count > 0 && (unitChain[0] == tu || (d.Bool("include_sub_units") && unitChain.Contains(tu)));
                if (!reaches) continue;
            }
            result.Add(a);
            if (result.Count >= MaxShown) break;
        }
        return result;
    }

    /// <summary>The unit and its parents, nearest first (max. 25 levels, stops on a cycle).</summary>
    public static async Task<List<string>> UnitChainAsync(Masters m, string? unit, CancellationToken ct)
    {
        var chain = new List<string>();
        while (!string.IsNullOrEmpty(unit) && !chain.Contains(unit) && chain.Count < 25)
        {
            chain.Add(unit);
            unit = await m.StrAsync("OrganizationUnit", unit, "parent_org_unit", ct);
        }
        return chain;
    }

    public static bool Acknowledged(Document announcement, string? employeeId) =>
        !string.IsNullOrEmpty(employeeId) && announcement.Data.Rows("acknowledgments").Any(r => r.Str("employee") == employeeId);

    public static bool NeedsAcknowledgment(Document announcement, string? employeeId) =>
        announcement.Data.Bool("require_acknowledgment") && !string.IsNullOrEmpty(employeeId) && !Acknowledged(announcement, employeeId);
}

/// <summary>Announcement: number of acknowledgments; acknowledgment rows come only from the "I have read this" action.</summary>
public sealed class AnnouncementHook : IBeforeValidate<Announcement>
{
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        if (!c.User.IsAdmin && c.Previous is not null)
            d["acknowledgments"] = c.Previous.Data.Rows("acknowledgments").Select(r => new Dictionary<string, object?>(r)).ToList();
        else if (!c.User.IsAdmin)
            d["acknowledgments"] = new List<Dictionary<string, object?>>();
        d["ack_count"] = (long)d.Rows("acknowledgments").Count;
        return Task.CompletedTask;
    }
}

/// <summary>"I have read this": records the employee once on a mandatory announcement.</summary>
public sealed class AnnouncementAcknowledgeAction(TimeProvider clock) : IFormAction<Announcement>
{
    public string ActionId => "erp.announcement.acknowledge";
    public string Label => "I have read this";
    public string Icon => "check";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) => Announcements.NeedsAcknowledgment(doc, user.Attr("employee"));

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var empId = ctx.User.Attr("employee");
        if (string.IsNullOrEmpty(empId)) return FormActionResult.Fail("Your account is not linked to an employee.");
        var sys = UserContext.System(ctx.Doc.TenantId);
        var m = new Masters(ctx.Services.GetRequiredService<IDocumentStore>(), ctx.Doc.TenantId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await ctx.Docs.GetAsync(sys, "Announcement", ctx.Doc.Name, ct);
            if (Announcements.Acknowledged(current, empId)) return FormActionResult.Ok("Already acknowledged.");
            var rows = current.Data.Rows("acknowledgments").Select(r => new Dictionary<string, object?>(r)).ToList();
            rows.Add(new Dictionary<string, object?>
            {
                ["employee"] = empId,
                ["employee_name"] = await m.StrAsync("Employee", empId, "full_name", ct),
                ["acknowledged_at"] = clock.GetUtcNow().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture),
            });
            try
            {
                await ctx.Docs.UpdateAsync(sys, "Announcement", current.Name, current.Version,
                    new Dictionary<string, object?> { ["acknowledgments"] = rows }, ct);
                return FormActionResult.Ok("Thank you, your acknowledgment was recorded.");
            }
            catch (ConcurrencyException) { /* someone else acknowledged at the same moment: read again */ }
        }
        return FormActionResult.Fail("Please try again.");
    }
}
