using System.Globalization;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Home;

/// <summary>A direct report who has no recent 1:1 and none scheduled soon.</summary>
public sealed record CheckInDue(string Employee, string FullName, DateOnly? LastCheckIn, int? DaysSinceCheckIn, int OverdueActions, int OpenGoals);

/// <summary>
/// Rules for the 1:1 cadence, shared by the home panel and the reminder job. A report is due when there is no completed
/// 1:1 in the last <see cref="CadenceDays"/> days and no scheduled 1:1 within <see cref="ScheduledWindowDays"/> days.
/// </summary>
public static class CheckInRules
{
    public const int CadenceDays = 30;
    public const int ScheduledWindowDays = 14;

    public static bool IsDue(DateOnly today, DateOnly? lastCompleted, DateOnly? nextScheduled)
    {
        if (nextScheduled is { } next && next >= today && next.DayNumber - today.DayNumber <= ScheduledWindowDays) return false;
        return lastCompleted is not { } last || today.DayNumber - last.DayNumber > CadenceDays;
    }

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), FieldTypeSettings.DefaultTimeZone).DateTime);

    /// <summary>Local date and time of a Datetime value (stored in UTC).</summary>
    public static DateTimeOffset? ParseMeeting(string? value) =>
        value is { Length: > 0 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? TimeZoneInfo.ConvertTime(dt, FieldTypeSettings.DefaultTimeZone)
            : null;

    /// <summary>Last completed 1:1, next scheduled 1:1 on or after today, and open action items past their due date.</summary>
    public static (DateOnly? Last, DateOnly? Next, int OverdueActions) Summarize(IEnumerable<Document> meetings, DateOnly today)
    {
        DateOnly? last = null, next = null;
        var overdue = 0;
        foreach (var x in meetings)
        {
            var at = ParseMeeting(x.Data.Str("meeting_at"));
            var day = at is null ? (DateOnly?)null : DateOnly.FromDateTime(at.Value.DateTime);
            if (x.Data.Str("status") == "Completed" && day is { } done && (last is null || done > last)) last = done;
            if (x.Data.Str("status") == "Scheduled" && day is { } plan && plan >= today && (next is null || plan < next)) next = plan;
            overdue += x.Data.Rows("action_items").Count(a => a.Str("status") != "Done" && a.Date("due_date") is { } due && due < today);
        }
        return (last, next, overdue);
    }
}

/// <summary>
/// "Check-ins due" for managers: direct reports (active, reports_to = reader) with no recent 1:1, most overdue first.
/// Reads go straight to the store; nothing here changes data.
/// </summary>
public sealed class CheckInDueService(IDocumentStore store, TimeProvider clock)
{
    public async Task<IReadOnlyList<CheckInDue>> DueAsync(UserContext user, CancellationToken ct = default)
    {
        var me = ErpHomeService.EmployeeOf(user);
        if (me is null) return [];
        var m = new Masters(store, user.TenantId);
        var today = CheckInRules.Today(clock);

        var directs = (await m.QueryAsync("Employee", [new FilterClause("reports_to", "=", me)], ct, "full_name", "asc", TeamHomeService.MaxDirectReports))
            .Where(e => e.Data.Str("status") != Hr.Left && e.Name != me).ToList();
        if (directs.Count == 0) return [];
        var ids = directs.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        // Only this manager's check-ins and their reports' goals (not the whole tenant, which would be cut off at `take`).
        var meetings = (await m.QueryAsync("OneOnOne", [new FilterClause("manager", "=", me)], ct, "meeting_at", "desc", 5000))
            .Where(x => ids.Contains(x.Data.Str("employee") ?? "")).ToList();
        var goals = (await m.QueryAsync("OkrObjective", [new FilterClause("reports_to", "=", me), new FilterClause("status", "=", "Active")], ct, take: 5000))
            .Where(g => ids.Contains(g.Data.Str("employee") ?? "")).ToList();

        var result = new List<CheckInDue>();
        foreach (var e in directs)
        {
            var (last, next, overdue) = CheckInRules.Summarize(meetings.Where(x => x.Data.Str("employee") == e.Name), today);
            if (!CheckInRules.IsDue(today, last, next)) continue;
            int? days = last is { } l ? today.DayNumber - l.DayNumber : null;
            result.Add(new(e.Name, e.Data.Str("full_name") ?? e.Name, last, days, overdue, goals.Count(g => g.Data.Str("employee") == e.Name)));
        }
        return result.OrderByDescending(r => r.DaysSinceCheckIn ?? int.MaxValue).ThenBy(r => r.FullName, StringComparer.CurrentCulture).ToList();
    }
}
