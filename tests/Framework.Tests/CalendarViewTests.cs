using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// List calendar view (P1-2): date field resolution, month/week/day ranges, entries from list rows (inclusive
/// Date ends, Datetime in the default zone), time-grid lanes, all-day row, colour slots and metadata validation.
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildCalendarProvider(string calendarJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-cal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CalTask.doctype.json"), $$"""
        {
          "name": "CalTask", "version": 1, "title": "Cal Task", "module": "CORE",
          "naming": { "pattern": "CT-{#####}" },
          "listView": { "titleField": "subject", "searchFields": ["name", "subject"], "columns": ["subject", "due_date", "priority"] {{calendarJson}} },
          "fields": [
            { "name": "subject", "type": "Data", "label": "Subject", "requiredWhen": "true" },
            { "name": "starts_at", "type": "Datetime", "label": "Starts at" },
            { "name": "ends_at", "type": "Datetime", "label": "Ends at" },
            { "name": "due_date", "type": "Date", "label": "Due date" },
            { "name": "until_date", "type": "Date", "label": "Until" },
            { "name": "priority", "type": "Select", "label": "Priority", "options": ["Low", "Medium", "High"] }
          ],
          "permissions": [ { "role": "Staff", "actions": ["read", "create", "write"], "scope": "all" } ]
        }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(dir));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Calendar_entries_ranges_lanes_and_colours_follow_the_date_fields()
    {
        await using var provider = BuildCalendarProvider("""
            , "calendar": { "startField": "starts_at", "endField": "ends_at", "colorField": "priority" }
            """);
        var meta = provider.GetRequiredService<IMetadataProvider>().Current.Get("CalTask");
        var docs = provider.GetRequiredService<DocumentService>();
        var staff = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());

        // Settings: configured, user override, fallback.
        Assert.True(CalendarView.Supports(meta), "DocType with date fields offers the calendar");
        Assert.Equal(new CalendarView.Settings("starts_at", "ends_at", "priority"), CalendarView.Resolve(meta), "configured settings");
        Assert.Equal(new CalendarView.Settings("due_date", null, "priority"), CalendarView.Resolve(meta, "due_date"), "user picks another date field: no end field");
        Assert.Equal(new CalendarView.Settings("starts_at", "ends_at", "priority"), CalendarView.Resolve(meta, "subject"), "a non-date override is ignored");

        // Ranges: Friday 20 Nov 2026.
        var fri = new DateOnly(2026, 11, 20);
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 12, 6)), CalendarView.Range(CalendarView.Mode.Month, fri), "month grid: 6 weeks from the Monday before the 1st");
        Assert.Equal((new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 22)), CalendarView.Range(CalendarView.Mode.Week, fri), "ISO week");
        Assert.Equal((fri, fri), CalendarView.Range(CalendarView.Mode.Day, fri), "day");
        Assert.Equal(new DateOnly(2026, 12, 20), CalendarView.Shift(CalendarView.Mode.Month, fri, 1), "next month");
        Assert.Equal(new DateOnly(2026, 11, 13), CalendarView.Shift(CalendarView.Mode.Week, fri, -1), "previous week");

        // Documents: local times without offset are read in the default zone, like the form does.
        async Task<Document> Add(string subject, string? start, string? end, string? due = null, string? until = null, string? priority = null) =>
            await docs.InsertAsync(staff, "CalTask", new Dictionary<string, object?>
            {
                ["subject"] = subject, ["starts_at"] = start, ["ends_at"] = end, ["due_date"] = due, ["until_date"] = until, ["priority"] = priority,
            });
        await Add("Standup", "2026-11-20T09:00", "2026-11-20T10:00", priority: "Low");
        await Add("Design review", "2026-11-20T09:30", "2026-11-20T11:00", priority: "High");
        await Add("Call", "2026-11-20T10:00", "2026-11-20T10:30", priority: "Medium");
        await Add("Lunch", "2026-11-20T13:00", null, priority: "Low");
        await Add("Night shift", "2026-11-20T22:00", "2026-11-21T06:00");
        await Add("Offsite", "2026-11-18T00:00", "2026-11-21T00:00");
        await Add("No date", null, null, due: "2026-11-25");

        var rows = (await docs.ListAsync(staff, "CalTask", new ListRequest(), allowAllReadableFields: true)).Items;
        var settings = CalendarView.Resolve(meta)!;
        var entries = CalendarView.Entries(meta, settings, rows);
        CalendarView.Entry E(string title) => entries.Single(e => e.Title == title);
        Assert.Equal(6, entries.Count, "rows without a start value are skipped");
        Assert.Equal(new DateTime(2026, 11, 20, 9, 0, 0), E("Standup").Start, "stored UTC shown in the default zone again");
        Assert.Equal(TimeSpan.FromHours(1), E("Lunch").End - E("Lunch").Start, "no end: one hour");
        Assert.True(E("Night shift").OnDay(new DateOnly(2026, 11, 21)) && E("Night shift").OnDay(fri), "cross-midnight entry shows on both days");
        Assert.Equal(6, CalendarView.ForDay(entries, fri).Count, "month cell of Friday: four day entries, the night shift and the offsite (18–20 Nov)");
        Assert.True(!E("Offsite").OnDay(new DateOnly(2026, 11, 21)), "offsite ends at Saturday 00:00");

        // Time grid of Friday: 09:00–10:00, 09:30–11:00 and 10:00–10:30 share a cluster of two lanes; lunch stands alone.
        var placed = CalendarView.LayoutDay(entries, fri);
        CalendarView.Placed P(string title) => placed.Single(p => p.Entry.Title == title);
        Assert.Equal((0, 2), (P("Standup").Lane, P("Standup").Lanes), "standup lane");
        Assert.Equal((1, 2), (P("Design review").Lane, P("Design review").Lanes), "overlapping review gets the second lane");
        Assert.Equal((0, 2), (P("Call").Lane, P("Call").Lanes), "call reuses the lane freed at 10:00");
        Assert.Equal((1, 1), (P("Lunch").Lanes, P("Lunch").Lanes), "lunch is its own cluster");
        Assert.Equal((9 * 60, 60), (P("Standup").TopMinutes, P("Standup").HeightMinutes), "position in minutes");
        Assert.Equal((22 * 60, 120), (P("Night shift").TopMinutes, P("Night shift").HeightMinutes), "clipped at midnight");
        Assert.True(placed.All(p => p.Entry.Title != "Offsite"), "whole-day span leaves the time grid");
        Assert.True(CalendarView.AllDay(entries, new DateOnly(2026, 11, 19)).Any(e => e.Title == "Offsite"), "and sits in the all-day row");

        // Colour slots follow the Select option order.
        var slots = CalendarView.ColorSlots(entries, meta.ByName["priority"]);
        Assert.Equal((0, 1, 2), (slots["Low"], slots["Medium"], slots["High"]), "slots by option order");

        // Date fields: inclusive end date; fallback to the first Date list column without configuration.
        var dateSettings = new CalendarView.Settings("due_date", "until_date", null);
        await Add("Audit", null, null, due: "2026-11-23", until: "2026-11-25");
        await Add("Bad end", null, null, due: "2026-11-23", until: "2026-11-20");
        var dated = CalendarView.Entries(meta, dateSettings, (await docs.ListAsync(staff, "CalTask", new ListRequest(), allowAllReadableFields: true)).Items);
        var audit = dated.Single(e => e.Title == "Audit");
        Assert.True(audit.AllDay && audit.Start == new DateTime(2026, 11, 23) && audit.End == new DateTime(2026, 11, 26), "inclusive end: 23–25 Nov");
        Assert.Equal(new DateTime(2026, 11, 24), dated.Single(e => e.Title == "Bad end").End, "end before start: one day");
    }

    [Test]
    public async Task Calendar_settings_are_validated_and_default_to_the_first_date_column()
    {
        await using (var ok = BuildCalendarProvider(""))
        {
            var meta = ok.GetRequiredService<IMetadataProvider>().Current.Get("CalTask");
            Assert.Equal(new CalendarView.Settings("due_date", null, null), CalendarView.Resolve(meta), "first Date/Datetime list column");
        }
        await using var bad = BuildCalendarProvider("""
            , "calendar": { "startField": "subject", "endField": "priority", "colorField": "nope" }
            """);
        var ex = Assert.Throws<MetadataCompilationException>(() => bad.GetRequiredService<IMetadataProvider>(), "invalid calendar settings are rejected");
        var paths = ex.Errors.Select(e => e.Path).ToList();
        Assert.True(paths.Contains("listView.calendar.startField") && paths.Contains("listView.calendar.endField") && paths.Contains("listView.calendar.colorField"),
            "all three settings reported: " + string.Join(", ", ex.Errors.Select(e => $"{e.Path}: {e.Message}")));
    }
}
