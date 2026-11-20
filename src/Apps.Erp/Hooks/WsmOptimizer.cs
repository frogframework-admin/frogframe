using System.Diagnostics;
using System.Globalization;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;
using Google.OrTools.Sat;

namespace Apps.Erp.Hooks;

/// <summary>
/// Pure Google OR-Tools CP-SAT Auto-Scheduling Optimizer engine.
/// Balances shift coverage requirements, employee preferences, workload/weekend/night fairness,
/// and stability against baseline schedules while enforcing hard fatigue and rest constraints.
/// </summary>
public static class WsmOptimizerEngine
{
    public sealed record EmployeeInput(
        string Id,
        string Name,
        DateOnly? HireDate = null,
        DateOnly? SeparationDate = null,
        string? OrgUnit = null,
        string? ShiftGroup = null);

    /// <param name="Assignable">
    /// False for a shift the solver may not hand out: it only appears in the model because a pinned row or a day
    /// before the horizon uses it, so rest rules and coverage still see it.
    /// </param>
    public sealed record ShiftInput(
        string Code,
        TimeOnly StartTime,
        TimeOnly EndTime,
        bool IsNight,
        bool IsMorning,
        bool Assignable = true);

    public sealed record PinnedSlot(
        string EmployeeId,
        DateOnly Date,
        string ScheduleType,
        string? ShiftCode);

    public sealed record PreferenceInput(
        string EmployeeId,
        string PreferenceType, // "Prefer" | "Avoid" | "Unavailable"
        string? ShiftCode,
        DateOnly? FromDate,
        DateOnly? ToDate,
        IReadOnlySet<DayOfWeek>? DaysOfWeek,
        int Weight = 3);

    /// <summary>
    /// Headcount needed on one day for one shift. Demands with the same day, shift and scope add up.
    /// </summary>
    /// <param name="EligibleEmployeeIds">
    /// Employees who count toward this demand (e.g. members of the requirement's organization unit with the
    /// requirement's job profile). Null = every employee in the run. Each demand is a separate minimum on its own scope.
    /// </param>
    /// <param name="Scope">Label of the scope for logs and shortage lines (e.g. the organization unit).</param>
    public sealed record DemandInput(
        DateOnly Date,
        string ShiftCode,
        int MinHeadcount,
        int TargetHeadcount = 0,
        IReadOnlySet<string>? EligibleEmployeeIds = null,
        string? Scope = null);

    /// <param name="MaxWorkDaysPer7Days">Most working days in any 7 consecutive days (e.g. 5 for a 5-day week). 7 = no limit.</param>
    public sealed record RuleInput(
        int MaxConsecutiveWorkDays = 6,
        double MinRestHours = 11.0,
        bool ForbidNightToMorning = true,
        int MaxWorkDaysPer7Days = 7);

    /// <param name="Overstaff">Cost of each shift assigned above the target headcount (or on a day/shift without demand).
    /// Keep it above Fairness and above Preference x 5, so nobody is scheduled only to even out numbers or to honour a preference.</param>
    /// <param name="Target">Cost of each shift missing between the minimum and the target headcount.</param>
    public sealed record WeightInput(
        int Coverage = 100,
        int Preference = 10,
        int Fairness = 20,
        int NightBalance = 15,
        int Stability = 3,
        int TimeLimitSeconds = 20,
        int Overstaff = 60,
        int Target = 20);

    /// <param name="History">
    /// Schedule of the days just before <see cref="Days"/>: shift code for a working day, null for a day off.
    /// Used so consecutive-day, 7-day and rest-hour rules hold across the start of the horizon. Missing days count as off.
    /// </param>
    public sealed record OptimizerInput(
        IReadOnlyList<EmployeeInput> Employees,
        IReadOnlyList<DateOnly> Days,
        IReadOnlyList<ShiftInput> Shifts,
        IReadOnlyList<DemandInput> Demands,
        IReadOnlyList<PinnedSlot> Pinned,
        IReadOnlyList<PreferenceInput> Preferences,
        IReadOnlyDictionary<(string EmpId, DateOnly Date), string?> Baseline,
        RuleInput Rules,
        WeightInput Weights,
        IReadOnlyDictionary<(string EmpId, DateOnly Date), string?>? History = null);

    public sealed record ProposedScheduleLine(
        string EmployeeId,
        string EmployeeName,
        DateOnly WorkDate,
        string ScheduleType,
        string? ShiftCode,
        string? PreviousShiftCode,
        bool IsChanged,
        bool IsPinned,
        string PreferenceHit);

    public sealed record DailyShortage(DateOnly Date, string ShiftCode, int Required, int Assigned, int Shortage, string? Scope = null);

    public sealed record OptimizerResult(
        string SolverStatus,
        bool IsSuccess,
        double ObjectiveValue,
        long SolveMs,
        double CoveragePct,
        double PreferenceSatisfactionPct,
        int ShortageSlots,
        int ChangedRows,
        IReadOnlyList<ProposedScheduleLine> Lines,
        IReadOnlyList<DailyShortage> Shortages,
        string Log,
        int OverstaffSlots = 0);

    public static OptimizerResult Solve(OptimizerInput input)
    {
        var sw = Stopwatch.StartNew();
        int E = input.Employees.Count;
        int D = input.Days.Count;
        var history = input.History ?? new Dictionary<(string, DateOnly), string?>();

        // Shifts used by pinned rows or by the days before the horizon must exist in the model, even when they are
        // not candidates: otherwise a locked working day looks like a day off to every rule.
        var shifts = input.Shifts.ToList();
        var knownTimes = shifts.Select(_ => true).ToList();
        void AddFixedShift(string? code)
        {
            if (code is null || shifts.Any(s => s.Code == code)) return;
            shifts.Add(new ShiftInput(code, TimeOnly.MinValue, TimeOnly.MinValue, false, false, Assignable: false));
            knownTimes.Add(false); // times unknown: excluded from rest-hour pairs, still counts as a working day
        }
        foreach (var p in input.Pinned.Where(p => p.ScheduleType == "WORK")) AddFixedShift(p.ShiftCode);
        foreach (var h in history.Values) AddFixedShift(h);
        int S = shifts.Count;

        if (E == 0 || D == 0 || !shifts.Any(s => s.Assignable))
        {
            sw.Stop();
            return new OptimizerResult(
                "EMPTY", false, 0, sw.ElapsedMilliseconds, 0, 100, 0, 0, [], [],
                $"Cannot optimize: employees={E}, days={D}, candidate_shifts={shifts.Count(s => s.Assignable)}.");
        }

        var empIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int e = 0; e < E; e++) empIndex[input.Employees[e].Id] = e;
        var dayIndex = new Dictionary<DateOnly, int>();
        for (int d = 0; d < D; d++) dayIndex[input.Days[d]] = d;
        var shiftIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int s = 0; s < S; s++) shiftIndex[shifts[s].Code] = s;

        var pinnedMap = new Dictionary<(int e, int d), PinnedSlot>();
        foreach (var p in input.Pinned)
        {
            if (empIndex.TryGetValue(p.EmployeeId, out var ei) && dayIndex.TryGetValue(p.Date, out var di))
                pinnedMap[(ei, di)] = p;
        }

        var model = new CpModel();

        // Decision variables: x[e, d, s] = 1 iff employee e works shift s on day d
        var x = new BoolVar[E, D, S];
        var work = new BoolVar[E, D];
        for (int e = 0; e < E; e++)
        {
            for (int d = 0; d < D; d++)
            {
                work[e, d] = model.NewBoolVar($"w_{e}_{d}");
                var shiftVars = new List<IntVar>(S);
                for (int s = 0; s < S; s++)
                {
                    x[e, d, s] = model.NewBoolVar($"x_{e}_{d}_{s}");
                    shiftVars.Add(x[e, d, s]);
                }
                // At most one shift per day; work[e,d] == sum_s x[e,d,s]
                model.Add(LinearExpr.Sum(shiftVars) == work[e, d]);
            }
        }

        // Incompatible consecutive shift pairs (s1 on day d -> s2 on day d+1)
        var forbiddenPairs = new List<(int s1, int s2)>();
        for (int s1 = 0; s1 < S; s1++)
        {
            for (int s2 = 0; s2 < S; s2++)
            {
                if (!knownTimes[s1] || !knownTimes[s2]) continue;
                var sh1 = shifts[s1];
                var sh2 = shifts[s2];
                if (input.Rules.ForbidNightToMorning && sh1.IsNight && sh2.IsMorning)
                {
                    forbiddenPairs.Add((s1, s2));
                    continue;
                }
                var restHours = ComputeRestHoursBetween(sh1, sh2);
                if (restHours < input.Rules.MinRestHours - 1e-6)
                    forbiddenPairs.Add((s1, s2));
            }
        }

        // Pinning, hire/separation bounds, non-assignable shifts
        var pinnedWork = new bool[E, D];
        var pinnedShiftIdx = new int[E, D];
        var available = new bool[E, D];
        for (int e = 0; e < E; e++)
        {
            var emp = input.Employees[e];
            for (int d = 0; d < D; d++)
            {
                pinnedShiftIdx[e, d] = -1;
                var date = input.Days[d];
                if ((emp.HireDate is { } hd && date < hd) || (emp.SeparationDate is { } sd && date > sd))
                {
                    model.Add(work[e, d] == 0);
                    continue;
                }
                if (pinnedMap.TryGetValue((e, d), out var pin))
                {
                    if (pin.ScheduleType == "WORK" && pin.ShiftCode is not null && shiftIndex.TryGetValue(pin.ShiftCode, out var si))
                    {
                        model.Add(x[e, d, si] == 1);
                        pinnedWork[e, d] = true;
                        pinnedShiftIdx[e, d] = si;
                    }
                    else
                    {
                        model.Add(work[e, d] == 0);
                    }
                    continue;
                }
                available[e, d] = true;
                for (int s = 0; s < S; s++)
                    if (!shifts[s].Assignable) model.Add(x[e, d, s] == 0);
            }
        }

        // Hard Unavailable preferences (respecting pinned slots)
        var preferBonus = new Dictionary<(int e, int d, int s), int>();
        var avoidPenalty = new Dictionary<(int e, int d, int s), int>();
        var preferDayBonus = new Dictionary<(int e, int d), int>();
        var avoidDayPenalty = new Dictionary<(int e, int d), int>();

        foreach (var pref in input.Preferences)
        {
            if (!empIndex.TryGetValue(pref.EmployeeId, out var ei)) continue;
            var w = Math.Clamp(pref.Weight, 1, 5);
            for (int d = 0; d < D; d++)
            {
                var date = input.Days[d];
                if (pref.FromDate is { } f && date < f) continue;
                if (pref.ToDate is { } t && date > t) continue;
                if (pref.DaysOfWeek is { Count: > 0 } dows && !dows.Contains(date.DayOfWeek)) continue;
                if (pinnedMap.ContainsKey((ei, d))) continue;

                if (pref.PreferenceType == "Unavailable")
                {
                    if (pref.ShiftCode is null)
                    {
                        model.Add(work[ei, d] == 0);
                    }
                    else if (shiftIndex.TryGetValue(pref.ShiftCode, out var si))
                    {
                        model.Add(x[ei, d, si] == 0);
                    }
                }
                else if (pref.PreferenceType == "Prefer")
                {
                    if (pref.ShiftCode is not null && shiftIndex.TryGetValue(pref.ShiftCode, out var si))
                        preferBonus[(ei, d, si)] = preferBonus.GetValueOrDefault((ei, d, si)) + w;
                    else
                        preferDayBonus[(ei, d)] = preferDayBonus.GetValueOrDefault((ei, d)) + w;
                }
                else if (pref.PreferenceType == "Avoid")
                {
                    if (pref.ShiftCode is not null && shiftIndex.TryGetValue(pref.ShiftCode, out var si))
                        avoidPenalty[(ei, d, si)] = avoidPenalty.GetValueOrDefault((ei, d, si)) + w;
                    else
                        avoidDayPenalty[(ei, d)] = avoidDayPenalty.GetValueOrDefault((ei, d)) + w;
                }
            }
        }

        // Working days before the horizon (offset -1 = the day before Days[0]).
        bool WorkedBefore(string empId, int offset) =>
            history.TryGetValue((empId, input.Days[0].AddDays(offset)), out var code) && code is not null;

        // Hard limits on working days in a sliding window, counting the days before the horizon:
        // max consecutive working days (window maxConsec + 1) and max working days in any 7 days.
        void AddWindowLimit(int windowLength, int limit)
        {
            if (limit >= windowLength) return;
            for (int e = 0; e < E; e++)
            {
                var empId = input.Employees[e].Id;
                // Every window ending on a day of the horizon; its start may lie before the horizon.
                for (int end = 0; end < D; end++)
                {
                    int start = end - windowLength + 1;
                    int before = 0, pinnedIn = 0;
                    var vars = new List<IntVar>(windowLength);
                    for (int k = start; k <= end; k++)
                    {
                        if (k < 0) { if (WorkedBefore(empId, k)) before++; continue; }
                        if (pinnedWork[e, k]) pinnedIn++;
                        vars.Add(work[e, k]);
                    }
                    // A window already over the limit because of history or pinned rows is a known conflict, not something to solve.
                    if (before + pinnedIn > limit) continue;
                    model.Add(LinearExpr.Sum(vars) <= limit - before);
                }
            }
        }
        int maxConsec = Math.Max(1, input.Rules.MaxConsecutiveWorkDays);
        AddWindowLimit(maxConsec + 1, maxConsec);
        AddWindowLimit(7, Math.Clamp(input.Rules.MaxWorkDaysPer7Days, 1, 7));

        // Hard constraint: minimum rest hours & night-to-morning restriction
        foreach (var (s1, s2) in forbiddenPairs)
        {
            for (int e = 0; e < E; e++)
            {
                for (int d = 0; d < D - 1; d++)
                {
                    if (pinnedShiftIdx[e, d] == s1 && pinnedShiftIdx[e, d + 1] == s2)
                        continue; // pre-existing pinned conflict
                    model.Add(x[e, d, s1] + x[e, d + 1, s2] <= 1);
                }
                // Across the start of the horizon: the shift worked the day before Days[0].
                if (history.TryGetValue((input.Employees[e].Id, input.Days[0].AddDays(-1)), out var prev)
                    && prev is not null && shiftIndex.TryGetValue(prev, out var prevIdx) && prevIdx == s1
                    && pinnedShiftIdx[e, 0] != s2)
                    model.Add(x[e, 0, s2] == 0);
            }
        }

        // Objective terms (all linear, minimized)
        var objTerms = new List<LinearExpr>();
        var weights = input.Weights;

        // 1. Coverage per demand scope: shortage below the minimum, and (cheaper) shortfall below the target.
        //    Demands with the same day, shift and scope add up; different scopes are separate minimums.
        var demandGroups = input.Demands
            .Where(dem => (dem.MinHeadcount > 0 || dem.TargetHeadcount > 0) && dayIndex.ContainsKey(dem.Date) && shiftIndex.ContainsKey(dem.ShiftCode))
            .GroupBy(dem => (d: dayIndex[dem.Date], s: shiftIndex[dem.ShiftCode],
                scope: dem.EligibleEmployeeIds is null ? null : string.Join("|", dem.EligibleEmployeeIds.Order(StringComparer.Ordinal))))
            .Select(g => new
            {
                g.Key.d,
                g.Key.s,
                Min = g.Sum(x => Math.Max(0, x.MinHeadcount)),
                Target = g.Sum(x => Math.Max(Math.Max(0, x.MinHeadcount), x.TargetHeadcount)),
                Eligible = g.First().EligibleEmployeeIds is { } ids
                    ? Enumerable.Range(0, E).Where(e => ids.Contains(input.Employees[e].Id)).ToList()
                    : Enumerable.Range(0, E).ToList(),
                Scope = string.Join(", ", g.Select(x => x.Scope).OfType<string>().Distinct()),
            })
            .ToList();

        foreach (var g in demandGroups)
        {
            var assigned = LinearExpr.Sum(g.Eligible.Select(e => (IntVar)x[e, g.d, g.s]));
            if (g.Min > 0 && weights.Coverage > 0)
            {
                var shortage = model.NewIntVar(0, g.Min, $"short_{g.d}_{g.s}");
                model.Add(assigned + shortage >= g.Min);
                objTerms.Add(shortage * weights.Coverage);
            }
            if (g.Target > g.Min && weights.Target > 0)
            {
                var gap = model.NewIntVar(0, g.Target, $"gap_{g.d}_{g.s}");
                model.Add(assigned + gap >= g.Target);
                objTerms.Add(gap * weights.Target);
            }
        }

        // 1b. Overstaffing: every assignment above the summed target of a day/shift costs Overstaff.
        //     A candidate shift without demand on a day has target 0, so it is only used when something else pays for it.
        var allowance = new Dictionary<(int d, int s), int>();
        foreach (var g in demandGroups) allowance[(g.d, g.s)] = allowance.GetValueOrDefault((g.d, g.s)) + g.Target;
        var overVars = new List<IntVar>();
        if (weights.Overstaff > 0)
        {
            for (int d = 0; d < D; d++)
            {
                for (int s = 0; s < S; s++)
                {
                    if (!shifts[s].Assignable) continue;
                    var allowed = allowance.GetValueOrDefault((d, s));
                    var staffed = LinearExpr.Sum(Enumerable.Range(0, E).Select(e => (IntVar)x[e, d, s]));
                    var over = model.NewIntVar(0, E, $"over_{d}_{s}");
                    model.Add(over >= staffed - allowed);
                    overVars.Add(over);
                    objTerms.Add(over * weights.Overstaff);
                }
            }
        }

        // 2. Preferences (Prefer = negative cost / reward; Avoid = positive penalty)
        if (weights.Preference > 0)
        {
            foreach (var ((ei, di, si), w) in preferBonus)
                objTerms.Add(x[ei, di, si] * (-w * weights.Preference));
            foreach (var ((ei, di, si), w) in avoidPenalty)
                objTerms.Add(x[ei, di, si] * (w * weights.Preference));
            foreach (var ((ei, di), w) in preferDayBonus)
                objTerms.Add(work[ei, di] * (-w * weights.Preference));
            foreach (var ((ei, di), w) in avoidDayPenalty)
                objTerms.Add(work[ei, di] * (w * weights.Preference));
        }

        // 3. Workload fairness (max - min, plus weekend spread). Days an employee cannot work (leave, before hire,
        //    after separation, whole-day Unavailable) count as used, so someone with one day of leave is expected to
        //    work one day less instead of pulling the minimum down. Employees with no available day are left out.
        var blocked = new bool[E, D];
        for (int e = 0; e < E; e++)
            for (int d = 0; d < D; d++)
                blocked[e, d] = !available[e, d] && !pinnedWork[e, d];
        foreach (var pref in input.Preferences.Where(p => p.PreferenceType == "Unavailable" && p.ShiftCode is null))
        {
            if (!empIndex.TryGetValue(pref.EmployeeId, out var ei)) continue;
            for (int d = 0; d < D; d++)
            {
                var date = input.Days[d];
                if (pref.FromDate is { } f && date < f) continue;
                if (pref.ToDate is { } t && date > t) continue;
                if (pref.DaysOfWeek is { Count: > 0 } dows && !dows.Contains(date.DayOfWeek)) continue;
                if (!pinnedMap.ContainsKey((ei, d))) blocked[ei, d] = true;
            }
        }
        var fairSet = Enumerable.Range(0, E).Where(e => Enumerable.Range(0, D).Any(d => !blocked[e, d])).ToList();

        IntVar Load(int e, IReadOnlyList<int> dayList, string name)
        {
            var t = model.NewIntVar(0, dayList.Count, name);
            var fixedDays = dayList.Count(d => blocked[e, d]);
            model.Add(t == LinearExpr.Sum(dayList.Select(d => (IntVar)work[e, d])) + fixedDays);
            return t;
        }

        if (weights.Fairness > 0 && fairSet.Count > 1)
        {
            var allDays = Enumerable.Range(0, D).ToList();
            var totalWork = fairSet.Select(e => Load(e, allDays, $"totW_{e}")).ToList();
            var maxW = model.NewIntVar(0, D, "maxW");
            var minW = model.NewIntVar(0, D, "minW");
            model.AddMaxEquality(maxW, totalWork);
            model.AddMinEquality(minW, totalWork);
            objTerms.Add((maxW - minW) * weights.Fairness);

            var weekendDays = Enumerable.Range(0, D)
                .Where(d => input.Days[d].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                .ToList();
            if (weekendDays.Count > 0)
            {
                var totalWknd = fairSet.Select(e => Load(e, weekendDays, $"totWk_{e}")).ToList();
                var maxWk = model.NewIntVar(0, weekendDays.Count, "maxWk");
                var minWk = model.NewIntVar(0, weekendDays.Count, "minWk");
                model.AddMaxEquality(maxWk, totalWknd);
                model.AddMinEquality(minWk, totalWknd);
                objTerms.Add((maxWk - minWk) * Math.Max(1, weights.Fairness / 2));
            }
        }

        // 4. Night shift balance (max - min night shifts across the same employees)
        var nightShifts = Enumerable.Range(0, S).Where(s => shifts[s].IsNight).ToList();
        if (weights.NightBalance > 0 && fairSet.Count > 1 && nightShifts.Count > 0)
        {
            var totalNight = new List<IntVar>();
            foreach (var e in fairSet)
            {
                var t = model.NewIntVar(0, D, $"totN_{e}");
                var nList = new List<IntVar>();
                for (int d = 0; d < D; d++)
                    foreach (var ns in nightShifts)
                        nList.Add(x[e, d, ns]);
                model.Add(t == LinearExpr.Sum(nList));
                totalNight.Add(t);
            }
            var maxN = model.NewIntVar(0, D, "maxN");
            var minN = model.NewIntVar(0, D, "minN");
            model.AddMaxEquality(maxN, totalNight);
            model.AddMinEquality(minN, totalNight);
            objTerms.Add((maxN - minN) * weights.NightBalance);
        }

        // 5. Stability vs baseline schedule (penalize deviations from existing pattern)
        if (weights.Stability > 0 && input.Baseline.Count > 0)
        {
            for (int e = 0; e < E; e++)
            {
                var empId = input.Employees[e].Id;
                for (int d = 0; d < D; d++)
                {
                    if (pinnedMap.ContainsKey((e, d))) continue;
                    if (!input.Baseline.TryGetValue((empId, input.Days[d]), out var baseShift)) continue;
                    if (baseShift is not null && shiftIndex.TryGetValue(baseShift, out var bsi) && shifts[bsi].Assignable)
                    {
                        // Deviation = 1 - x[e, d, bsi]
                        objTerms.Add((1 - x[e, d, bsi]) * weights.Stability);
                    }
                    else if (baseShift is null)
                    {
                        // Baseline was REST; penalize turning into WORK
                        objTerms.Add(work[e, d] * weights.Stability);
                    }
                }
            }
        }

        if (objTerms.Count > 0)
            model.Minimize(LinearExpr.Sum(objTerms));

        var solver = new CpSolver
        {
            StringParameters = $"max_time_in_seconds:{Math.Clamp(weights.TimeLimitSeconds, 1, 120)},num_workers:4,random_seed:42"
        };
        var status = solver.Solve(model);
        sw.Stop();

        var isOk = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;
        if (!isOk)
        {
            return new OptimizerResult(
                status.ToString().ToUpperInvariant(), false, 0, sw.ElapsedMilliseconds,
                0, 0, 0, 0, [], [],
                $"CP-SAT solver returned {status} in {sw.ElapsedMilliseconds} ms.");
        }

        // Extract proposed lines and KPIs
        var lines = new List<ProposedScheduleLine>(E * D);
        int changedCount = 0;
        int prefTotal = 0, prefSatisfied = 0;

        for (int e = 0; e < E; e++)
        {
            var emp = input.Employees[e];
            for (int d = 0; d < D; d++)
            {
                var date = input.Days[d];
                if ((emp.HireDate is { } hd && date < hd) || (emp.SeparationDate is { } sd && date > sd))
                    continue;

                var isPinned = pinnedMap.TryGetValue((e, d), out var pin);
                string schedType;
                string? chosenShift = null;

                if (isPinned && pin!.ScheduleType != "WORK")
                {
                    schedType = pin.ScheduleType;
                }
                else
                {
                    for (int s = 0; s < S; s++)
                    {
                        if (solver.BooleanValue(x[e, d, s]))
                        {
                            chosenShift = shifts[s].Code;
                            break;
                        }
                    }
                    schedType = chosenShift is not null ? "WORK" : "REST";
                }

                input.Baseline.TryGetValue((emp.Id, date), out var prevShift);
                var isChanged = !isPinned && !string.Equals(prevShift, chosenShift, StringComparison.Ordinal);
                if (isChanged) changedCount++;

                // Evaluate preference hit label for this slot
                var prefHit = EvaluateSlotPreference(input.Preferences, emp.Id, date, chosenShift, ref prefTotal, ref prefSatisfied);

                lines.Add(new ProposedScheduleLine(
                    emp.Id, emp.Name, date, schedType, chosenShift, prevShift, isChanged, isPinned, prefHit));
            }
        }

        // Coverage shortages per demand scope
        var shortages = new List<DailyShortage>();
        int totalRequired = 0, totalShortage = 0;
        foreach (var g in demandGroups.Where(g => g.Min > 0).OrderBy(g => g.d).ThenBy(g => g.s))
        {
            int assigned = g.Eligible.Count(e => solver.BooleanValue(x[e, g.d, g.s]));
            int shortage = Math.Max(0, g.Min - assigned);
            totalRequired += g.Min;
            totalShortage += shortage;
            if (shortage > 0)
                shortages.Add(new DailyShortage(input.Days[g.d], shifts[g.s].Code, g.Min, assigned, shortage, g.Scope.Length > 0 ? g.Scope : null));
        }
        int overstaffSlots = overVars.Sum(v => (int)solver.Value(v));

        double coveragePct = totalRequired == 0 ? 100.0 : Math.Round(100.0 * (totalRequired - totalShortage) / totalRequired, 1);
        double prefPct = prefTotal == 0 ? 100.0 : Math.Round(100.0 * prefSatisfied / prefTotal, 1);

        var log = $"CP-SAT status: {status} ({sw.ElapsedMilliseconds} ms) | Employees: {E}, Days: {D}, Shifts: {shifts.Count(s => s.Assignable)} | " +
                  $"Coverage: {coveragePct:F1}% ({totalShortage} shortage slot(s)) | Above target: {overstaffSlots} | Preferences: {prefPct:F1}% | Changed: {changedCount}.";
        if (shortages.Count > 0)
            log += "\nShortages:\n- " + string.Join("\n- ", shortages.Take(20).Select(s =>
                $"{s.Date:yyyy-MM-dd} {s.ShiftCode}{(s.Scope is null ? "" : $" [{s.Scope}]")}: {s.Assigned}/{s.Required} (short {s.Shortage})"));

        return new OptimizerResult(
            status.ToString().ToUpperInvariant(),
            true,
            solver.ObjectiveValue,
            sw.ElapsedMilliseconds,
            coveragePct,
            prefPct,
            totalShortage,
            changedCount,
            lines,
            shortages,
            log,
            overstaffSlots);
    }


    private static double ComputeRestHoursBetween(ShiftInput dayShift, ShiftInput nextDayShift)
    {
        var baseDate = new DateOnly(2026, 1, 1);
        var end1 = (dayShift.IsNight || dayShift.EndTime <= dayShift.StartTime ? baseDate.AddDays(1) : baseDate)
            .ToDateTime(dayShift.EndTime);
        var start2 = baseDate.AddDays(1).ToDateTime(nextDayShift.StartTime);
        return (start2 - end1).TotalHours;
    }

    private static string EvaluateSlotPreference(
        IReadOnlyList<PreferenceInput> prefs, string empId, DateOnly date, string? chosenShift,
        ref int total, ref int satisfied)
    {
        string label = "";
        foreach (var p in prefs)
        {
            if (p.EmployeeId != empId || p.PreferenceType == "Unavailable") continue;
            if (p.FromDate is { } f && date < f) continue;
            if (p.ToDate is { } t && date > t) continue;
            if (p.DaysOfWeek is { Count: > 0 } dows && !dows.Contains(date.DayOfWeek)) continue;

            total++;
            bool matchedShift = p.ShiftCode is null ? chosenShift is not null : string.Equals(chosenShift, p.ShiftCode, StringComparison.Ordinal);
            if (p.PreferenceType == "Prefer")
            {
                if (matchedShift) { satisfied++; label = "Prefer"; }
            }
            else if (p.PreferenceType == "Avoid")
            {
                if (!matchedShift) satisfied++;
                else label = "Avoid";
            }
        }
        return label;
    }
}

/// <summary>Orchestrates loading DocFrame records, calling WsmOptimizerEngine, and applying proposals to EmployeeDailySchedule.</summary>
public static class WsmOptimizationService
{
    public static IReadOnlySet<DayOfWeek>? ParseDaysOfWeek(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = new HashSet<DayOfWeek>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.ToLowerInvariant();
            if (p.StartsWith("mon") || p == "senin") set.Add(DayOfWeek.Monday);
            else if (p.StartsWith("tue") || p == "selasa") set.Add(DayOfWeek.Tuesday);
            else if (p.StartsWith("wed") || p == "rabu") set.Add(DayOfWeek.Wednesday);
            else if (p.StartsWith("thu") || p == "kamis") set.Add(DayOfWeek.Thursday);
            else if (p.StartsWith("fri") || p == "jumat") set.Add(DayOfWeek.Friday);
            else if (p.StartsWith("sat") || p == "sabtu") set.Add(DayOfWeek.Saturday);
            else if (p.StartsWith("sun") || p == "minggu") set.Add(DayOfWeek.Sunday);
        }
        return set.Count > 0 ? set : null;
    }

    public static async Task<WsmOptimizerEngine.OptimizerResult> RunAsync(
        IDocumentStore store, string tenant, Document optDoc, CancellationToken ct)
    {
        var m = new Masters(store, tenant);
        var d = optDoc.Data;
        var company = d.Str("company") ?? throw Errors.One("company", "Company is required");
        var fromDate = d.Date("from_date") ?? throw Errors.One("from_date", "From date is required");
        var toDate = d.Date("to_date") ?? throw Errors.One("to_date", "To date is required");
        var orgUnit = d.Str("organization_unit");
        var shiftGroup = d.Str("shift_group");

        // 1. Target employees
        List<Document> targetEmployees;
        if (!string.IsNullOrEmpty(orgUnit))
        {
            targetEmployees = (await Hr.EmployeesInSubtreeAsOfAsync(m, orgUnit, fromDate, ct))
                .Where(e => e.Data.Str("company") == company).ToList();
        }
        else
        {
            targetEmployees = (await m.QueryAsync("Employee", [new FilterClause("company", "=", company)], ct, take: 10_000))
                .Where(e => e.Data.Str("status") != Hr.Left).ToList();
        }

        var allGroups = (await m.QueryAsync("ShiftGroup", [new FilterClause("company", "=", company), new FilterClause("is_active", "=", true)], ct, take: 500)).ToList();
        if (!string.IsNullOrEmpty(shiftGroup))
        {
            var grp = allGroups.FirstOrDefault(g => g.Name == shiftGroup);
            var memberIds = grp?.Data.Rows("members").Where(r => r.Bool("is_active")).Select(r => r.Str("employee")).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            targetEmployees = targetEmployees.Where(e => memberIds.Contains(e.Name)).ToList();
        }

        var days = new List<DateOnly>();
        for (var cur = fromDate; cur <= toDate; cur = cur.AddDays(1)) days.Add(cur);

        // 2. Shift requirements & candidate shifts
        var allShifts = (await m.QueryAsync("ShiftType", [], ct, take: 500)).ToDictionary(s => s.Name, StringComparer.Ordinal);
        // Requirements of the run's unit and of its sub-units. A company-wide requirement (no unit) is only used
        // when the run covers the whole company, so a run for one unit is not asked to staff the whole company.
        HashSet<string>? unitsInScope = null;
        if (!string.IsNullOrEmpty(orgUnit))
        {
            var subUnits = await Hr.DescendantUnitsAsOfAsync(m, orgUnit, fromDate, ct);
            unitsInScope = subUnits.Select(u => u.Name)
                .Concat(subUnits.Select(u => u.Data.Str("base_unit_code")).OfType<string>())
                .Append(orgUnit)
                .ToHashSet(StringComparer.Ordinal);
        }
        var reqs = (await m.QueryAsync("ShiftRequirement", [new FilterClause("company", "=", company), new FilterClause("is_active", "=", true)], ct, take: 500))
            .Where(r => Hr.Overlaps(fromDate, toDate, r.Data.Date("effective_from"), r.Data.Date("effective_to")))
            .Where(r => unitsInScope is null || (r.Data.Str("organization_unit") is { } ru && unitsInScope.Contains(ru)))
            .ToList();

        var explicitCodes = (d.Str("candidate_shift_types") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        var candidateCodes = explicitCodes.Count > 0
            ? explicitCodes
            : reqs.Select(r => r.Data.Str("shift_type")).OfType<string>().ToHashSet(StringComparer.Ordinal);

        if (candidateCodes.Count == 0)
        {
            foreach (var s in allShifts.Values.Where(s => s.Data.Bool("is_active") || !s.Data.ContainsKey("is_active")))
                candidateCodes.Add(s.Name);
        }

        var shiftInputs = new List<WsmOptimizerEngine.ShiftInput>();
        void AddShift(string code, bool assignable)
        {
            if (shiftInputs.Any(x => x.Code == code) || !allShifts.TryGetValue(code, out var sd)) return;
            if (!TimeOnly.TryParse(sd.Data.Str("start_time"), CultureInfo.InvariantCulture, out var st)) st = new TimeOnly(8, 0);
            if (!TimeOnly.TryParse(sd.Data.Str("end_time"), CultureInfo.InvariantCulture, out var et)) et = new TimeOnly(17, 0);
            shiftInputs.Add(new WsmOptimizerEngine.ShiftInput(code, st, et, WsmTime.IsNightShift(sd), WsmTime.IsMorningShift(sd), assignable));
        }
        foreach (var code in candidateCodes.OrderBy(c => c, StringComparer.Ordinal)) AddShift(code, assignable: true);

        // 3. Demands per day & shift. Each requirement counts only employees of its unit (and sub-units) and,
        //    when set, of its job profile. Requirements with the same scope add up.
        var empIds = targetEmployees.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var eligibleCache = new Dictionary<(string? Unit, string? Job), IReadOnlySet<string>?>();
        async Task<IReadOnlySet<string>?> EligibleAsync(string? unit, string? job)
        {
            if (eligibleCache.TryGetValue((unit, job), out var cached)) return cached;
            IReadOnlySet<string>? set = null;
            if (unit is not null || job is not null)
            {
                var pool = unit is null
                    ? targetEmployees
                    : (await Hr.EmployeesInSubtreeAsOfAsync(m, unit, fromDate, ct)).Where(e => empIds.Contains(e.Name)).ToList();
                set = pool.Where(e => job is null || e.Data.Str("job_profile") == job).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            }
            return eligibleCache[(unit, job)] = set;
        }

        var demands = new List<WsmOptimizerEngine.DemandInput>();
        foreach (var r in reqs)
        {
            var sc = r.Data.Str("shift_type");
            if (sc is null) continue;
            int minHc = Math.Max(0, (int)r.Data.Dec("min_headcount"));
            int tgtHc = Math.Max(minHc, (int)r.Data.Dec("target_headcount"));
            if (tgtHc == 0) continue;
            var reqUnit = r.Data.Str("organization_unit");
            var reqJob = r.Data.Str("job_profile");
            var eligible = await EligibleAsync(reqUnit, reqJob);
            var label = string.Join(" / ", new[] { reqUnit, reqJob }.OfType<string>());
            var dayScope = r.Data.Str("day_scope") ?? "All days";
            foreach (var day in days)
            {
                if (!Hr.Overlaps(day, day, r.Data.Date("effective_from"), r.Data.Date("effective_to"))) continue;
                var isWknd = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if (dayScope == "Weekdays" && isWknd) continue;
                if (dayScope == "Weekends" && !isWknd) continue;
                demands.Add(new WsmOptimizerEngine.DemandInput(day, sc, minHc, tgtHc, eligible, label.Length > 0 ? label : null));
            }
        }

        // 4. Existing schedules (baseline + pinned) & approved leaves
        var existingScheds = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("company", "=", company),
             new FilterClause("work_date", ">=", D.Iso(fromDate)),
             new FilterClause("work_date", "<=", D.Iso(toDate))], ct, take: 20_000))
            .Where(s => s.Data.Str("employee") is { } eid && empIds.Contains(eid))
            .ToList();

        var pinned = new List<WsmOptimizerEngine.PinnedSlot>();
        var baseline = new Dictionary<(string, DateOnly), string?>();
        foreach (var s in existingScheds)
        {
            var eid = s.Data.Str("employee")!;
            var wd = s.Data.Date("work_date");
            if (wd is null) continue;
            var sType = s.Data.Str("schedule_type") ?? "REST";
            var sh = sType == "WORK" ? s.Data.Str("shift_type") : null;
            baseline[(eid, wd.Value)] = sh;
            var src = s.Data.Str("source_type");
            if (s.Data.Bool("is_locked") || src is "Override" or "Shift Swap" or "Leave")
                pinned.Add(new WsmOptimizerEngine.PinnedSlot(eid, wd.Value, sType, sh));
        }

        var approvedLeaves = (await m.QueryAsync("LeaveApplication",
            [new FilterClause("workflow_state", "=", Approval.Approved),
             new FilterClause("from_date", "<=", D.Iso(toDate)),
             new FilterClause("to_date", ">=", D.Iso(fromDate))], ct, take: 5000))
            .Where(l => l.WorkflowState == Approval.Approved && l.Data.Str("employee") is { } eid && empIds.Contains(eid))
            .Where(l => Hr.Overlaps(fromDate, toDate, l.Data.Date("from_date"), l.Data.Date("to_date")))
            .ToList();
        foreach (var l in approvedLeaves)
        {
            var eid = l.Data.Str("employee")!;
            var lf = l.Data.Date("from_date")!.Value;
            var lt = l.Data.Date("to_date")!.Value;
            for (var day = lf < fromDate ? fromDate : lf; day <= (lt > toDate ? toDate : lt); day = day.AddDays(1))
                pinned.Add(new WsmOptimizerEngine.PinnedSlot(eid, day, "LEAVE", null));
        }

        // 5. Preferences
        var prefDocs = (await m.QueryAsync("ShiftPreference", [new FilterClause("status", "=", "Active")], ct, take: 5000))
            .Where(p => p.Data.Str("employee") is { } eid && empIds.Contains(eid))
            .ToList();
        var prefInputs = prefDocs.Select(p => new WsmOptimizerEngine.PreferenceInput(
            p.Data.Str("employee")!,
            p.Data.Str("preference_type") ?? "Prefer",
            p.Data.Str("shift_type"),
            p.Data.Date("from_date"),
            p.Data.Date("to_date"),
            ParseDaysOfWeek(p.Data.Str("days_of_week")),
            Math.Clamp((int)(p.Data.Dec("weight") == 0 ? 3 : p.Data.Dec("weight")), 1, 5)
        )).ToList();

        // 6. Work schedule rules (pick strictest active rule for company/unit)
        var rules = (await m.QueryAsync("WorkScheduleRule", [new FilterClause("is_active", "=", true)], ct, take: 100))
            .Where(r => (r.Data.Str("company") is null || r.Data.Str("company") == company)
                        && (r.Data.Str("organization_unit") is null || r.Data.Str("organization_unit") == orgUnit))
            .ToList();
        int maxConsec = rules.Count > 0 ? rules.Min(r => Math.Max(1, (int)r.Data.Dec("max_consecutive_work_days"))) : 6;
        double minRest = rules.Count > 0 ? rules.Max(r => (double)r.Data.Dec("min_rest_hours_between_shifts")) : 11.0;
        bool forbidN2M = rules.Count == 0 || rules.Any(r => r.Data.Bool("forbid_night_to_morning"));
        int maxPer7 = rules.Select(r => (int)r.Data.Dec("max_work_days_per_7_days")).Where(v => v is > 0 and < 7).DefaultIfEmpty(7).Min();

        // 7. Days just before the horizon, so the consecutive-day, 7-day and rest-hour rules hold across from_date.
        var lookback = Math.Max(maxConsec, 6);
        var history = new Dictionary<(string, DateOnly), string?>();
        var historyRows = await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("company", "=", company),
             new FilterClause("work_date", ">=", D.Iso(fromDate.AddDays(-lookback))),
             new FilterClause("work_date", "<", D.Iso(fromDate))], ct, take: 20_000);
        foreach (var h in historyRows)
        {
            if (h.Data.Str("employee") is not { } eid || !empIds.Contains(eid) || h.Data.Date("work_date") is not { } hd) continue;
            history[(eid, hd)] = h.Data.Str("schedule_type") == "WORK" ? h.Data.Str("shift_type") : null;
        }

        // Shifts of locked rows and of the days before the horizon are in the model too (not handed out),
        // so a locked working day is not mistaken for a day off.
        foreach (var code in pinned.Where(p => p.ScheduleType == "WORK").Select(p => p.ShiftCode).Concat(history.Values).OfType<string>())
            AddShift(code, assignable: false);

        var empInputs = targetEmployees.OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new WsmOptimizerEngine.EmployeeInput(
                e.Name, e.Data.Str("full_name") ?? e.Name,
                e.Data.Date("hire_date"), e.Data.Date("separation_date"),
                e.Data.Str("organization_unit"), shiftGroup))
            .ToList();

        var weights = new WsmOptimizerEngine.WeightInput(
            Coverage: (int)(d.ContainsKey("w_coverage") ? d.Dec("w_coverage") : 100),
            Preference: (int)(d.ContainsKey("w_preference") ? d.Dec("w_preference") : 10),
            Fairness: (int)(d.ContainsKey("w_fairness") ? d.Dec("w_fairness") : 20),
            NightBalance: (int)(d.ContainsKey("w_night_balance") ? d.Dec("w_night_balance") : 15),
            Stability: (int)(d.ContainsKey("w_stability") ? d.Dec("w_stability") : 3),
            TimeLimitSeconds: Math.Clamp((int)(d.Dec("time_limit_seconds") == 0 ? 20 : d.Dec("time_limit_seconds")), 1, 120),
            Overstaff: (int)(d.ContainsKey("w_overstaff") ? d.Dec("w_overstaff") : 60));

        var result = WsmOptimizerEngine.Solve(new WsmOptimizerEngine.OptimizerInput(
            empInputs, days, shiftInputs, demands, pinned, prefInputs, baseline,
            new WsmOptimizerEngine.RuleInput(maxConsec, minRest, forbidN2M, maxPer7), weights, history));

        // Persist results back onto ScheduleOptimization document
        var lineRows = result.Lines.Select(l => new Dictionary<string, object?>
        {
            ["employee"] = l.EmployeeId,
            ["employee_name"] = l.EmployeeName,
            ["work_date"] = D.Iso(l.WorkDate),
            ["schedule_type"] = l.ScheduleType,
            ["shift_type"] = l.ShiftCode,
            ["previous_shift_type"] = l.PreviousShiftCode,
            ["is_changed"] = l.IsChanged,
            ["is_pinned"] = l.IsPinned,
            ["preference_hit"] = l.PreferenceHit,
        }).ToList();

        var clone = optDoc.Clone();
        clone.Data["status"] = result.IsSuccess ? "Proposed" : "Failed";
        clone.Data["solver_status"] = result.SolverStatus;
        clone.Data["coverage_pct"] = (decimal)result.CoveragePct;
        clone.Data["preference_satisfaction_pct"] = (decimal)result.PreferenceSatisfactionPct;
        clone.Data["shortage_slots"] = (long)result.ShortageSlots;
        clone.Data["overstaff_slots"] = (long)result.OverstaffSlots;
        clone.Data["changed_rows"] = (long)result.ChangedRows;
        clone.Data["objective_value"] = (decimal)Math.Round(result.ObjectiveValue, 2);
        clone.Data["solve_ms"] = result.SolveMs;
        clone.Data["summary_log"] = result.Log;
        clone.Data["lines"] = lineRows;
        clone.Version = optDoc.Version + 1;
        clone.Modified = DateTimeOffset.UtcNow;

        var uow = new UnitOfWork();
        uow.Upserts.Add((clone, optDoc.Version));
        await store.CommitAsync(uow, ct);
        return result;
    }

    /// <summary>
    /// Writes a Proposed optimization to EmployeeDailySchedule. Rows are only written when they change. Protected
    /// days are skipped: pinned lines, locked rows and rows from overrides, swaps or leave, days covered by leave
    /// approved after the proposal, and months whose payslip is already submitted.
    /// </summary>
    public static async Task<(int Applied, int Skipped, int Warnings)> ApplyAsync(
        IDocumentStore store, string tenant, Document optDoc, CancellationToken ct)
    {
        if (optDoc.Data.Str("status") != "Proposed")
            throw Errors.One("status", "Only a Proposed optimization can be applied");

        var m = new Masters(store, tenant);
        var company = optDoc.Data.Str("company") ?? "";
        var fromDate = optDoc.Data.Date("from_date")!.Value;
        var toDate = optDoc.Data.Date("to_date")!.Value;
        var lines = optDoc.Data.Rows("lines");
        var shifts = (await m.QueryAsync("ShiftType", [], ct, take: 500)).ToDictionary(s => s.Name, StringComparer.Ordinal);
        var emps = (await m.QueryAsync("Employee", [new FilterClause("company", "=", company)], ct, take: 10_000)).ToDictionary(e => e.Name, StringComparer.Ordinal);
        var existingRows = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("company", "=", company),
             new FilterClause("work_date", ">=", D.Iso(fromDate)),
             new FilterClause("work_date", "<=", D.Iso(toDate))], ct, take: 20_000))
            .Where(s => s.Data.Str("employee") is not null && s.Data.Date("work_date") is not null)
            .GroupBy(s => (s.Data.Str("employee")!, s.Data.Date("work_date")!.Value))
            .ToDictionary(g => g.Key, g => g.First());

        // Leave approved after the proposal was made.
        var onLeave = new HashSet<(string, DateOnly)>();
        foreach (var l in await m.QueryAsync("LeaveApplication",
            [new FilterClause("workflow_state", "=", Approval.Approved),
             new FilterClause("from_date", "<=", D.Iso(toDate)),
             new FilterClause("to_date", ">=", D.Iso(fromDate))], ct, take: 5000))
        {
            if (l.Data.Str("employee") is not { } eid || l.Data.Date("from_date") is not { } lf || l.Data.Date("to_date") is not { } lt) continue;
            for (var day = lf < fromDate ? fromDate : lf; day <= (lt > toDate ? toDate : lt); day = day.AddDays(1)) onLeave.Add((eid, day));
        }
        var paidMonths = new Dictionary<(string, string), bool>();

        int applied = 0, skipped = 0, warnings = 0, unchanged = 0, paid = 0;
        var uow = new UnitOfWork();
        var now = DateTimeOffset.UtcNow;

        foreach (var grp in lines.GroupBy(r => r.Str("employee") ?? "", StringComparer.Ordinal))
        {
            if (!emps.TryGetValue(grp.Key, out var emp)) continue;
            var proposals = new List<WsmConstraintEngine.ProposedSlot>();
            foreach (var r in grp)
            {
                var wd = r.Date("work_date");
                if (wd is null) continue;

                existingRows.TryGetValue((emp.Name, wd.Value), out var existing);
                if (r.Bool("is_pinned")
                    || existing is not null && (existing.Data.Bool("is_locked") || existing.Data.Str("source_type") is "Override" or "Shift Swap" or "Leave")
                    || onLeave.Contains((emp.Name, wd.Value)))
                {
                    skipped++;
                    continue;
                }

                var period = $"{wd.Value:yyyy-MM}";
                if (!paidMonths.TryGetValue((emp.Name, period), out var isPaid))
                    paidMonths[(emp.Name, period)] = isPaid = await Overtime.SubmittedPayslipAsync(m, emp.Name, period, ct) is not null;
                if (isPaid)
                {
                    skipped++;
                    paid++;
                    continue;
                }

                var schedType = r.Str("schedule_type") ?? "REST";
                var shiftCode = schedType == "WORK" ? r.Str("shift_type") : null;
                proposals.Add(new WsmConstraintEngine.ProposedSlot(wd.Value, schedType, shiftCode));

                if (existing is not null
                    && (existing.Data.Str("schedule_type") ?? "REST") == schedType
                    && string.Equals(existing.Data.Str("shift_type"), shiftCode, StringComparison.Ordinal))
                {
                    unchanged++; // same day as today: keep the row (and its source) as it is
                    continue;
                }

                shifts.TryGetValue(shiftCode ?? "", out var shiftDoc);
                var (startUtc, endUtc, crosses) = schedType == "WORK" ? WsmTime.ComputeWindow(wd.Value, shiftDoc) : (null, null, false);
                var dayCat = schedType switch
                {
                    "WORK" => "Scheduled Workday",
                    "LEAVE" => "Annual Leave",
                    "HOLIDAY" => "Public Holiday",
                    _ => "Scheduled Rest Day",
                };

                var data = new Dictionary<string, object?>
                {
                    ["employee"] = emp.Name,
                    ["employee_name"] = emp.Data.Str("full_name"),
                    ["company"] = company,
                    ["organization_unit"] = emp.Data.Str("organization_unit"),
                    ["shift_group"] = optDoc.Data.Str("shift_group"),
                    ["work_date"] = D.Iso(wd.Value),
                    ["schedule_type"] = schedType,
                    ["day_category"] = dayCat,
                    ["shift_type"] = shiftCode,
                    ["is_public_holiday"] = schedType == "HOLIDAY",
                    ["scheduled_start_datetime"] = startUtc,
                    ["scheduled_end_datetime"] = endUtc,
                    ["crosses_midnight"] = crosses,
                    ["source_type"] = "Optimizer",
                    ["source_reference"] = optDoc.Name,
                    ["is_locked"] = false,
                };

                if (existing is not null)
                {
                    var clone = existing.Clone();
                    foreach (var (k, v) in data) clone.Data[k] = v;
                    clone.Version = existing.Version + 1;
                    clone.Modified = now;
                    clone.ModifiedBy = "system";
                    uow.Upserts.Add((clone, existing.Version));
                }
                else
                {
                    var docName = await store.NextNameAsync(tenant, "EmployeeDailySchedule", "EDS-{YYYY}-{#######}", now, ct);
                    uow.Upserts.Add((new Document
                    {
                        TenantId = tenant,
                        DocType = "EmployeeDailySchedule",
                        Name = docName,
                        Owner = "system",
                        ModifiedBy = "system",
                        Created = now,
                        Modified = now,
                        Version = 1,
                        SchemaVersion = 1,
                        DocStatus = DocStatus.Draft,
                        Data = data,
                    }, null));
                }
                applied++;
            }

            if (proposals.Count > 0)
            {
                var viols = await WsmConstraintEngine.EvaluateAsync(m, emp, proposals, ct);
                warnings += viols.Count;
            }
        }

        var optClone = optDoc.Clone();
        optClone.Data["status"] = "Applied";
        optClone.Data["summary_log"] = (optDoc.Data.Str("summary_log") ?? "") +
            $"\nApplied {applied} schedule slot(s); {unchanged} already as proposed; {skipped} protected slot(s) skipped" +
            (paid > 0 ? $" ({paid} in months with a submitted payslip)" : "") + $"; {warnings} constraint check warning(s).";
        optClone.Version = optDoc.Version + 1;
        optClone.Modified = now;
        uow.Upserts.Add((optClone, optDoc.Version));

        await store.CommitAsync(uow, ct);
        return (applied, skipped, warnings);
    }

    public static async Task DiscardAsync(IDocumentStore store, Document optDoc, CancellationToken ct)
    {
        var clone = optDoc.Clone();
        clone.Data["status"] = "Discarded";
        clone.Version = optDoc.Version + 1;
        clone.Modified = DateTimeOffset.UtcNow;
        var uow = new UnitOfWork();
        uow.Upserts.Add((clone, optDoc.Version));
        await store.CommitAsync(uow, ct);
    }
}

/// <summary>Shift Preference validation hook.</summary>
public sealed class ShiftPreferenceHook(IDocumentStore store) : IBeforeValidate<ShiftPreference>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var from = d.Date("from_date");
        var to = d.Date("to_date");
        if (from is not null && to is not null && to < from)
            throw Errors.One("to_date", "The end date must be on or after the start date");
        var w = (int)d.Dec("weight");
        if (w < 1 || w > 5) d["weight"] = 3L;
    }
}

/// <summary>Schedule Optimization validation hook.</summary>
public sealed class ScheduleOptimizationHook : IBeforeValidate<ScheduleOptimization>
{
    public const int MaxOptimizationDays = 62;

    public Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var from = d.Date("from_date");
        var to = d.Date("to_date");
        if (from is null || to is null) return Task.CompletedTask;
        if (to < from)
            throw Errors.One("to_date", "The end date must be on or after the start date");
        var span = to.Value.DayNumber - from.Value.DayNumber + 1;
        if (span > MaxOptimizationDays)
            throw Errors.One("to_date", $"A single optimization run cannot exceed {MaxOptimizationDays} days (requested {span} days)");
        return Task.CompletedTask;
    }
}

/// <summary>Form action to run the OR-Tools CP-SAT optimizer on a ScheduleOptimization document.</summary>
public sealed class ScheduleOptimizationRunAction(IDocumentStore store) : IFormAction<ScheduleOptimization>
{
    public string ActionId => "erp.wsm.run_optimizer";
    public string Label => "Run Optimizer (CP-SAT)";
    public string Icon => "sparkles";
    public string Tone => "primary";

    public bool IsVisible(UserContext user, Document doc) =>
        (user.HasRole("HR") || user.HasRole("Manager") || user.IsAdmin)
        && doc.Data.Str("status") is null or "Draft" or "Proposed" or "Failed";

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var res = await WsmOptimizationService.RunAsync(store, ctx.Doc.TenantId, ctx.Doc, ct);
        return FormActionResult.Ok(res.Log);
    }
}

/// <summary>Form action to apply a Proposed ScheduleOptimization to EmployeeDailySchedule.</summary>
public sealed class ScheduleOptimizationApplyAction(IDocumentStore store) : IFormAction<ScheduleOptimization>
{
    public string ActionId => "erp.wsm.apply_optimization";
    public string Label => "Apply Proposal";
    public string Icon => "check-circle";
    public string Tone => "primary";

    public bool IsVisible(UserContext user, Document doc) =>
        (user.HasRole("HR") || user.IsAdmin)
        && doc.Data.Str("status") == "Proposed";

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var (applied, skipped, warnings) = await WsmOptimizationService.ApplyAsync(store, ctx.Doc.TenantId, ctx.Doc, ct);
        return FormActionResult.Ok($"Applied {applied} schedule slot(s); {skipped} protected slot(s) skipped ({warnings} warning(s)).");
    }
}

/// <summary>Form action to discard a Proposed ScheduleOptimization.</summary>
public sealed class ScheduleOptimizationDiscardAction(IDocumentStore store) : IFormAction<ScheduleOptimization>
{
    public string ActionId => "erp.wsm.discard_optimization";
    public string Label => "Discard Proposal";
    public string Icon => "x-circle";
    public string Tone => "danger";

    public bool IsVisible(UserContext user, Document doc) =>
        (user.HasRole("HR") || user.HasRole("Manager") || user.IsAdmin)
        && doc.Data.Str("status") == "Proposed";

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        await WsmOptimizationService.DiscardAsync(store, ctx.Doc, ct);
        return FormActionResult.Ok("Optimization proposal discarded.");
    }
}
