using System.Globalization;
using Framework.Metadata;

namespace Framework.Documents;

/// <summary>
/// Calendar view of a list (month, week, day): turns list rows into entries on a date field and lays them out.
/// Pure and I/O free; the rows come from <see cref="DocumentService.ListAsync"/>, so search, filters, row scope and
/// field permissions are the list's own.
/// </summary>
public static class CalendarView
{
    public enum Mode { Month, Week, Day }

    /// <summary>Fields the calendar can be placed on.</summary>
    public sealed record Settings(string StartField, string? EndField, string? ColorField);

    /// <summary>
    /// One entry. Times are local (the default time zone for Datetime fields). <paramref name="End"/> is exclusive:
    /// an all-day entry on 3 Dec ends at 4 Dec 00:00.
    /// </summary>
    public sealed record Entry(string Name, string Title, DateTime Start, DateTime End, bool AllDay, string? ColorKey, IReadOnlyDictionary<string, object?> Row)
    {
        public bool Overlaps(DateTime from, DateTime toExclusive) => Start < toExclusive && End > from;
        public bool OnDay(DateOnly day) => Overlaps(day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>Entry placed in a day column of the week/day time grid (minutes from midnight; lanes for overlaps).</summary>
    public sealed record Placed(Entry Entry, int TopMinutes, int HeightMinutes, int Lane, int Lanes);

    public static IReadOnlyList<CompiledField> DateFields(CompiledDocType meta) =>
        meta.Fields.Where(f => !f.IsLayout && f.Def.Type is "Date" or "Datetime").ToList();

    /// <summary>The calendar is offered for top-level DocTypes with at least one Date or Datetime field.</summary>
    public static bool Supports(CompiledDocType meta) => !meta.Def.IsSingle && !meta.Def.IsChild && DateFields(meta).Count > 0;

    /// <summary>
    /// Configured settings (<c>listView.calendar</c>), or the first Date/Datetime list column, or the first
    /// Date/Datetime field. <paramref name="startOverride"/> lets the user pick another date field.
    /// </summary>
    public static Settings? Resolve(CompiledDocType meta, string? startOverride = null)
    {
        var dates = DateFields(meta);
        if (dates.Count == 0) return null;
        var cfg = meta.Def.ListView.Calendar;
        if (startOverride is { Length: > 0 } && dates.Any(f => f.Name == startOverride))
            return startOverride == cfg?.StartField
                ? new Settings(cfg.StartField, Blank(cfg.EndField), Blank(cfg.ColorField))
                : new Settings(startOverride, null, Blank(cfg?.ColorField));
        if (cfg is not null && dates.Any(f => f.Name == cfg.StartField))
            return new Settings(cfg.StartField, Blank(cfg.EndField), Blank(cfg.ColorField));
        var start = meta.ListColumns.FirstOrDefault(c => dates.Any(f => f.Name == c)) ?? dates[0].Name;
        return new Settings(start, null, null);
    }

    /// <summary>First and last day shown: a 6-week grid for a month, Monday–Sunday for a week, the day itself.</summary>
    public static (DateOnly From, DateOnly To) Range(Mode mode, DateOnly anchor)
    {
        switch (mode)
        {
            case Mode.Month:
                var first = new DateOnly(anchor.Year, anchor.Month, 1);
                var gridStart = StartOfWeek(first);
                return (gridStart, gridStart.AddDays(41));
            case Mode.Week:
                var ws = StartOfWeek(anchor);
                return (ws, ws.AddDays(6));
            default:
                return (anchor, anchor);
        }
    }

    public static DateOnly Shift(Mode mode, DateOnly anchor, int direction) => mode switch
    {
        Mode.Month => anchor.AddMonths(direction),
        Mode.Week => anchor.AddDays(7 * direction),
        _ => anchor.AddDays(direction),
    };

    /// <summary>Monday of the ISO week.</summary>
    public static DateOnly StartOfWeek(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Entries of the rows. Rows without a start value are skipped. A Datetime entry without an end lasts one hour;
    /// an end before its start is ignored.
    /// </summary>
    public static List<Entry> Entries(CompiledDocType meta, Settings settings, IEnumerable<IReadOnlyDictionary<string, object?>> rows, TimeZoneInfo? zone = null)
    {
        var startField = meta.ByName[settings.StartField];
        var endField = settings.EndField is { } ef && meta.ByName.TryGetValue(ef, out var e) ? e : null;
        var list = new List<Entry>();
        foreach (var row in rows)
        {
            if (row.GetValueOrDefault(StandardFields.Name) is not string name) continue;
            if (Read(startField, row.GetValueOrDefault(startField.Name), zone) is not { } start) continue;
            var allDay = startField.Def.Type == "Date";
            DateTime end;
            var endValue = endField is null ? null : Read(endField, row.GetValueOrDefault(endField.Name), zone);
            if (allDay)
            {
                // Date fields: inclusive end date. A Datetime end on a Date start still ends on that day.
                var last = endValue is { } ev && ev.Date >= start ? ev.Date : start;
                end = last.AddDays(1);
            }
            else
            {
                end = endValue is { } ev && ev > start
                    ? (endField!.Def.Type == "Date" ? ev.Date.AddDays(1) : ev)
                    : start.AddHours(1);
            }
            var title = row.GetValueOrDefault("_title") is { } t && Convert.ToString(t, CultureInfo.InvariantCulture) is { Length: > 0 } ts ? ts : name;
            var color = settings.ColorField is { } cf ? Convert.ToString(row.GetValueOrDefault(cf), CultureInfo.InvariantCulture) : null;
            list.Add(new Entry(name, title, start, end, allDay, string.IsNullOrEmpty(color) ? null : color, row));
        }
        return list.OrderBy(x => x.Start).ThenByDescending(x => x.End - x.Start).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Entries shown in a month cell (all-day and timed), in start order.</summary>
    public static List<Entry> ForDay(IEnumerable<Entry> entries, DateOnly day) => entries.Where(x => x.OnDay(day)).ToList();

    /// <summary>
    /// Timed entries of one day column: clipped to the day, split into lanes so overlapping entries sit side by side.
    /// All-day entries and entries spanning the whole day are left for the all-day row.
    /// </summary>
    public static List<Placed> LayoutDay(IEnumerable<Entry> entries, DateOnly day, int minHeightMinutes = 20)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var timed = entries
            .Where(x => !x.AllDay && x.Overlaps(dayStart, dayEnd) && !(x.Start <= dayStart && x.End >= dayEnd))
            .Select(x =>
            {
                var top = (int)((x.Start < dayStart ? dayStart : x.Start) - dayStart).TotalMinutes;
                var bottom = (int)((x.End > dayEnd ? dayEnd : x.End) - dayStart).TotalMinutes;
                return (Entry: x, Top: top, Bottom: Math.Min(24 * 60, Math.Max(bottom, top + minHeightMinutes)));
            })
            .OrderBy(x => x.Top).ThenByDescending(x => x.Bottom).ToList();

        var placed = new List<Placed>();
        var cluster = new List<(Entry Entry, int Top, int Bottom, int Lane)>();
        var laneEnds = new List<int>();
        var clusterEnd = -1;
        void Flush()
        {
            foreach (var c in cluster) placed.Add(new Placed(c.Entry, c.Top, c.Bottom - c.Top, c.Lane, laneEnds.Count));
            cluster.Clear(); laneEnds.Clear();
        }
        foreach (var x in timed)
        {
            if (x.Top >= clusterEnd) Flush();
            var lane = laneEnds.FindIndex(endMin => endMin <= x.Top);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(x.Bottom); }
            else laneEnds[lane] = x.Bottom;
            cluster.Add((x.Entry, x.Top, x.Bottom, lane));
            clusterEnd = Math.Max(clusterEnd, x.Bottom);
        }
        Flush();
        return placed;
    }

    /// <summary>Entries for the all-day row of the week/day grid on <paramref name="day"/>.</summary>
    public static List<Entry> AllDay(IEnumerable<Entry> entries, DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        return entries.Where(x => x.Overlaps(dayStart, dayEnd) && (x.AllDay || (x.Start <= dayStart && x.End >= dayEnd))).ToList();
    }

    /// <summary>Stable colour slot (0..slots-1) per colour key, in first-seen order of the sorted keys.</summary>
    public static Dictionary<string, int> ColorSlots(IEnumerable<Entry> entries, CompiledField? colorField, int slots = 8)
    {
        var keys = entries.Select(e => e.ColorKey).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var options = colorField?.Def.Options;
        keys = options is { Count: > 0 }
            ? keys.OrderBy(k => options.IndexOf(k) is var i and >= 0 ? i : int.MaxValue).ThenBy(k => k, StringComparer.Ordinal).ToList()
            : keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i % slots, StringComparer.Ordinal);
    }

    private static DateTime? Read(CompiledField field, object? value, TimeZoneInfo? zone)
    {
        if (value is null) return null;
        if (field.Def.Type == "Datetime")
            return DatetimeFieldType.ToZone(value, zone)?.DateTime;
        return value switch
        {
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            DateTime dt => dt.Date,
            string s when DateOnly.TryParseExact(s.Length >= 10 ? s[..10] : s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d.ToDateTime(TimeOnly.MinValue),
            _ => null,
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
