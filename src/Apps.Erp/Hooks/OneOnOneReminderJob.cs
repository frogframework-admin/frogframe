using Apps.Erp.Home;
using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

/// <summary>
/// Daily reminders for 1:1 check-ins, all sent to the inbox with a deterministic key (one per day and item):
/// the employee and manager the day before and on the day of a scheduled 1:1; the assignee of an action item on its
/// due date and every day after while it stays open; and on Mondays the manager of a direct report who is due for a 1:1
/// (see <see cref="CheckInRules"/>).
/// </summary>
public sealed class OneOnOneReminderJob : IScheduledJob
{
    public string Name => "Erp.OneOnOneReminders";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = context.Services.GetRequiredService<IDocumentStore>();
        var clock = context.Services.GetService<TimeProvider>() ?? TimeProvider.System;
        var users = context.Services.GetService<IUserDirectory>();
        var inbox = context.Services.GetService<INotificationStore>();
        if (users is null || inbox is null) return;

        var tenant = context.TenantId;
        var today = CheckInRules.Today(clock);
        var todayIso = D.Iso(today);
        var m = new Masters(store, tenant);
        // Newest first, so the cap keeps recent check-ins (last 1:1 per employee, open action items).
        var meetings = await m.QueryAsync("OneOnOne", [], ct, "meeting_at", "desc", 20000);

        foreach (var meet in meetings)
        {
            var emp = meet.Data.Str("employee");
            var boss = meet.Data.Str("manager");

            if (meet.Data.Str("status") == "Scheduled" && CheckInRules.ParseMeeting(meet.Data.Str("meeting_at")) is { } at)
            {
                var gap = DateOnly.FromDateTime(at.DateTime).DayNumber - today.DayNumber;
                if (gap is 0 or 1)
                {
                    var when = gap == 0 ? "today" : "tomorrow";
                    await Notify.ToEmployeesAsync(users, inbox, tenant, [emp, boss],
                        $"1:1 check-in {when} at {at:HH:mm}", "Open the 1:1 to add the agenda and your notes.",
                        "OneOnOne", meet.Name, $"1on1:{meet.Name}:{D.Iso(DateOnly.FromDateTime(at.DateTime))}:{when}", ct);
                }
            }

            var rows = meet.Data.Rows("action_items");
            for (var i = 0; i < rows.Count; i++)
            {
                var a = rows[i];
                if (a.Str("status") == "Done" || a.Date("due_date") is not { } due || due > today) continue;
                var subject = due < today ? $"Overdue action item: {a.Str("action")}" : $"Action item due today: {a.Str("action")}";
                await Notify.ToEmployeesAsync(users, inbox, tenant, [a.Str("assignee") ?? emp], subject,
                    "From your 1:1 check-in. Mark it Done in the check-in once finished.",
                    "OneOnOne", meet.Name, $"1on1:{meet.Name}:{i}:{todayIso}", ct);
            }
        }

        if (today.DayOfWeek != DayOfWeek.Monday) return;
        var employees = await m.QueryAsync("Employee", [], ct, take: 20000);
        foreach (var e in employees.Where(x => x.Data.Str("reports_to") is not null && x.Data.Str("status") != Hr.Left))
        {
            // Same rule as the home panel: only check-ins with the current manager count.
            var boss = e.Data.Str("reports_to");
            var (last, next, _) = CheckInRules.Summarize(meetings.Where(x => x.Data.Str("employee") == e.Name && x.Data.Str("manager") == boss), today);
            if (!CheckInRules.IsDue(today, last, next)) continue;
            var name = e.Data.Str("full_name") ?? e.Name;
            await Notify.ToEmployeesAsync(users, inbox, tenant, [boss],
                $"No 1:1 check-in with {name} in the last {CheckInRules.CadenceDays} days",
                "Schedule a 1:1 check-in with this direct report.",
                "OneOnOne", null, $"1on1:gap:{e.Name}:{todayIso}", ct);
        }
    }
}
