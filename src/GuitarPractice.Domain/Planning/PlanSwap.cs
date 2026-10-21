namespace GuitarPractice.Domain.Planning;

/// <summary>Why two board items can't trade places (None = they can).</summary>
public enum SwapRefusal
{
    None,
    SameItem,
    NotFound,
    /// <summary>Warmups are pinned to the start of every day.</summary>
    Pinned,
    /// <summary>The exercise is already on the other day: a day holds each exercise once.</summary>
    AlreadyOnThatDay,
}

/// <summary>
/// Swapping two items of a stored week: the exercises trade places, each slot keeps its day, position and minutes.
/// "10 min Theory on Monday ↔ 15 min Legato on Tuesday" gives 10 min Legato on Monday and 15 min Theory on Tuesday,
/// so every day's total stays the same. An item is identified by (date, subcategory), like the practice log.
/// </summary>
public static class PlanSwap
{
    public static SwapRefusal Check(WeeklyPlan plan, (DateOnly Date, int SubcategoryId) a, (DateOnly Date, int SubcategoryId) b)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (a == b) return SwapRefusal.SameItem;
        if (Find(plan, a) is not { } first || Find(plan, b) is not { } second) return SwapRefusal.NotFound;
        if (first.Entry.IsPinned || second.Entry.IsPinned) return SwapRefusal.Pinned;
        if (a.SubcategoryId == b.SubcategoryId) return SwapRefusal.None; // same exercise on two days: nothing to collide
        if (a.Date != b.Date
            && (Has(second.Day, a.SubcategoryId) || Has(first.Day, b.SubcategoryId)))
            return SwapRefusal.AlreadyOnThatDay;
        return SwapRefusal.None;
    }

    /// <summary>Trades the two exercises in place (the caller saves). Throws if <see cref="Check"/> refuses.</summary>
    public static void Apply(WeeklyPlan plan, (DateOnly Date, int SubcategoryId) a, (DateOnly Date, int SubcategoryId) b)
    {
        var refusal = Check(plan, a, b);
        if (refusal != SwapRefusal.None) throw new InvalidOperationException($"Can't swap these items: {refusal}.");
        var first = Find(plan, a)!.Value.Entry;
        var second = Find(plan, b)!.Value.Entry;
        (first.SubcategoryId, second.SubcategoryId) = (second.SubcategoryId, first.SubcategoryId);
        // Navigation properties would otherwise still point at the old exercises until reloaded.
        (first.Subcategory, second.Subcategory) = (second.Subcategory, first.Subcategory);
    }

    private static (PlanDay Day, PlanEntry Entry)? Find(WeeklyPlan plan, (DateOnly Date, int SubcategoryId) key) =>
        plan.Days.Where(d => d.Date == key.Date)
            .SelectMany(d => d.Entries.Where(e => e.SubcategoryId == key.SubcategoryId).Select(e => ((PlanDay, PlanEntry)?)(d, e)))
            .FirstOrDefault();

    private static bool Has(PlanDay day, int subcategoryId) => day.Entries.Any(e => e.SubcategoryId == subcategoryId);
}
