namespace GuitarPractice.Domain.Planning;

/// <summary>
/// Pure, deterministic weekly planner: <c>Generate(request)</c> always returns the same plan for the same request.
///
/// Pipeline:
///  1. Split each day into pinned minutes and flexible minutes (budget mode decides whether pinned time is
///     carved out of the budget or added on top). Rest days (budget 0) get nothing.
///  2. Allocate the week's flexible units (minutes / quantum) to tasks by weight using largest remainder
///     (Hamilton) in exact integer arithmetic, then rebalance so every task reaches its weekly minimum.
///  3. Choose a session count per task (respecting MaxSessionMinutes) and pick spaced preferred days
///     (alternate picking 3x/week lands Mon/Wed/Fri-ish, not Mon/Tue/Wed).
///  4. Solve a convex min-cost transportation problem (tasks → days) that fills every day exactly and
///     balances session sizes, using preferred days unless capacity forces otherwise.
///  5. Assign sub-quantum leftover minutes, then order each day: pinned-first, flexible tasks by category
///     sort order, pinned-last.
/// </summary>
public static class WeeklyPlanner
{
    // Cost-model tuning. Costs per unit: the convex term is d/dk (k - target)^2 = 2k - 1 - 2*target.
    private const double FirstUnitOnPreferredDayBonus = 100;
    private const double NonPreferredDayPenalty = 50;
    private const double OverMaxSessionPenalty = 20;

    public static IReadOnlyList<string> Validate(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var opt = request.Options;

        if (opt.QuantumMinutes <= 0) errors.Add("Quantum must be at least 1 minute.");
        if (opt.MaxTasksPerDay <= 0) errors.Add("Max tasks per day must be at least 1.");

        foreach (var (day, minutes) in request.DayBudgets)
            if (minutes < 0) errors.Add($"{day}: daily minutes cannot be negative.");

        var duplicate = request.Tasks.GroupBy(t => t.TaskId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) errors.Add($"Task {duplicate.Key} is selected more than once.");

        foreach (var t in request.Tasks)
        {
            if (t.Weight < 0) errors.Add($"Task {t.TaskId}: weight cannot be negative.");
            if (t.MinSessionMinutes <= 0) errors.Add($"Task {t.TaskId}: minimum session must be at least 1 minute.");
            if (t.MaxSessionMinutes < t.MinSessionMinutes) errors.Add($"Task {t.TaskId}: max session is below min session.");
            if (t.IsPinned && t.EffectivePinnedMinutes <= 0) errors.Add($"Task {t.TaskId}: pinned minutes must be positive.");
        }

        if (!opt.PinnedTimeOnTopOfBudget)
        {
            var pinned = PinnedMinutesPerDay(request.Tasks);
            foreach (var (day, minutes) in request.DayBudgets.OrderBy(kv => kv.Key))
                if (minutes > 0 && pinned > minutes)
                    errors.Add($"{day}: pinned tasks need {pinned} min but the day budget is {minutes} min.");
        }

        return errors;
    }

    /// <exception cref="PlanValidationException">When <see cref="Validate"/> reports errors.</exception>
    public static PlanResult Generate(PlanRequest request)
    {
        var errors = Validate(request);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var opt = request.Options;
        var q = opt.QuantumMinutes;
        var rng = new Random(request.Seed);
        var warnings = new List<PlanWarning>();

        var pinnedFirst = request.Tasks.Where(t => t.Pin == PinMode.DailyFirst)
            .OrderBy(t => t.CategorySortOrder).ThenBy(t => t.TaskId).ToList();
        var pinnedLast = request.Tasks.Where(t => t.Pin == PinMode.DailyLast)
            .OrderBy(t => t.CategorySortOrder).ThenBy(t => t.TaskId).ToList();
        var pinnedPerDay = PinnedMinutesPerDay(request.Tasks);
        var flex = request.Tasks.Where(t => !t.IsPinned && t.Weight > 0).OrderBy(t => t.TaskId).ToList();

        // ---- 1. Day split -------------------------------------------------------------------------------
        var dates = new DateOnly[7];
        var budget = new int[7];
        var pinned = new int[7];
        var flexMinutes = new int[7];
        for (var d = 0; d < 7; d++)
        {
            dates[d] = request.WeekStart.AddDays(d);
            budget[d] = request.DayBudgets.GetValueOrDefault(dates[d].DayOfWeek);
            (pinned[d], flexMinutes[d]) = SplitDay(budget[d], pinnedPerDay, opt.PinnedTimeOnTopOfBudget);
        }

        var cells = new int[flex.Count, 7]; // minutes per (task, day)
        if (flex.Count == 0)
        {
            if (flexMinutes.Sum() > 0)
                warnings.Add(new(PlanWarningCode.NoFlexibleTasks,
                    "No weighted tasks are selected, so only pinned tasks were scheduled."));
        }
        else
        {
            var dayUnits = flexMinutes.Select(m => m / q).ToArray();
            var totalUnits = dayUnits.Sum();

            // ---- 2. Weekly allocation -----------------------------------------------------------------
            var tieBreak = flex.Select(_ => rng.Next()).ToArray();
            var units = AllocateUnits(flex.Select(t => t.Weight).ToArray(), totalUnits, tieBreak);
            EnforceWeeklyMinimums(flex, units, q, totalUnits, warnings);

            // ---- 3 + 4. Spaced placement and exact fill -----------------------------------------------
            if (totalUnits > 0)
                PlaceUnits(flex, units, dayUnits, q, opt.MaxTasksPerDay, rng, cells, warnings);

            // ---- 5. Sub-quantum leftovers -------------------------------------------------------------
            for (var d = 0; d < 7; d++)
            {
                var leftover = flexMinutes[d] % q;
                if (leftover == 0) continue;

                var target = -1;
                for (var i = 0; i < flex.Count; i++)
                    if (cells[i, d] > 0 && (target < 0 || cells[i, d] > cells[target, d])) target = i;

                if (target < 0) // day too short for even one quantum: give it to the heaviest task
                    target = Enumerable.Range(0, flex.Count)
                        .OrderByDescending(i => flex[i].Weight).ThenBy(i => flex[i].TaskId).First();

                cells[target, d] += leftover;
            }
        }

        // ---- Assemble -----------------------------------------------------------------------------------
        var days = new List<PlannedDay>(7);
        for (var d = 0; d < 7; d++)
        {
            var entries = new List<PlannedEntry>();
            if (budget[d] > 0)
            {
                foreach (var t in pinnedFirst) entries.Add(new(t.TaskId, t.CategoryId, 0, t.EffectivePinnedMinutes, t.Pin));

                var dayFlex = Enumerable.Range(0, flex.Count)
                    .Where(i => cells[i, d] > 0)
                    .OrderBy(i => flex[i].CategorySortOrder)
                    .ThenByDescending(i => cells[i, d])
                    .ThenBy(i => flex[i].TaskId)
                    .ToList();
                if (dayFlex.Count > opt.MaxTasksPerDay)
                    warnings.Add(new(PlanWarningCode.TooManyTasksOnDay,
                        $"{dates[d].DayOfWeek} has {dayFlex.Count} tasks (max {opt.MaxTasksPerDay}).", dates[d]));
                foreach (var i in dayFlex)
                    entries.Add(new(flex[i].TaskId, flex[i].CategoryId, 0, cells[i, d], PinMode.None));

                foreach (var t in pinnedLast) entries.Add(new(t.TaskId, t.CategoryId, 0, t.EffectivePinnedMinutes, t.Pin));
            }

            var ordered = entries.Select((e, idx) => e with { Order = idx }).ToList();
            days.Add(new PlannedDay(
                dates[d],
                budget[d],
                ordered.Where(e => e.IsPinned).Sum(e => e.Minutes),
                ordered.Where(e => !e.IsPinned).Sum(e => e.Minutes),
                ordered));
        }

        return new PlanResult(request.WeekStart, request.Seed, days, warnings);
    }

    internal static (int Pinned, int Flex) SplitDay(int budget, int pinnedPerDay, bool onTop)
    {
        if (budget <= 0) return (0, 0);                       // rest day: no warmup either
        if (onTop) return (pinnedPerDay, budget);              // warmup added; full budget is flexible
        return (pinnedPerDay, budget - pinnedPerDay);          // warmup carved out (validated >= 0)
    }

    private static int PinnedMinutesPerDay(IEnumerable<PlanTaskSpec> tasks) =>
        tasks.Where(t => t.IsPinned).Sum(t => t.EffectivePinnedMinutes);

    /// <summary>
    /// Largest-remainder (Hamilton) apportionment in exact integer arithmetic.
    /// Every result is floor(ideal) or ceil(ideal), where ideal = total * w / sum(w).
    /// </summary>
    internal static int[] AllocateUnits(IReadOnlyList<int> weights, int totalUnits, IReadOnlyList<int> tieBreak)
    {
        var n = weights.Count;
        var result = new int[n];
        long weightSum = weights.Sum(w => (long)w);
        if (n == 0 || totalUnits <= 0 || weightSum == 0) return result;

        var remainders = new long[n];
        var assigned = 0;
        for (var i = 0; i < n; i++)
        {
            var scaled = (long)totalUnits * weights[i];
            result[i] = (int)(scaled / weightSum);
            remainders[i] = scaled % weightSum;
            assigned += result[i];
        }

        foreach (var i in Enumerable.Range(0, n)
                     .OrderByDescending(i => remainders[i])
                     .ThenBy(i => tieBreak[i])
                     .ThenBy(i => i)
                     .Take(totalUnits - assigned))
            result[i]++;

        return result;
    }

    private static void EnforceWeeklyMinimums(
        List<PlanTaskSpec> flex, int[] units, int q, int totalUnits, List<PlanWarning> warnings)
    {
        var minUnits = flex.Select(t => Math.Max(1, CeilDiv(t.MinSessionMinutes, q))).ToArray();
        if (minUnits.Sum() > totalUnits)
        {
            warnings.Add(new(PlanWarningCode.InsufficientTimeForMinimums,
                $"The week has {totalUnits * q} flexible minutes but the selected tasks need at least " +
                $"{minUnits.Sum() * q}. Lower-weight tasks may be skipped; add time or remove tasks."));
            return;
        }

        var moved = false;
        for (var i = 0; i < flex.Count; i++)
        {
            while (units[i] < minUnits[i])
            {
                var donor = Enumerable.Range(0, flex.Count)
                    .Where(j => j != i && units[j] > minUnits[j])
                    .OrderByDescending(j => units[j] - minUnits[j])
                    .ThenByDescending(j => flex[j].Weight)
                    .ThenBy(j => j)
                    .First(); // exists because sum(min) <= total
                units[donor]--;
                units[i]++;
                moved = true;
            }
        }

        if (moved)
            warnings.Add(new(PlanWarningCode.MinimumsRebalanced,
                "Some time was moved from higher-weight tasks so every task gets its minimum session."));
    }

    private static void PlaceUnits(
        List<PlanTaskSpec> flex, int[] units, int[] dayUnits, int q, int maxTasksPerDay,
        Random rng, int[,] cells, List<PlanWarning> warnings)
    {
        var active = Enumerable.Range(0, 7).Where(d => dayUnits[d] > 0).ToArray();
        var a = active.Length;
        var taskCount = flex.Count;

        // ---- 3. Session counts + spaced preferred days ----
        var minUnits = flex.Select(t => Math.Max(1, CeilDiv(t.MinSessionMinutes, q))).ToArray();
        var maxUnits = flex.Select((t, i) => Math.Max(minUnits[i], t.MaxSessionMinutes / q)).ToArray();
        var sessions = new int[taskCount];
        var target = new double[taskCount];
        for (var i = 0; i < taskCount; i++)
        {
            if (units[i] == 0) continue;
            var needed = CeilDiv(units[i], maxUnits[i]);
            sessions[i] = Math.Clamp(needed, 1, Math.Min(a, units[i]));
            target[i] = (double)units[i] / sessions[i];
            if (needed > a)
                warnings.Add(new(PlanWarningCode.SessionExceedsMax,
                    $"Task {flex[i].TaskId} needs {units[i] * q} min across only {a} practice days, " +
                    $"so sessions will run longer than {flex[i].MaxSessionMinutes} min.", TaskId: flex[i].TaskId));
        }

        var preferred = new bool[taskCount, a];
        var load = new double[a];
        var count = new int[a];
        var order = Enumerable.Range(0, taskCount)
            .Select(i => (i, key: rng.Next()))
            .OrderByDescending(x => units[x.i]).ThenBy(x => x.key)
            .Select(x => x.i)
            .ToList();

        foreach (var i in order)
        {
            if (sessions[i] == 0) continue;
            var spacing = (double)a / sessions[i];
            var phase = rng.NextDouble() * spacing;
            for (var j = 0; j < sessions[i]; j++)
            {
                var ideal = phase + j * spacing;
                var best = -1;
                var bestScore = double.PositiveInfinity;
                for (var k = 0; k < a; k++)
                {
                    if (preferred[i, k]) continue;
                    var dist = Math.Abs(k - ideal);
                    dist = Math.Min(dist, a - dist); // circular distance across the week boundary
                    var overflow = Math.Max(0, load[k] + target[i] - dayUnits[active[k]]) / target[i];
                    var full = count[k] >= maxTasksPerDay ? 1000 : 0;
                    var score = dist + 1.5 * overflow + full;
                    if (score < bestScore - 1e-12) { bestScore = score; best = k; }
                }
                preferred[i, best] = true;
                load[best] += target[i];
                count[best]++;
            }
        }

        // ---- 4. Convex transportation: source -> task -> day -> sink ----
        var source = 0;
        var sink = 1 + taskCount + a;
        var net = new ConvexFlowNetwork(sink + 1);
        var cellEdge = new int[taskCount, a];

        for (var i = 0; i < taskCount; i++)
        {
            net.AddEdge(source, 1 + i, units[i]);
            for (var k = 0; k < a; k++)
            {
                var isPreferred = preferred[i, k];
                var t = target[i];
                var cap = maxUnits[i];
                cellEdge[i, k] = units[i] == 0
                    ? -1
                    : net.AddEdge(1 + i, 1 + taskCount + k, Math.Min(units[i], dayUnits[active[k]]), unit =>
                    {
                        var cost = 2.0 * unit - 1 - 2 * t;
                        if (unit > cap) cost += OverMaxSessionPenalty;
                        if (!isPreferred) cost += NonPreferredDayPenalty;
                        else if (unit == 1) cost -= FirstUnitOnPreferredDayBonus;
                        return cost;
                    });
            }
        }
        for (var k = 0; k < a; k++) net.AddEdge(1 + taskCount + k, sink, dayUnits[active[k]]);

        var total = units.Sum();
        var pushed = net.Run(source, sink, total);
        if (pushed != total)
            throw new InvalidOperationException($"Planner flow routed {pushed}/{total} units; this is a bug.");

        var grid = new int[taskCount, a];
        for (var i = 0; i < taskCount; i++)
            for (var k = 0; k < a; k++)
                if (cellEdge[i, k] >= 0)
                    grid[i, k] = net.Flow(cellEdge[i, k]);

        // ---- 4b. Session-size repair ----
        RepairSessionSizes(grid, active, minUnits, maxUnits, maxTasksPerDay);

        for (var i = 0; i < taskCount; i++)
            for (var k = 0; k < a; k++)
                cells[i, active[k]] = grid[i, k] * q;
    }

    /// <summary>
    /// Local search over cross swaps: task i moves x units from day d to day e while task j moves x units
    /// from e to d. Every day total and every task total is preserved, so the exactness invariants hold;
    /// only session sizes change. Applies the best strictly improving move until none is left.
    /// Objective, lexicographic: (violation, adjacency, sessions) where
    ///   violation = units below MinSession + units above MaxSession + tasks beyond MaxTasksPerDay,
    ///   adjacency = same-task sessions on consecutive calendar days (spacing),
    ///   sessions  = number of non-empty cells (fewer fragments).
    /// A move is accepted only if it lowers violation, or keeps it and lowers adjacency, so sizes get fixed
    /// first and spacing is polished without ever making sizes worse. Deterministic scan order.
    /// </summary>
    /// <param name="dayOfWeekIndex">Calendar index (0–6) of each grid column, for adjacency.</param>
    internal static void RepairSessionSizes(
        int[,] grid, int[] dayOfWeekIndex, int[] minUnits, int[] maxUnits, int maxTasksPerDay)
    {
        var tasks = grid.GetLength(0);
        var days = grid.GetLength(1);

        int Cell(int i, int v) => v == 0 ? 0 : Math.Max(0, minUnits[i] - v) + Math.Max(0, v - maxUnits[i]);
        int Crowd(int count) => Math.Max(0, count - maxTasksPerDay);
        static int Nz(int v) => v > 0 ? 1 : 0;

        int Adjacency(int i)
        {
            var n = 0;
            for (var a = 0; a < days; a++)
                for (var b = a + 1; b < days; b++)
                    if (grid[i, a] > 0 && grid[i, b] > 0 && Math.Abs(dayOfWeekIndex[a] - dayOfWeekIndex[b]) == 1)
                        n++;
            return n;
        }

        var dayCount = new int[days];
        for (var d = 0; d < days; d++)
            for (var i = 0; i < tasks; i++)
                dayCount[d] += Nz(grid[i, d]);

        // Each accepted move lowers (violation, adjacency) lexicographically, both non-negative integers,
        // so this terminates;
        // the cap is a belt-and-braces guard for pathological inputs.
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            (int i, int j, int d, int e, int x) best = default;
            var bestViolation = 0;
            var bestAdjacency = 0;
            var bestSessions = 0;

            for (var i = 0; i < tasks; i++)
            for (var d = 0; d < days; d++)
            {
                var gid = grid[i, d];
                if (gid == 0) continue;
                for (var e = 0; e < days; e++)
                {
                    if (e == d) continue;
                    var gie = grid[i, e];
                    for (var j = 0; j < tasks; j++)
                    {
                        var gje = grid[j, e];
                        if (j == i || gje == 0) continue;
                        var gjd = grid[j, d];

                        var before = Cell(i, gid) + Cell(i, gie) + Cell(j, gje) + Cell(j, gjd)
                                     + Crowd(dayCount[d]) + Crowd(dayCount[e]);
                        var sessionsBefore = Nz(gid) + Nz(gie) + Nz(gje) + Nz(gjd);
                        var adjacencyBefore = Adjacency(i) + Adjacency(j);

                        for (var x = 1; x <= Math.Min(gid, gje); x++)
                        {
                            int nid = gid - x, nie = gie + x, nje = gje - x, njd = gjd + x;
                            var countD = dayCount[d] - Nz(gid) + Nz(nid) - Nz(gjd) + Nz(njd);
                            var countE = dayCount[e] - Nz(gie) + Nz(nie) - Nz(gje) + Nz(nje);
                            var after = Cell(i, nid) + Cell(i, nie) + Cell(j, nje) + Cell(j, njd)
                                        + Crowd(countD) + Crowd(countE);
                            var dv = after - before;
                            if (dv > 0) continue;

                            grid[i, d] = nid; grid[i, e] = nie; grid[j, e] = nje; grid[j, d] = njd;
                            var da = Adjacency(i) + Adjacency(j) - adjacencyBefore;
                            grid[i, d] = gid; grid[i, e] = gie; grid[j, e] = gje; grid[j, d] = gjd;
                            if (dv == 0 && da >= 0) continue;

                            var ds = Nz(nid) + Nz(nie) + Nz(nje) + Nz(njd) - sessionsBefore;
                            if ((dv, da, ds).CompareTo((bestViolation, bestAdjacency, bestSessions)) < 0)
                            {
                                bestViolation = dv;
                                bestAdjacency = da;
                                bestSessions = ds;
                                best = (i, j, d, e, x);
                            }
                        }
                    }
                }
            }

            if (bestViolation == 0 && bestAdjacency == 0) return;

            var (bi, bj, bd, be, bx) = best;
            dayCount[bd] -= Nz(grid[bi, bd]) + Nz(grid[bj, bd]);
            dayCount[be] -= Nz(grid[bi, be]) + Nz(grid[bj, be]);
            grid[bi, bd] -= bx; grid[bi, be] += bx;
            grid[bj, be] -= bx; grid[bj, bd] += bx;
            dayCount[bd] += Nz(grid[bi, bd]) + Nz(grid[bj, bd]);
            dayCount[be] += Nz(grid[bi, be]) + Nz(grid[bj, be]);
        }
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
