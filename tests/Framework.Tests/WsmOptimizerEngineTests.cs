using Apps.Erp.Hooks;
using static Apps.Erp.Hooks.WsmOptimizerEngine;

/// <summary>
/// Pure CP-SAT engine checks (no database): staffing limits, rules across the start of the horizon,
/// pinned rows on non-candidate shifts, demand scopes and fairness.
/// </summary>
sealed partial class Tests
{
    private static readonly ShiftInput OptMorning = new("MOR", new(6, 0), new(14, 0), IsNight: false, IsMorning: true);
    private static readonly ShiftInput OptAfternoon = new("AFT", new(14, 0), new(22, 0), IsNight: false, IsMorning: false);
    private static readonly ShiftInput OptNight = new("NGT", new(22, 0), new(6, 0), IsNight: true, IsMorning: false);

    private static List<DateOnly> OptDays(int count) => Enumerable.Range(0, count).Select(i => new DateOnly(2026, 12, 7).AddDays(i)).ToList();
    private static List<EmployeeInput> OptEmployees(int count, string prefix = "E") =>
        Enumerable.Range(0, count).Select(i => new EmployeeInput($"{prefix}{i}", $"{prefix}{i}")).ToList();
    private static Dictionary<(string, DateOnly), string?> NoBaseline() => new();
    private static int Worked(OptimizerResult r, string emp) => r.Lines.Count(l => l.EmployeeId == emp && l.ScheduleType == "WORK");

    [Test]
    public Task WsmOptimizer_schedules_no_more_staff_than_the_target()
    {
        var days = OptDays(7);
        var emps = OptEmployees(8);
        var shifts = new[] { OptMorning, OptAfternoon, OptNight };
        var demands = days.SelectMany(d => shifts.Select(s => new DemandInput(d, s.Code, 1, 1))).ToList();

        // The optimum is easy to reach but slow to prove (many equal rosters), so a short time limit is enough.
        var r = Solve(new OptimizerInput(emps, days, shifts, demands, [], [], NoBaseline(), new RuleInput(), new WeightInput(TimeLimitSeconds: 3)));

        Assert.True(r.IsSuccess, $"solved ({r.SolverStatus})");
        Assert.Equal(0, r.ShortageSlots, "every slot covered");
        Assert.Equal(21, r.Lines.Count(l => l.ScheduleType == "WORK"), "exactly the 21 required shifts are scheduled (3 per day x 7 days)");
        Assert.Equal(0, r.OverstaffSlots, "nobody above the target");

        // A target above the minimum is filled when people are free, and no further.
        var withTarget = days.Select(d => new DemandInput(d, "MOR", 1, 3)).ToList();
        var t = Solve(new OptimizerInput(emps, days, [OptMorning], withTarget, [], [], NoBaseline(), new RuleInput(), new WeightInput(TimeLimitSeconds: 10)));
        Assert.True(days.All(d => t.Lines.Count(l => l.WorkDate == d && l.ScheduleType == "WORK") == 3), "staffed to the target of 3 each day");
        return Task.CompletedTask;
    }

    [Test]
    public Task WsmOptimizer_limits_working_days_per_7_days()
    {
        var days = OptDays(14);
        var emps = OptEmployees(3);
        var demands = days.Select(d => new DemandInput(d, "MOR", 3, 3)).ToList();

        var r = Solve(new OptimizerInput(emps, days, [OptMorning], demands, [], [], NoBaseline(),
            new RuleInput(MaxConsecutiveWorkDays: 6, MaxWorkDaysPer7Days: 5), new WeightInput(TimeLimitSeconds: 10)));

        Assert.True(r.IsSuccess, $"solved ({r.SolverStatus})");
        foreach (var e in emps)
            for (int start = 0; start + 7 <= days.Count; start++)
            {
                var window = days.Skip(start).Take(7).ToHashSet();
                var n = r.Lines.Count(l => l.EmployeeId == e.Id && window.Contains(l.WorkDate) && l.ScheduleType == "WORK");
                Assert.True(n <= 5, $"{e.Id} works {n} days in the 7 days from {days[start]}");
            }
        Assert.True(r.ShortageSlots > 0, "3 people x 5 days cannot cover 3 x 7 shifts a week: the gap is reported as shortage");
        return Task.CompletedTask;
    }

    [Test]
    public Task WsmOptimizer_counts_days_before_the_horizon()
    {
        var days = OptDays(7);
        var emps = OptEmployees(2);
        var demands = days.Select(d => new DemandInput(d, "MOR", 1, 1)).ToList();
        // E0 worked the 6 days before the horizon; E1 was off. E0 is cheaper to keep (preference), so without history
        // the solver would happily give E0 the first day.
        var history = Enumerable.Range(1, 6).ToDictionary(i => ("E0", days[0].AddDays(-i)), _ => (string?)"MOR");
        var prefs = new List<PreferenceInput> { new("E0", "Prefer", "MOR", null, null, null, 5) };

        var r = Solve(new OptimizerInput(emps, days, [OptMorning], demands, [], prefs, NoBaseline(),
            new RuleInput(MaxConsecutiveWorkDays: 6), new WeightInput(TimeLimitSeconds: 10), history));

        Assert.True(r.IsSuccess, $"solved ({r.SolverStatus})");
        Assert.Equal("REST", r.Lines.Single(l => l.EmployeeId == "E0" && l.WorkDate == days[0]).ScheduleType,
            "after 6 days in a row before the horizon, E0 must rest on the first day");

        // Rest hours across the start: a night shift the day before forbids a morning shift on the first day.
        var nightBefore = new Dictionary<(string, DateOnly), string?> { [("E0", days[0].AddDays(-1))] = "NGT" };
        var r2 = Solve(new OptimizerInput(emps, days, [OptMorning, OptNight], demands, [], prefs, NoBaseline(),
            new RuleInput(), new WeightInput(TimeLimitSeconds: 10), nightBefore));
        Assert.True(r2.Lines.Single(l => l.EmployeeId == "E0" && l.WorkDate == days[0]).ShiftCode != "MOR",
            "no morning shift right after a night shift worked before the horizon");
        return Task.CompletedTask;
    }

    [Test]
    public Task WsmOptimizer_keeps_pinned_work_on_a_non_candidate_shift()
    {
        var days = OptDays(7);
        var emps = OptEmployees(2);
        var office = new ShiftInput("OFF8", new(8, 0), new(17, 0), false, false, Assignable: false);
        var demands = days.Select(d => new DemandInput(d, "NGT", 1, 1)).ToList();
        var pinned = new List<PinnedSlot> { new("E0", days[1], "WORK", "OFF8"), new("E1", days[3], "WORK", "UNKNOWN-SHIFT") };

        var r = Solve(new OptimizerInput(emps, days, [OptMorning, OptNight, office], demands, pinned, [], NoBaseline(),
            new RuleInput(), new WeightInput(TimeLimitSeconds: 10)));

        Assert.True(r.IsSuccess, $"solved ({r.SolverStatus})");
        var locked = r.Lines.Single(l => l.EmployeeId == "E0" && l.WorkDate == days[1]);
        Assert.True(locked is { ScheduleType: "WORK", ShiftCode: "OFF8", IsPinned: true }, $"locked row stays WORK OFF8 (got {locked.ScheduleType} {locked.ShiftCode})");
        Assert.True(r.Lines.Single(l => l.EmployeeId == "E0" && l.WorkDate == days[0]).ShiftCode != "NGT",
            "no night shift ending at 06:00 before the locked 08:00 shift (2 hours rest)");
        var unknown = r.Lines.Single(l => l.EmployeeId == "E1" && l.WorkDate == days[3]);
        Assert.True(unknown is { ScheduleType: "WORK", ShiftCode: "UNKNOWN-SHIFT" }, "a pinned shift missing from the input still counts as work");
        Assert.True(r.Lines.Where(l => !l.IsPinned).All(l => l.ShiftCode is not ("OFF8" or "UNKNOWN-SHIFT")), "non-candidate shifts are never handed out");
        return Task.CompletedTask;
    }

    [Test]
    public Task WsmOptimizer_adds_demands_and_respects_their_scope()
    {
        var days = OptDays(3);
        var unitA = OptEmployees(4, "A");
        var unitB = OptEmployees(4, "B");
        var emps = unitA.Concat(unitB).ToList();
        var idsA = unitA.Select(e => e.Id).ToHashSet();
        var idsB = unitB.Select(e => e.Id).ToHashSet();

        // Same scope twice (two requirements for unit A): they add up to 2 + 1 = 3.
        // Unit B needs 2 of its own people.
        var demands = days.SelectMany(d => new[]
        {
            new DemandInput(d, "MOR", 2, 2, idsA, "A"),
            new DemandInput(d, "MOR", 1, 1, idsA, "A"),
            new DemandInput(d, "MOR", 2, 2, idsB, "B"),
        }).ToList();

        var r = Solve(new OptimizerInput(emps, days, [OptMorning], demands, [], [], NoBaseline(), new RuleInput(), new WeightInput(TimeLimitSeconds: 10)));

        Assert.True(r.IsSuccess, $"solved ({r.SolverStatus})");
        foreach (var d in days)
        {
            Assert.Equal(3, r.Lines.Count(l => l.WorkDate == d && l.ScheduleType == "WORK" && idsA.Contains(l.EmployeeId)), $"unit A staffed 2 + 1 on {d}");
            Assert.Equal(2, r.Lines.Count(l => l.WorkDate == d && l.ScheduleType == "WORK" && idsB.Contains(l.EmployeeId)), $"unit B staffed with its own people on {d}");
        }

        // Unit B has only one person: the gap shows as a shortage of B, not covered by spare people of A.
        var small = emps.Where(e => idsA.Contains(e.Id) || e.Id == "B0").ToList();
        var r2 = Solve(new OptimizerInput(small, days, [OptMorning], demands, [], [], NoBaseline(), new RuleInput(), new WeightInput(TimeLimitSeconds: 10)));
        Assert.Equal(3, r2.ShortageSlots, "unit B is one short on each of the 3 days");
        Assert.True(r2.Shortages.All(s => s.Scope == "B"), "shortages are reported for unit B");
        return Task.CompletedTask;
    }

    [Test]
    public Task WsmOptimizer_fairness_ignores_employees_on_leave()
    {
        var days = OptDays(7);
        var emps = OptEmployees(4);
        var demands = days.Select(d => new DemandInput(d, "MOR", 1, 1)).ToList();
        // Last period E0 worked every weekday; stability alone would keep that pattern.
        var baseline = new Dictionary<(string, DateOnly), string?>();
        foreach (var e in emps)
            for (int i = 0; i < days.Count; i++)
                baseline[(e.Id, days[i])] = e.Id == "E0" && i < 6 ? "MOR" : null;
        var leave = days.Select(d => new PinnedSlot("E3", d, "LEAVE", null)).ToList();

        var r = Solve(new OptimizerInput(emps, days, [OptMorning], demands, leave, [], baseline, new RuleInput(), new WeightInput(TimeLimitSeconds: 10)));

        Assert.True(r.IsSuccess, $"solved ({r.SolverStatus})");
        var counts = new[] { "E0", "E1", "E2" }.Select(e => Worked(r, e)).ToList();
        Assert.Equal(7, counts.Sum(), "7 shifts, no extra ones");
        Assert.True(counts.Max() - counts.Min() <= 1, $"the 7 shifts are spread over the 3 available people ({string.Join("/", counts)})");
        return Task.CompletedTask;
    }
}
