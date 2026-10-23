namespace GuitarPractice.Domain.Planning;

/// <summary>
/// "Reshuffle from today": plans the rest of a week that has already started.
///
/// Days before <c>firstOpenDay</c> are copied from the current plan unchanged. The open days get the routine's
/// budgets and pinned items as usual, keep the entries listed in <c>keep</c> (items already practised there) and
/// share out their remaining flexible time so the <b>whole week</b> catches up to the weights: each task's weekly
/// share is computed over the kept time plus the open time, what the kept entries already gave it is subtracted,
/// and the open time goes to the tasks still behind: furthest behind first, each a full minimum session, while
/// there is room; leftover time by what they still miss. A task added mid-week (a new song) therefore gets more of
/// the remaining days than one that already had its share, and tasks that don't fit are skipped (with a warning)
/// rather than cut below their minimum. Deterministic per <see cref="PlanRequest.Seed"/>.
/// </summary>
public static partial class WeeklyPlanner
{
    /// <param name="request">The routine for the whole week (WeekStart, budgets, tasks, seed).</param>
    /// <param name="current">The week as it stands (all seven days, or at least the days before <paramref name="firstOpenDay"/>).</param>
    /// <param name="firstOpenDay">First day to plan again; earlier days are kept as they are.</param>
    /// <param name="keep">Entries on open days that stay where they are, with their minutes (date, task).</param>
    /// <exception cref="PlanValidationException">When <see cref="Validate"/> reports errors.</exception>
    public static PlanResult Replan(
        PlanRequest request, IReadOnlyList<PlannedDay> current, DateOnly firstOpenDay,
        IReadOnlyCollection<(DateOnly Date, int TaskId)> keep)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(keep);
        var errors = Validate(request);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var opt = request.Options;
        var q = opt.QuantumMinutes;
        var rng = new Random(request.Seed);
        var warnings = new List<PlanWarning>();
        var currentByDate = current.ToDictionary(d => d.Date);
        var keepSet = keep.ToHashSet();

        var pinnedFirst = request.Tasks.Where(t => t.Pin == PinMode.DailyFirst)
            .OrderBy(t => t.CategorySortOrder).ThenBy(t => t.TaskId).ToList();
        var pinnedLast = request.Tasks.Where(t => t.Pin == PinMode.DailyLast)
            .OrderBy(t => t.CategorySortOrder).ThenBy(t => t.TaskId).ToList();
        var pinnedPerDay = PinnedMinutesPerDay(request.Tasks);
        var flex = request.Tasks.Where(t => !t.IsPinned && t.Weight > 0).OrderBy(t => t.TaskId).ToList();
        var flexIndex = flex.Select((t, i) => (t.TaskId, i)).ToDictionary(x => x.TaskId, x => x.i);
        var sortOrderByCategory = request.Tasks.GroupBy(t => t.CategoryId).ToDictionary(g => g.Key, g => g.First().CategorySortOrder);

        // ---- What stays: whole days before firstOpenDay, kept entries on open days -------------------------
        var dates = new DateOnly[7];
        var open = new bool[7];
        var budget = new int[7];
        var freeMinutes = new int[7];
        var kept = new List<PlannedEntry>[7];
        var givenMinutes = new int[flex.Count];
        for (var d = 0; d < 7; d++)
        {
            dates[d] = request.WeekStart.AddDays(d);
            open[d] = dates[d] >= firstOpenDay;
            var day = currentByDate.GetValueOrDefault(dates[d]);
            kept[d] = open[d]
                ? (day?.Entries ?? []).Where(e => !e.IsPinned && keepSet.Contains((dates[d], e.TaskId))).ToList()
                : (day?.Entries ?? []).Where(e => !e.IsPinned).ToList();
            foreach (var e in kept[d])
                if (flexIndex.TryGetValue(e.TaskId, out var i)) givenMinutes[i] += e.Minutes;

            if (!open[d]) continue;
            budget[d] = request.DayBudgets.GetValueOrDefault(dates[d].DayOfWeek);
            var (_, flexMinutes) = SplitDay(budget[d], pinnedPerDay, opt.PinnedTimeOnTopOfBudget);
            freeMinutes[d] = Math.Max(0, flexMinutes - kept[d].Sum(e => e.Minutes));
        }

        // ---- Catch-up allocation of the open time ----------------------------------------------------------
        var cells = new int[flex.Count, 7];
        var dayUnits = freeMinutes.Select(m => m / q).ToArray();
        var openUnits = dayUnits.Sum();
        if (flex.Count > 0 && openUnits > 0)
        {
            var weights = flex.Select(t => t.Weight).ToArray();
            var givenUnits = givenMinutes.Select(m => m / q).ToArray();
            var share = AllocateUnits(weights, openUnits + givenUnits.Sum(), flex.Select(_ => rng.Next()).ToArray());
            var deficit = share.Select((s, i) => Math.Max(0, s - givenUnits[i])).ToArray();
            var tie = flex.Select(_ => rng.Next()).ToArray();
            var minUnits = flex.Select(t => Math.Max(1, CeilDiv(t.MinSessionMinutes, q))).ToArray();
            var units = new int[flex.Count];

            // Furthest behind first, each with a full minimum session, while the days left have room. A big
            // routine can't fit everything into a few days; better fewer real sessions than many cut-down ones.
            var left = openUnits;
            var skipped = 0;
            foreach (var i in Enumerable.Range(0, flex.Count).Where(i => deficit[i] > 0)
                         .OrderByDescending(i => deficit[i]).ThenByDescending(i => weights[i]).ThenBy(i => tie[i]))
            {
                if (minUnits[i] > left) { skipped++; continue; }
                units[i] = minUnits[i];
                left -= minUnits[i];
            }
            if (skipped > 0)
                warnings.Add(new(PlanWarningCode.InsufficientTimeForMinimums,
                    $"The days left have {openUnits * q} flexible minutes: {skipped} item{(skipped == 1 ? "" : "s")} still behind " +
                    "this week don't fit and get no session."));

            // The rest goes to the chosen items by what they still miss (else by weight).
            if (left > 0)
            {
                var pool = Enumerable.Range(0, flex.Count).Where(i => units[i] > 0).ToList();
                if (pool.Count == 0) pool = [.. Enumerable.Range(0, flex.Count)];
                var extraWeights = pool.Select(i => Math.Max(0, deficit[i] - units[i])).ToArray();
                if (extraWeights.Sum() == 0) extraWeights = [.. pool.Select(i => weights[i])];
                var extra = AllocateUnits(extraWeights, left, [.. pool.Select(i => tie[i])]);
                for (var k = 0; k < pool.Count; k++) units[pool[k]] += extra[k];
            }

            PlaceUnits(flex, units, dayUnits, q, opt.MaxTasksPerDay, rng, cells, warnings);
        }

        // Sub-quantum leftovers: to the day's largest placed (else kept) item.
        for (var d = 0; d < 7; d++)
        {
            var leftover = freeMinutes[d] % q;
            if (leftover == 0) continue;
            var target = -1;
            for (var i = 0; i < flex.Count; i++)
                if (cells[i, d] > 0 && (target < 0 || cells[i, d] > cells[target, d])) target = i;
            if (target >= 0) cells[target, d] += leftover;
            else if (kept[d].Count > 0)
            {
                var largest = kept[d].OrderByDescending(e => e.Minutes).ThenBy(e => e.TaskId).First();
                kept[d][kept[d].IndexOf(largest)] = largest with { Minutes = largest.Minutes + leftover };
            }
        }

        // ---- Assemble ---------------------------------------------------------------------------------------
        var days = new List<PlannedDay>(7);
        for (var d = 0; d < 7; d++)
        {
            if (!open[d])
            {
                days.Add(currentByDate.GetValueOrDefault(dates[d]) ?? new PlannedDay(dates[d], 0, 0, 0, []));
                continue;
            }

            // One entry per task and day: a kept item that also gets new time grows instead of appearing twice.
            var flexEntries = kept[d].ToDictionary(e => e.TaskId, e => (e.CategoryId, Minutes: e.Minutes));
            for (var i = 0; i < flex.Count; i++)
            {
                if (cells[i, d] == 0) continue;
                flexEntries[flex[i].TaskId] = (flex[i].CategoryId, flexEntries.GetValueOrDefault(flex[i].TaskId).Minutes + cells[i, d]);
            }

            var entries = new List<PlannedEntry>();
            if (budget[d] > 0)
                foreach (var t in pinnedFirst) entries.Add(new(t.TaskId, t.CategoryId, 0, t.EffectivePinnedMinutes, t.Pin));
            var dayFlex = flexEntries
                .OrderBy(x => sortOrderByCategory.GetValueOrDefault(x.Value.CategoryId))
                .ThenByDescending(x => x.Value.Minutes)
                .ThenBy(x => x.Key)
                .ToList();
            if (dayFlex.Count > opt.MaxTasksPerDay)
                warnings.Add(new(PlanWarningCode.TooManyTasksOnDay,
                    $"{dates[d].DayOfWeek} has {dayFlex.Count} tasks (max {opt.MaxTasksPerDay}).", dates[d]));
            foreach (var (taskId, (categoryId, minutes)) in dayFlex)
                entries.Add(new(taskId, categoryId, 0, minutes, PinMode.None));
            if (budget[d] > 0)
                foreach (var t in pinnedLast) entries.Add(new(t.TaskId, t.CategoryId, 0, t.EffectivePinnedMinutes, t.Pin));

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
}
